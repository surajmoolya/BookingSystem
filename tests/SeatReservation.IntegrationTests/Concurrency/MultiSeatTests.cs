using System.Net;
using SeatReservation.IntegrationTests.Infrastructure;
using Xunit.Abstractions;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Concurrency;

/// <summary>Multi-seat reservations are all-or-nothing, exclusive and deadlock-free under overlap (T-3.10, D-010, D-030).</summary>
[Collection(PostgresCollection.Name)]
public class MultiSeatTests(ApiFixture api, ITestOutputHelper output) : IClassFixture<ApiFixture>
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private string Db => api.Factory.ConnectionString;

    [Fact]
    public async Task A_request_with_one_taken_seat_reserves_none_of_them()
    {
        var showId = await CreateShowAsync(api.Client, ["A1", "A2", "A3"]);
        Assert.True((await ReserveAsync(api.Client, showId, $"bob-{_run}", ["A2"], "k")).Is(HttpStatusCode.Created));

        var result = await ReserveAsync(api.Client, showId, $"alice-{_run}", ["A1", "A2", "A3"], "k");

        Assert.True(result.Is(HttpStatusCode.Conflict, "seat_taken"), result.ToString());
        Assert.Equal(["A2"], result.Body.GetProperty("unavailable_seats").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(2, await ScalarAsync(Db, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label IN ('A1','A3') AND status = 'available'"));
        Assert.Equal(0, await ScalarAsync(Db, $"SELECT count(*) FROM reservations WHERE show_id = '{showId}' AND user_id = 'alice-{_run}'"));
    }

    [Fact]
    public async Task Three_hundred_overlapping_multi_seat_requests_are_exclusive_and_deadlock_free()
    {
        const int requests = 300;
        const int seatCount = 30;
        var seed = Random.Shared.Next();
        output.WriteLine($"seed {seed}");
        var random = new Random(seed);
        var showId = await CreateShowAsync(api.Client, Labels(seatCount));

        // 2-3 random seats each, deliberately in random order: the service must sort them, or two requests locking
        // {S1,S2} and {S2,S1} could deadlock. Distinct users, so only seat availability decides.
        var picks = Enumerable.Range(0, requests)
            .Select(_ => Labels(seatCount).OrderBy(_ => random.Next()).Take(random.Next(2, 4)).ToArray())
            .ToArray();
        var deadlocksBefore = await DeadlocksAsync();

        var results = await TestConcurrency.ConcurrentAsync(requests, i =>
            ReserveAsync(api.Client, showId, $"u{i}-{_run}", picks[i], "k"));

        var mix = Histogram(results);
        output.WriteLine(mix);
        Assert.DoesNotContain(results, r => (int)r.Status >= 500);
        Assert.All(results, r => Assert.True(r.Is(HttpStatusCode.Created) || r.Is(HttpStatusCode.Conflict, "seat_taken"), mix));
        Assert.Contains(results, r => r.Is(HttpStatusCode.Created));

        // Every winner got exactly what it asked for, and no seat went to two winners.
        var winners = results.Select((r, i) => (r, i)).Where(x => x.r.Is(HttpStatusCode.Created)).ToArray();
        foreach (var (r, i) in winners)
        {
            Assert.Equal(picks[i].Order(StringComparer.Ordinal), r.Seats);
        }

        var wonSeats = winners.SelectMany(w => w.r.Seats).ToArray();
        Assert.Equal(wonSeats.Length, wonSeats.Distinct().Count());

        // The database agrees: the confirmed seats are exactly the union of the 201s, each owned by the reservation that won it.
        Assert.Equal(wonSeats.Length, await ScalarAsync(Db, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND status = 'confirmed'"));
        foreach (var (r, _) in winners)
        {
            Assert.Equal(r.Seats.Length, await ScalarAsync(Db,
                $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND status = 'confirmed' AND reservation_id = '{r.ReservationId}'"));
        }

        Assert.Equal(winners.Length, await ScalarAsync(Db, $"SELECT count(*) FROM reservations WHERE show_id = '{showId}'"));
        Assert.Equal(0, await ScalarAsync(Db, $"""
            SELECT count(*) FROM (
                SELECT label FROM reservations, unnest(seats) AS label
                WHERE show_id = '{showId}' AND status = 'confirmed'
                GROUP BY label HAVING count(*) > 1) AS doubled
            """));

        // A deadlock would be retried by the runner (D-055) and so wouldn't show as a 5xx; Postgres still counts it.
        // Backends flush their statistics about once a second, so give the counter time to catch up.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(deadlocksBefore, await DeadlocksAsync());
    }

    private Task<long> DeadlocksAsync() =>
        ScalarAsync(Db, "SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()");
}
