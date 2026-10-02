using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Shows;
using SeatReservation.Infrastructure.Repositories;
using SeatReservation.Infrastructure.Transactions;
using SeatReservation.IntegrationTests.Infrastructure;
using Xunit.Abstractions;

namespace SeatReservation.IntegrationTests.Persistence;

/// <summary>Each repository port method's SQL against real Postgres, with no HTTP involved (lld §13.3).</summary>
[Collection(PostgresCollection.Name)]
public class RepositorySqlTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private static PgTransactionRunner RunnerFor(MigratedDatabase db) => new(db.Sources, NullLogger<PgTransactionRunner>.Instance);

    private static ShowInfo NewShow(int seats, string name = "friday-night", long price = 25_000, int limit = 4) =>
        new(Guid.NewGuid(), name, price, limit, seats);

    private static Task InsertAsync(MigratedDatabase db, ShowInfo show, IReadOnlyList<string> labels, bool commit = true) =>
        RunnerFor(db).RunAsync("create_show", async (uow, ct) =>
        {
            await uow.Shows.InsertShowWithSeatsAsync(show, labels, ct);
            return new TxResult<bool>(true, commit);
        }, CancellationToken.None);

    // ---- ShowRepository.InsertShowWithSeatsAsync ----

    [Fact]
    public async Task Insert_writes_the_show_row_with_every_field()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var show = NewShow(2, name: "gala", price: 9_000_000_000, limit: 7);   // price beyond int32

        await InsertAsync(db, show, ["A1", "A2"]);

        Assert.Equal(show, await new ShowReadRepository(db.Sources).GetShowAsync(show.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Insert_writes_all_seats_available_with_ordinals_in_the_given_order()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var show = NewShow(4);

        await InsertAsync(db, show, ["B2", "A10", "a1", "A2"]);

        Assert.Equal(4, await db.CountAsync($"SELECT count(*) FROM seats WHERE show_id = '{show.Id}' AND status = 'available' AND user_id IS NULL AND reservation_id IS NULL"));
        Assert.Equal(1, await db.CountAsync($"SELECT ordinal::bigint FROM seats WHERE show_id = '{show.Id}' AND label = 'B2'"));
        Assert.Equal(2, await db.CountAsync($"SELECT ordinal::bigint FROM seats WHERE show_id = '{show.Id}' AND label = 'A10'"));
        Assert.Equal(3, await db.CountAsync($"SELECT ordinal::bigint FROM seats WHERE show_id = '{show.Id}' AND label = 'a1'"));
        Assert.Equal(4, await db.CountAsync($"SELECT ordinal::bigint FROM seats WHERE show_id = '{show.Id}' AND label = 'A2'"));
    }

    [Fact]
    public async Task Ten_thousand_seats_insert_in_under_a_second()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        await InsertAsync(db, NewShow(1), ["warm-up"]);   // connection open, statements planned once
        var labels = Enumerable.Range(1, 10_000).Select(i => $"S{i}").ToArray();
        var show = NewShow(labels.Length);

        var stopwatch = Stopwatch.StartNew();
        await InsertAsync(db, show, labels);
        stopwatch.Stop();
        output.WriteLine($"10,000-seat insert: {stopwatch.ElapsedMilliseconds} ms");

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"10,000-seat insert took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(10_000, await db.CountAsync($"SELECT count(*) FROM seats WHERE show_id = '{show.Id}'"));
        Assert.Equal(10_000, await db.CountAsync($"SELECT max(ordinal)::bigint FROM seats WHERE show_id = '{show.Id}'"));
    }

    [Fact]
    public async Task Rolled_back_insert_leaves_neither_show_nor_seats()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var show = NewShow(2);

        await InsertAsync(db, show, ["A1", "A2"], commit: false);

        Assert.Equal(0, await db.CountAsync($"SELECT count(*) FROM shows WHERE id = '{show.Id}'"));
        Assert.Equal(0, await db.CountAsync($"SELECT count(*) FROM seats WHERE show_id = '{show.Id}'"));
    }

    [Fact]
    public async Task Failed_seat_insert_takes_the_show_row_down_with_it()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var show = NewShow(2);

        // A duplicate label violates pk_seats (the validator normally prevents it): the batch is one transaction, all or nothing.
        await Assert.ThrowsAnyAsync<Exception>(() => InsertAsync(db, show, ["A1", "A1"]));

        Assert.Equal(0, await db.CountAsync($"SELECT count(*) FROM shows WHERE id = '{show.Id}'"));
    }

    // ---- ShowReadRepository ----

    [Fact]
    public async Task GetShow_returns_null_for_an_unknown_id()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);

        Assert.Null(await new ShowReadRepository(db.Sources).GetShowAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_returns_seats_in_ordinal_order_not_label_order()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var show = NewShow(4);
        await InsertAsync(db, show, ["B2", "A10", "a1", "A2"]);

        var snapshot = await new ShowReadRepository(db.Sources).GetSeatSnapshotAsync(show.Id, CancellationToken.None);

        Assert.Equal(["B2", "A10", "a1", "A2"], snapshot.Select(s => s.Label));
        Assert.All(snapshot, s => Assert.Equal(SeatStatus.Available, s.Status));
    }

    [Fact]
    public async Task Snapshot_maps_every_status_and_covers_only_the_requested_show()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);
        var show = NewShow(3);
        var other = NewShow(1);
        await InsertAsync(db, show, ["A1", "A2", "A3"]);
        await InsertAsync(db, other, ["Z1"]);
        var reservation = Guid.NewGuid();
        await db.ExecuteAsync($"""
            INSERT INTO reservations (id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status)
            VALUES ('{reservation}', '{show.Id}', 'alice', 'k1', '\x00'::bytea, ARRAY['A1','A3'], 50000, 'confirmed');
            UPDATE seats SET status = 'confirmed', user_id = 'alice', reservation_id = '{reservation}' WHERE show_id = '{show.Id}' AND label = 'A1';
            UPDATE seats SET status = 'held', user_id = 'alice', reservation_id = '{reservation}' WHERE show_id = '{show.Id}' AND label = 'A3';
            """);

        var snapshot = await new ShowReadRepository(db.Sources).GetSeatSnapshotAsync(show.Id, CancellationToken.None);

        Assert.Equal(
            [new SeatState("A1", SeatStatus.Confirmed), new SeatState("A2", SeatStatus.Available), new SeatState("A3", SeatStatus.Held)],
            snapshot);
    }

    [Fact]
    public async Task Snapshot_of_an_unknown_show_is_empty()
    {
        await using var db = await MigratedDatabase.CreateAsync(postgres);

        Assert.Empty(await new ShowReadRepository(db.Sources).GetSeatSnapshotAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public void Unknown_status_text_is_a_loud_error()
    {
        Assert.Throws<InvalidOperationException>(() => SeatStatusMapping.Parse("reserved"));
    }
}
