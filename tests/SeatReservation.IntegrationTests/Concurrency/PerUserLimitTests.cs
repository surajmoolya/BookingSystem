using System.Net;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Concurrency;

/// <summary>The per-user limit over HTTP, one request at a time and under a burst from one user (T-3.9, D-085).</summary>
[Collection(PostgresCollection.Name)]
public class PerUserLimitTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    // Each test (a new instance) gets its own users: keys and limits are per user (D-031).
    private readonly string _alice = $"alice-{Guid.NewGuid():N}"[..14];
    private readonly string _bob = $"bob-{Guid.NewGuid():N}"[..12];

    private async Task<long> SeatsOwnedAsync(Guid showId, string user) =>
        await ScalarAsync(api.Factory.ConnectionString, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND user_id = '{user}' AND status = 'confirmed'");

    private static void AssertLimit(ReserveResult result, int limit, int held, int requested)
    {
        Assert.True(result.Is(HttpStatusCode.Conflict, "per_user_limit"), result.ToString());
        Assert.Equal(limit, result.Body.GetProperty("limit").GetInt32());
        Assert.Equal(held, result.Body.GetProperty("held").GetInt32());
        Assert.Equal(requested, result.Body.GetProperty("requested").GetInt32());
    }

    [Fact]
    public async Task Four_single_seat_reserves_succeed_and_the_fifth_is_rejected()
    {
        var showId = await CreateShowAsync(api.Client, Labels(10));

        for (var i = 1; i <= 4; i++)
        {
            var ok = await ReserveAsync(api.Client, showId, _alice, [$"S{i}"], $"k{i}");
            Assert.True(ok.Is(HttpStatusCode.Created), $"reserve {i}: {ok}");
        }

        AssertLimit(await ReserveAsync(api.Client, showId, _alice, ["S5"], "k5"), limit: 4, held: 4, requested: 1);
        Assert.Equal(4, await SeatsOwnedAsync(showId, _alice));

        // Another user isn't affected by alice's count.
        Assert.True((await ReserveAsync(api.Client, showId, _bob, ["S5"], "k1")).Is(HttpStatusCode.Created));
    }

    [Fact]
    public async Task Cancelling_one_seat_at_the_limit_frees_room_for_one_more_but_not_two()
    {
        var showId = await CreateShowAsync(api.Client, Labels(10));
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S1", "S2", "S3"], "k1")).Is(HttpStatusCode.Created));
        var single = await ReserveAsync(api.Client, showId, _alice, ["S4"], "k2");
        Assert.True(single.Is(HttpStatusCode.Created), single.ToString());
        AssertLimit(await ReserveAsync(api.Client, showId, _alice, ["S5"], "k3"), limit: 4, held: 4, requested: 1);

        Assert.True((await CancelAsync(api.Client, single.ReservationId, _alice)).Is(HttpStatusCode.OK));

        // Held is now 3: two more would make 5, one more makes exactly 4, and then the limit is reached again.
        AssertLimit(await ReserveAsync(api.Client, showId, _alice, ["S5", "S6"], "k4"), limit: 4, held: 3, requested: 2);
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S5"], "k5")).Is(HttpStatusCode.Created));
        AssertLimit(await ReserveAsync(api.Client, showId, _alice, ["S6"], "k6"), limit: 4, held: 4, requested: 1);
        Assert.Equal(4, await SeatsOwnedAsync(showId, _alice));
    }

    [Fact]
    public async Task A_five_seat_request_is_rejected_outright()
    {
        var showId = await CreateShowAsync(api.Client, Labels(10));

        AssertLimit(await ReserveAsync(api.Client, showId, _alice, ["S1", "S2", "S3", "S4", "S5"], "k1"), limit: 4, held: 0, requested: 5);
        Assert.Equal(0, await SeatsOwnedAsync(showId, _alice));
    }

    [Fact]
    public async Task A_rejected_request_does_not_bind_its_key()
    {
        var showId = await CreateShowAsync(api.Client, Labels(10), perUserLimit: 1);
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S1"], "k1")).Is(HttpStatusCode.Created));
        AssertLimit(await ReserveAsync(api.Client, showId, _alice, ["S2"], "k2"), limit: 1, held: 1, requested: 1);

        // k2 is still free: on a fresh show (alice holds nothing there) it can be used for anything.
        var otherShow = await CreateShowAsync(api.Client, Labels(3), perUserLimit: 1);
        Assert.True((await ReserveAsync(api.Client, otherShow, _alice, ["S3"], "k2")).Is(HttpStatusCode.Created));
    }

    [Theory]
    [InlineData(4)]   // the default
    [InlineData(2)]   // "per_user_limit": 2 at creation (D-085)
    public async Task Fifty_parallel_single_seat_requests_from_one_user_get_exactly_the_limit(int limit)
    {
        const int requests = 50;
        var showId = await CreateShowAsync(api.Client, Labels(requests), perUserLimit: limit);

        // Distinct seats and distinct keys: only the limit can stop them, and the user lock makes the count race-free.
        var results = await TestConcurrency.ConcurrentAsync(requests, i =>
            ReserveAsync(api.Client, showId, _alice, [$"S{i + 1}"], $"k{i}"));

        var mix = Histogram(results);
        Assert.True(results.Count(r => r.Is(HttpStatusCode.Created)) == limit, mix);
        Assert.True(results.Count(r => r.Is(HttpStatusCode.Conflict, "per_user_limit")) == requests - limit, mix);
        Assert.DoesNotContain(results, r => (int)r.Status >= 500);

        Assert.Equal(limit, await SeatsOwnedAsync(showId, _alice));
        Assert.Equal(limit, await ScalarAsync(api.Factory.ConnectionString, $"SELECT count(*) FROM reservations WHERE show_id = '{showId}' AND user_id = '{_alice}'"));
        Assert.All(results.Where(r => r.Code == "per_user_limit"), r => Assert.Equal(limit, r.Body.GetProperty("held").GetInt32()));
    }
}
