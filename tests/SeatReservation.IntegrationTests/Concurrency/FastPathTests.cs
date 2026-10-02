using System.Net;
using Npgsql;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Concurrency;

/// <summary>
/// The lock-free fast path end to end (T-3.13, D-036). An outside transaction holds the seat's row lock: a request that
/// takes the fast path is declined without waiting for it, and one that must go through the locked path visibly waits.
/// </summary>
[Collection(PostgresCollection.Name)]
public class FastPathTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly string _alice = $"alice-{Guid.NewGuid():N}"[..14];
    private readonly string _bob = $"bob-{Guid.NewGuid():N}"[..12];

    private string Db => api.Factory.ConnectionString;

    /// <summary>An open transaction holding <c>FOR UPDATE</c> on one seat row, as a slow concurrent reserve would.</summary>
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldSeatLockAsync(Guid showId, string label)
    {
        var connection = new NpgsqlConnection(Db);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"SELECT 1 FROM seats WHERE show_id = '{showId}' AND label = '{label}' FOR UPDATE", connection, transaction);
        await command.ExecuteScalarAsync();
        return (connection, transaction);
    }

    private async Task<long> BackendsWaitingOnLocksAsync() =>
        await ScalarAsync(Db, "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'");

    [Fact]
    public async Task A_seat_owned_by_another_user_is_declined_without_touching_its_row_lock()
    {
        var showId = await CreateShowAsync(api.Client, ["HOT", "COLD"]);
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["HOT"], "a")).Is(HttpStatusCode.Created));
        var (connection, transaction) = await HoldSeatLockAsync(showId, "HOT");
        await using var _ = connection;

        // The locked path would queue behind the held row lock (lock_timeout 10s); the fast path never asks for it.
        var declined = await ReserveAsync(api.Client, showId, _bob, ["HOT", "COLD"], "b").WaitAsync(Patience);

        Assert.True(declined.Is(HttpStatusCode.Conflict, "seat_taken"), declined.ToString());
        Assert.Equal(["HOT"], declined.Body.GetProperty("unavailable_seats").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(0, await BackendsWaitingOnLocksAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task A_seat_the_user_already_owns_falls_through_to_the_locked_path()
    {
        var showId = await CreateShowAsync(api.Client, ["HOT"]);
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["HOT"], "a")).Is(HttpStatusCode.Created));
        var (connection, transaction) = await HoldSeatLockAsync(showId, "HOT");
        await using var _ = connection;

        // A new key for a seat alice owns: the fast path mustn't decline it (it could be a replay), so it reaches the
        // seat lock and waits there until the outside transaction ends. Then the locked path declines it.
        var pending = ReserveAsync(api.Client, showId, _alice, ["HOT"], "b");
        await WaitUntilAsync(async () => await BackendsWaitingOnLocksAsync() == 1);
        Assert.False(pending.IsCompleted);

        await transaction.RollbackAsync();
        var result = await pending.WaitAsync(Patience);
        Assert.True(result.Is(HttpStatusCode.Conflict, "seat_taken"), result.ToString());
    }

    [Fact]
    public async Task A_replay_after_commit_replays_even_while_the_seat_row_is_locked()
    {
        var showId = await CreateShowAsync(api.Client, ["HOT"]);
        var created = await ReserveAsync(api.Client, showId, _alice, ["HOT"], "a");
        Assert.True(created.Is(HttpStatusCode.Created));
        var (connection, transaction) = await HoldSeatLockAsync(showId, "HOT");
        await using var _ = connection;

        // The key exists, so the fast path steps aside and the locked path answers from the key lookup, before any seat lock.
        var replay = await ReserveAsync(api.Client, showId, _alice, ["HOT"], "a").WaitAsync(Patience);

        Assert.True(replay.Is(HttpStatusCode.OK) && replay.Replayed, replay.ToString());
        Assert.Equal(created.ReservationId, replay.ReservationId);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task The_hot_seat_still_has_exactly_one_winner_with_the_fast_path_on()
    {
        var showId = await CreateShowAsync(api.Client, ["HOT"]);

        var results = await TestConcurrency.ConcurrentAsync(100, i => ReserveAsync(api.Client, showId, $"{_bob}-{i}", ["HOT"], "k"));

        var mix = Histogram(results);
        Assert.True(results.Count(r => r.Is(HttpStatusCode.Created)) == 1, mix);
        Assert.True(results.Count(r => r.Is(HttpStatusCode.Conflict, "seat_taken")) == 99, mix);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(20);
        }
    }
}
