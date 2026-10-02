using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;
using SeatReservation.Infrastructure.Repositories;
using SeatReservation.Infrastructure.Transactions;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Persistence;

/// <summary>
/// The reserve/cancel repositories' SQL against real Postgres (lld §6.3), with no HTTP involved. Blocking is asserted by
/// asking Postgres whether the second backend is waiting on a lock, not by timing, so slow runners can't make these flaky.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReservationRepositorySqlTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 3, 9, 30, 15, 123, 456, TimeSpan.Zero);

    // ---- helpers ----

    /// <summary>An open READ COMMITTED transaction with its unit of work; disposing it rolls back what wasn't committed.</summary>
    private sealed class OpenTx : IAsyncDisposable
    {
        private OpenTx(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            Connection = connection;
            Transaction = transaction;
            Uow = new PgUnitOfWork(connection, transaction);
        }

        public NpgsqlConnection Connection { get; }

        public NpgsqlTransaction Transaction { get; }

        public PgUnitOfWork Uow { get; }

        public static async Task<OpenTx> BeginAsync(MigratedDatabase db)
        {
            var connection = await db.Sources.Main.OpenConnectionAsync();
            return new OpenTx(connection, await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted));
        }

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private static async Task<Guid> NewShowAsync(MigratedDatabase db, params string[] labels)
    {
        var show = new ShowInfo(Guid.NewGuid(), "show", 25_000, 4, labels.Length);
        await new PgTransactionRunner(db.Sources, NullLogger<PgTransactionRunner>.Instance).RunAsync("create_show", async (uow, ct) =>
        {
            await uow.Shows.InsertShowWithSeatsAsync(show, labels, ct);
            return TxResult<bool>.CommitWith(true);
        }, CancellationToken.None);
        return show.Id;
    }

    private static Reservation NewReservation(Guid showId, string user, string key, params string[] seats) => Reservation.Confirmed(
        Guid.NewGuid(), showId, user, key, SHA256.HashData(Guid.NewGuid().ToByteArray()), seats, 25_000L * seats.Length, CreatedAt);

    /// <summary>Inserts the reservation and confirms its seats in one committed transaction, the way a reserve does.</summary>
    private static async Task ReserveAsync(MigratedDatabase db, Reservation r)
    {
        await using var tx = await OpenTx.BeginAsync(db);
        await tx.Uow.Reservations.InsertAsync(r, CancellationToken.None);
        Assert.Equal(r.Seats.Count, await tx.Uow.Seats.ConfirmAsync(r.ShowId, r.Seats, r.UserId, r.Id, CancellationToken.None));
        await tx.Transaction.CommitAsync();
    }

    /// <summary>Waits until Postgres reports the backend as blocked on a lock (row lock or advisory lock).</summary>
    private static async Task AssertWaitingOnLockAsync(MigratedDatabase db, int backendPid)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (await db.CountAsync($"SELECT count(*) FROM pg_stat_activity WHERE pid = {backendPid} AND wait_event_type = 'Lock'") == 1)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Backend {backendPid} never waited on a lock.");
    }

    // ---- ReservationRepository ----

    [Fact]
    public async Task Inserted_reservation_reads_back_field_for_field_by_key_and_by_id()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1", "A2");
        var r = NewReservation(showId, "alice", "key-1", "A1", "A2");

        await ReserveAsync(db, r);

        var reads = new ReservationReadRepository(db.Sources);
        AssertSame(r, await reads.FindByKeyAsync("alice", "key-1", CancellationToken.None));
        AssertSame(r, await reads.GetByIdAsync(r.Id, CancellationToken.None));
        await using var tx = await OpenTx.BeginAsync(db);
        AssertSame(r, await tx.Uow.Reservations.FindByKeyAsync("alice", "key-1", CancellationToken.None));
        AssertSame(r, await tx.Uow.Reservations.GetByIdForUpdateAsync(r.Id, CancellationToken.None));
    }

    private static void AssertSame(Reservation expected, Reservation? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected with { RequestHash = [], Seats = [] }, actual with { RequestHash = [], Seats = [] });
        Assert.Equal(expected.RequestHash, actual.RequestHash);
        Assert.Equal(expected.Seats, actual.Seats);
        Assert.Equal(TimeSpan.Zero, actual.CreatedAt.Offset);
    }

    [Fact]
    public async Task Lookups_for_an_unknown_key_or_id_return_null_and_the_key_is_per_user()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1");
        await ReserveAsync(db, NewReservation(showId, "alice", "key-1", "A1"));
        var reads = new ReservationReadRepository(db.Sources);

        Assert.Null(await reads.FindByKeyAsync("bob", "key-1", CancellationToken.None));
        Assert.Null(await reads.FindByKeyAsync("alice", "KEY-1", CancellationToken.None));
        Assert.Null(await reads.GetByIdAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task A_duplicate_user_and_key_insert_throws_the_application_exception()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1", "A2");
        await ReserveAsync(db, NewReservation(showId, "alice", "key-1", "A1"));

        await using var tx = await OpenTx.BeginAsync(db);
        var ex = await Assert.ThrowsAsync<DuplicateIdempotencyKeyException>(() =>
            tx.Uow.Reservations.InsertAsync(NewReservation(showId, "alice", "key-1", "A2"), CancellationToken.None));

        Assert.Equal(PgErrorClassifier.IdempotencyConstraint, Assert.IsType<PostgresException>(ex.InnerException).ConstraintName);
    }

    [Fact]
    public async Task Other_unique_violations_are_not_mistaken_for_an_idempotency_race()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1");
        var r = NewReservation(showId, "alice", "key-1", "A1");
        await ReserveAsync(db, r);

        await using var tx = await OpenTx.BeginAsync(db);
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            tx.Uow.Reservations.InsertAsync(r with { IdempotencyKey = "key-2" }, CancellationToken.None));   // same primary key

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task MarkCancelled_sets_status_and_time()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1");
        var r = NewReservation(showId, "alice", "key-1", "A1");
        await ReserveAsync(db, r);
        var at = CreatedAt.AddMinutes(5);

        await using (var tx = await OpenTx.BeginAsync(db))
        {
            await tx.Uow.Seats.ReleaseByReservationAsync(r.Id, CancellationToken.None);
            await tx.Uow.Reservations.MarkCancelledAsync(r.Id, at, CancellationToken.None);
            await tx.Transaction.CommitAsync();
        }

        var read = await new ReservationReadRepository(db.Sources).GetByIdAsync(r.Id, CancellationToken.None);
        Assert.Equal(ReservationStatus.Cancelled, read!.Status);
        Assert.Equal(at, read.CancelledAt);
    }

    [Fact]
    public async Task GetByIdForUpdate_blocks_a_second_transaction_until_commit()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1");
        var r = NewReservation(showId, "alice", "key-1", "A1");
        await ReserveAsync(db, r);

        await using var first = await OpenTx.BeginAsync(db);
        await first.Uow.Reservations.GetByIdForUpdateAsync(r.Id, CancellationToken.None);

        await using var second = await OpenTx.BeginAsync(db);
        var blocked = second.Uow.Reservations.GetByIdForUpdateAsync(r.Id, CancellationToken.None);
        await AssertWaitingOnLockAsync(db, second.Connection.ProcessID);
        Assert.False(blocked.IsCompleted);

        await first.Transaction.CommitAsync();
        Assert.NotNull(await blocked.WaitAsync(Patience));
    }

    // ---- SeatRepository ----

    [Fact]
    public async Task LockForUpdate_returns_the_requested_seats_with_their_status()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1", "A2", "A3");
        await ReserveAsync(db, NewReservation(showId, "bob", "k", "A2"));

        await using var tx = await OpenTx.BeginAsync(db);
        var locked = await tx.Uow.Seats.LockForUpdateAsync(showId, ["A1", "A2", "ZZ"], CancellationToken.None);

        Assert.Equal(
            [new LockedSeat("A1", SeatStatus.Available), new LockedSeat("A2", SeatStatus.Confirmed)],
            locked.OrderBy(s => s.Label, StringComparer.Ordinal));
    }

    [Fact]
    public async Task LockForUpdate_blocks_a_second_transaction_on_the_same_seat_until_commit()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1", "A2");

        await using var first = await OpenTx.BeginAsync(db);
        await first.Uow.Seats.LockForUpdateAsync(showId, ["A1"], CancellationToken.None);
        var r = NewReservation(showId, "alice", "key-1", "A1");
        await first.Uow.Reservations.InsertAsync(r, CancellationToken.None);
        await first.Uow.Seats.ConfirmAsync(showId, ["A1"], "alice", r.Id, CancellationToken.None);

        await using var second = await OpenTx.BeginAsync(db);
        var blocked = second.Uow.Seats.LockForUpdateAsync(showId, ["A1"], CancellationToken.None);
        await AssertWaitingOnLockAsync(db, second.Connection.ProcessID);

        // A different seat isn't blocked.
        await using var third = await OpenTx.BeginAsync(db);
        Assert.Single(await third.Uow.Seats.LockForUpdateAsync(showId, ["A2"], CancellationToken.None).WaitAsync(Patience));

        await first.Transaction.CommitAsync();
        var seen = Assert.Single(await blocked.WaitAsync(Patience));
        Assert.Equal(SeatStatus.Confirmed, seen.Status);   // READ COMMITTED re-reads the row after the wait
    }

    [Fact]
    public async Task Confirm_only_updates_available_rows_and_returns_the_count()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1", "A2", "A3");
        var bobs = NewReservation(showId, "bob", "k", "A2");
        await ReserveAsync(db, bobs);
        var alices = NewReservation(showId, "alice", "k", "A1", "A2", "A3");

        await using var tx = await OpenTx.BeginAsync(db);
        await tx.Uow.Reservations.InsertAsync(alices, CancellationToken.None);
        var updated = await tx.Uow.Seats.ConfirmAsync(showId, ["A1", "A2", "A3"], "alice", alices.Id, CancellationToken.None);
        await tx.Transaction.CommitAsync();

        Assert.Equal(2, updated);
        Assert.Equal(1, await db.CountAsync($"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label = 'A2' AND user_id = 'bob' AND reservation_id = '{bobs.Id}'"));
        Assert.Equal(2, await db.CountAsync($"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND user_id = 'alice' AND status = 'confirmed' AND reservation_id = '{alices.Id}'"));
    }

    [Fact]
    public async Task CountConfirmedByUser_counts_only_this_users_confirmed_seats_in_this_show()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1", "A2", "A3");
        var otherShow = await NewShowAsync(db, "A1");
        await ReserveAsync(db, NewReservation(showId, "alice", "k1", "A1", "A2"));
        await ReserveAsync(db, NewReservation(showId, "bob", "k1", "A3"));
        await ReserveAsync(db, NewReservation(otherShow, "alice", "k2", "A1"));

        await using var tx = await OpenTx.BeginAsync(db);
        Assert.Equal(2, await tx.Uow.Seats.CountConfirmedByUserAsync(showId, "alice", CancellationToken.None));
        Assert.Equal(1, await tx.Uow.Seats.CountConfirmedByUserAsync(showId, "bob", CancellationToken.None));
        Assert.Equal(0, await tx.Uow.Seats.CountConfirmedByUserAsync(showId, "carol", CancellationToken.None));
    }

    [Fact]
    public async Task Release_frees_only_the_seats_of_that_reservation()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1", "A2", "A3");
        var mine = NewReservation(showId, "alice", "k1", "A1", "A2");
        await ReserveAsync(db, mine);
        await ReserveAsync(db, NewReservation(showId, "bob", "k1", "A3"));

        await using var tx = await OpenTx.BeginAsync(db);
        Assert.Equal(2, await tx.Uow.Seats.ReleaseByReservationAsync(mine.Id, CancellationToken.None));
        await tx.Transaction.CommitAsync();

        Assert.Equal(2, await db.CountAsync($"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND status = 'available' AND user_id IS NULL AND reservation_id IS NULL"));
        Assert.Equal(1, await db.CountAsync($"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label = 'A3' AND user_id = 'bob'"));
    }

    // ---- UserLock ----

    [Fact]
    public async Task User_lock_serializes_the_same_user_and_does_not_block_other_users()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);

        await using var first = await OpenTx.BeginAsync(db);
        await first.Uow.UserLock.AcquireAsync("alice", CancellationToken.None);

        await using var second = await OpenTx.BeginAsync(db);
        var blocked = second.Uow.UserLock.AcquireAsync("alice", CancellationToken.None);
        await AssertWaitingOnLockAsync(db, second.Connection.ProcessID);

        await using var third = await OpenTx.BeginAsync(db);
        await third.Uow.UserLock.AcquireAsync("bob", CancellationToken.None).WaitAsync(Patience);

        await first.Transaction.RollbackAsync();   // released at rollback too, not only at commit
        await blocked.WaitAsync(Patience);
    }

    [Fact]
    public async Task User_lock_is_released_when_the_transaction_ends_so_the_pooled_connection_is_clean()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);

        await using (var first = await OpenTx.BeginAsync(db))
        {
            await first.Uow.UserLock.AcquireAsync("alice", CancellationToken.None);
            await first.Transaction.CommitAsync();
        }

        Assert.Equal(0, await db.CountAsync($"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND classid = {UserLock.KeySpace}"));
    }

    // ---- ReservationReadRepository.GetFastPathSnapshotAsync ----

    [Fact]
    public async Task Fast_path_snapshot_returns_the_keys_reservation_and_the_seat_owners_together()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1", "A2", "A3");
        var alices = NewReservation(showId, "alice", "key-1", "A1");
        await ReserveAsync(db, alices);
        await ReserveAsync(db, NewReservation(showId, "bob", "k", "A2"));

        var snapshot = await new ReservationReadRepository(db.Sources)
            .GetFastPathSnapshotAsync("alice", "key-1", showId, ["A1", "A2", "A3", "ZZ"], CancellationToken.None);

        Assert.Equal(alices.Id, snapshot.ExistingForKey?.Id);
        Assert.Equal(
            [new SeatOwner("A1", SeatStatus.Confirmed, "alice"), new SeatOwner("A2", SeatStatus.Confirmed, "bob"), new SeatOwner("A3", SeatStatus.Available, null)],
            snapshot.Owners.OrderBy(o => o.Label, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Fast_path_snapshot_without_a_key_match_still_returns_the_owners()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1");

        var snapshot = await new ReservationReadRepository(db.Sources)
            .GetFastPathSnapshotAsync("alice", "never-used", showId, ["A1"], CancellationToken.None);

        Assert.Null(snapshot.ExistingForKey);
        Assert.Equal([new SeatOwner("A1", SeatStatus.Available, null)], snapshot.Owners);
    }

    [Fact]
    public async Task Fast_path_snapshot_is_a_single_database_command()
    {
        // Npgsql starts one tracing Activity per command or batch it sends; count the ones aimed at this test's database.
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await NewShowAsync(db, "A1");
        var database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database;
        var commands = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("db.name"), database))
                {
                    Interlocked.Increment(ref commands);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var snapshot = await new ReservationReadRepository(db.Sources).GetFastPathSnapshotAsync("alice", "k", showId, ["A1"], CancellationToken.None);

        Assert.Single(snapshot.Owners);
        Assert.Equal(1, commands);
    }
}
