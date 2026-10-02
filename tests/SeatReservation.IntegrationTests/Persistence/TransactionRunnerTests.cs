using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.Infrastructure.Transactions;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public class TransactionRunnerTests(PostgresFixture postgres)
{
    private static PgTransactionRunner RunnerFor(MigratedDatabase db) => new(db.Sources, NullLogger<PgTransactionRunner>.Instance);

    private static NpgsqlCommand Command(IUnitOfWork uow, string sql)
    {
        var pg = (PgUnitOfWork)uow;
        return new NpgsqlCommand(sql, pg.Connection, pg.Transaction);
    }

    private static async Task InsertShowAsync(IUnitOfWork uow, Guid id, CancellationToken ct)
    {
        await using var insert = Command(uow, $"INSERT INTO shows (id, name, price_paise, total_seats) VALUES ('{id}', 'x', 100, 1)");
        await insert.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task CommitWith_persists_the_writes_and_returns_the_value()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var id = Guid.NewGuid();

        var result = await RunnerFor(db).RunAsync("test", async (uow, ct) =>
        {
            await InsertShowAsync(uow, id, ct);
            return TxResult<string>.CommitWith("created");
        }, CancellationToken.None);

        Assert.Equal("created", result);
        Assert.Equal(1, await db.CountAsync($"SELECT count(*) FROM shows WHERE id = '{id}'"));
    }

    [Fact]
    public async Task RollbackWith_leaves_no_rows_behind_and_still_returns_the_value()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var id = Guid.NewGuid();

        var result = await RunnerFor(db).RunAsync("test", async (uow, ct) =>
        {
            await InsertShowAsync(uow, id, ct);
            return TxResult<string>.RollbackWith("declined");
        }, CancellationToken.None);

        Assert.Equal("declined", result);
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM shows"));
    }

    [Fact]
    public async Task A_bug_in_the_delegate_rolls_back_and_is_rethrown_unchanged_without_a_retry()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunnerFor(db).RunAsync<int>("test", async (uow, ct) =>
        {
            attempts++;
            await InsertShowAsync(uow, Guid.NewGuid(), ct);
            throw new InvalidOperationException("boom");
        }, CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM shows"));
    }

    [Fact]
    public async Task Transactions_run_at_read_committed()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);

        var level = await RunnerFor(db).RunAsync("test", async (uow, ct) =>
        {
            await using var command = Command(uow, "SHOW transaction_isolation");
            return TxResult<string>.RollbackWith((string)(await command.ExecuteScalarAsync(ct))!);
        }, CancellationToken.None);

        Assert.Equal("read committed", level);
    }

    [Fact]
    public async Task SetLockTimeout_applies_inside_the_transaction_and_does_not_leak_through_the_pool()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres, new DatabaseOptions { MaxPoolSize = 1, OpsPoolSize = 1 });
        var runner = RunnerFor(db);

        var inside = await runner.RunAsync("test", async (uow, ct) =>
        {
            await uow.SetLockTimeoutAsync(TimeSpan.FromSeconds(7), ct);
            await using var command = Command(uow, "SHOW lock_timeout");
            return TxResult<string>.RollbackWith((string)(await command.ExecuteScalarAsync(ct))!);
        }, CancellationToken.None);

        // Same single pooled connection (No Reset On Close) is reused: the setting must be gone.
        var after = await runner.RunAsync("test", async (uow, ct) =>
        {
            await using var command = Command(uow, "SHOW lock_timeout");
            return TxResult<string>.RollbackWith((string)(await command.ExecuteScalarAsync(ct))!);
        }, CancellationToken.None);

        Assert.Equal("7s", inside);
        Assert.Equal("0", after);
    }

    [Fact]
    public async Task A_dropped_connection_mid_transaction_is_retried_and_the_first_attempts_writes_are_gone()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var id = Guid.NewGuid();
        var attempts = 0;

        var result = await RunnerFor(db).RunAsync("test", async (uow, ct) =>
        {
            attempts++;
            await InsertShowAsync(uow, id, ct);   // same primary key both times: only passes if attempt 1 was rolled back
            if (attempts == 1)
            {
                await using var kill = Command(uow, "SELECT pg_terminate_backend(pg_backend_pid())");
                await kill.ExecuteNonQueryAsync(ct);
            }

            return TxResult<int>.CommitWith(attempts);
        }, CancellationToken.None);

        Assert.Equal(2, result);
        Assert.Equal(1, await db.CountAsync($"SELECT count(*) FROM shows WHERE id = '{id}'"));
    }

    [Fact]
    public async Task A_database_that_keeps_dropping_the_connection_ends_in_DependencyUnavailable_after_three_attempts()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<DependencyUnavailableException>(() => RunnerFor(db).RunAsync<int>("test", async (uow, ct) =>
        {
            attempts++;
            await using var kill = Command(uow, "SELECT pg_terminate_backend(pg_backend_pid())");
            await kill.ExecuteNonQueryAsync(ct);
            return TxResult<int>.CommitWith(1);
        }, CancellationToken.None));

        Assert.Equal(PgTransactionRunner.MaxAttempts, attempts);
        Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Contains("'test'", ex.Message);
    }

    [Fact]
    public async Task A_lock_timeout_is_retried_and_succeeds_once_the_lock_holder_lets_go()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await db.InsertShowAsync();
        await using var holder = new NpgsqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"SELECT 1 FROM shows WHERE id = '{showId}' FOR UPDATE", holder, holderTx))
        {
            await hold.ExecuteNonQueryAsync();
        }

        var attempts = 0;

        var result = await RunnerFor(db).RunAsync("test", async (uow, ct) =>
        {
            attempts++;
            if (attempts == 2)
            {
                await holderTx.RollbackAsync(ct);   // attempt 1 timed out on the held lock; let go before attempt 2 asks again
            }

            await uow.SetLockTimeoutAsync(TimeSpan.FromMilliseconds(100), ct);
            await using var lockRow = Command(uow, $"SELECT 1 FROM shows WHERE id = '{showId}' FOR UPDATE");
            await lockRow.ExecuteNonQueryAsync(ct);
            return TxResult<bool>.RollbackWith(true);
        }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_lock_that_is_never_released_exhausts_the_retries_as_DependencyUnavailable()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var showId = await db.InsertShowAsync();
        await using var holder = new NpgsqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"SELECT 1 FROM shows WHERE id = '{showId}' FOR UPDATE", holder, holderTx))
        {
            await hold.ExecuteNonQueryAsync();
        }

        var attempts = 0;
        var ex = await Assert.ThrowsAsync<DependencyUnavailableException>(() => RunnerFor(db).RunAsync<bool>("test", async (uow, ct) =>
        {
            attempts++;
            await uow.SetLockTimeoutAsync(TimeSpan.FromMilliseconds(50), ct);
            await using var lockRow = Command(uow, $"SELECT 1 FROM shows WHERE id = '{showId}' FOR UPDATE");
            await lockRow.ExecuteNonQueryAsync(ct);
            return TxResult<bool>.RollbackWith(true);
        }, CancellationToken.None));

        Assert.Equal(PgTransactionRunner.MaxAttempts, attempts);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task Cancellation_passes_through_as_cancellation_not_as_a_database_error()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunnerFor(db).RunAsync("test",
            (uow, ct) => Task.FromResult(TxResult<int>.CommitWith(1)), cts.Token));
    }

    [Fact]
    public async Task Concurrent_runs_each_get_their_own_transaction()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var runner = RunnerFor(db);

        var committed = await TestConcurrency.ConcurrentAsync(30, i => runner.RunAsync("test", async (uow, ct) =>
        {
            await InsertShowAsync(uow, Guid.NewGuid(), ct);
            return i % 2 == 0 ? TxResult<int>.CommitWith(i) : TxResult<int>.RollbackWith(i);
        }, CancellationToken.None));

        Assert.Equal(Enumerable.Range(0, 30), committed);
        Assert.Equal(15, await db.CountAsync("SELECT count(*) FROM shows"));
    }
}
