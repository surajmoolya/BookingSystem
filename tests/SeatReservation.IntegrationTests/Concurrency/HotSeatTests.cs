using System.Net;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Concurrency;

/// <summary>The headline guarantee: many users, one seat, exactly one winner (T-3.8).</summary>
[Collection(PostgresCollection.Name)]
public class HotSeatTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const int Users = 200;

    [Fact]
    public async Task Two_hundred_users_racing_for_one_seat_produce_exactly_one_winner()
    {
        var showId = await CreateShowAsync(api.Client, ["HOT", "COLD"]);
        var run = Guid.NewGuid().ToString("N")[..8];

        // Every request is parked at a start gate and released at once, so they really overlap.
        var results = await TestConcurrency.ConcurrentAsync(Users, i =>
            ReserveAsync(api.Client, showId, $"u{i}-{run}", ["HOT"], $"key-{i}"));

        var mix = Histogram(results);
        Assert.True(results.Count(r => r.Is(HttpStatusCode.Created)) == 1, mix);
        Assert.True(results.Count(r => r.Is(HttpStatusCode.Conflict, "seat_taken")) == Users - 1, mix);
        Assert.DoesNotContain(results, r => (int)r.Status >= 500);

        var winner = results.Single(r => r.Is(HttpStatusCode.Created));
        Assert.Equal(["HOT"], winner.Seats);
        Assert.All(results.Where(r => r.Code == "seat_taken"), r =>
            Assert.Equal(["HOT"], r.Body.GetProperty("unavailable_seats").EnumerateArray().Select(s => s.GetString())));

        // The database agrees: one reservation holds the seat, and the seat points at it.
        Assert.Equal(1, await ScalarAsync(api.Factory.ConnectionString, $"SELECT count(*) FROM reservations WHERE show_id = '{showId}' AND 'HOT' = ANY(seats)"));
        Assert.Equal(1, await ScalarAsync(api.Factory.ConnectionString, $"SELECT count(*) FROM reservations WHERE show_id = '{showId}'"));
        Assert.Equal(1, await ScalarAsync(api.Factory.ConnectionString,
            $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label = 'HOT' AND status = 'confirmed' AND reservation_id = '{winner.ReservationId}'"));
        Assert.Equal(1, await ScalarAsync(api.Factory.ConnectionString,
            $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label = 'COLD' AND status = 'available'"));
    }
}
