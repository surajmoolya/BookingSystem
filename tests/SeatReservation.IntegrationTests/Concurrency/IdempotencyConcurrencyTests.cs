using System.Net;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Concurrency;

/// <summary>A client retrying in parallel with one key still gets exactly one reservation (T-3.12, D-031–D-035).</summary>
[Collection(PostgresCollection.Name)]
public class IdempotencyConcurrencyTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const int Requests = 100;

    // Keys are per user across every show and the class shares one host, so each test (a new instance) gets its own user.
    private readonly string _alice = $"alice-{Guid.NewGuid():N}"[..14];

    private string Db => api.Factory.ConnectionString;

    [Fact]
    public async Task One_hundred_parallel_retries_of_one_request_create_one_reservation_and_replay_it()
    {
        var showId = await CreateShowAsync(api.Client, ["A1", "A2"]);

        var results = await TestConcurrency.ConcurrentAsync(Requests, _ => ReserveAsync(api.Client, showId, _alice, ["A1"], "key-1"));

        var mix = Histogram(results);
        Assert.True(results.Count(r => r.Is(HttpStatusCode.Created)) == 1, mix);
        Assert.True(results.Count(r => r.Is(HttpStatusCode.OK)) == Requests - 1, mix);
        Assert.All(results.Where(r => r.Is(HttpStatusCode.OK)), r => Assert.True(r.Replayed, mix));
        Assert.False(results.Single(r => r.Is(HttpStatusCode.Created)).Replayed);

        // Every response, the 201 and the 99 replays, names the same reservation.
        var id = results.Single(r => r.Is(HttpStatusCode.Created)).ReservationId;
        Assert.All(results, r => Assert.Equal(id, r.ReservationId));

        Assert.Equal(1, await ScalarAsync(Db, $"SELECT count(*) FROM reservations WHERE user_id = '{_alice}'"));
        Assert.Equal(1, await ScalarAsync(Db, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND status = 'confirmed'"));
        Assert.Equal(1, await ScalarAsync(Db, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label = 'A1' AND reservation_id = '{id}'"));
    }

    [Fact]
    public async Task One_hundred_parallel_requests_with_one_key_and_two_payloads_bind_the_key_to_exactly_one()
    {
        var showId = await CreateShowAsync(api.Client, ["X", "Y"]);
        string Seat(int i) => i % 2 == 0 ? "X" : "Y";

        var results = await TestConcurrency.ConcurrentAsync(Requests, i => ReserveAsync(api.Client, showId, _alice, [Seat(i)], "key-1"));

        var mix = Histogram(results);
        Assert.DoesNotContain(results, r => (int)r.Status >= 500);
        var created = Assert.Single(results, r => r.Is(HttpStatusCode.Created));
        var winningSeat = Assert.Single(created.Seats);
        var losingSeat = winningSeat == "X" ? "Y" : "X";

        // The winning payload's other 49 requests replay it; every request with the other payload is a key conflict.
        var indexed = results.Select((r, i) => (r, seat: Seat(i))).ToArray();
        Assert.All(indexed.Where(x => x.seat == winningSeat), x =>
        {
            Assert.True(x.r.Is(HttpStatusCode.Created) || (x.r.Is(HttpStatusCode.OK) && x.r.Replayed), mix);
            Assert.Equal(created.ReservationId, x.r.ReservationId);
        });
        Assert.All(indexed.Where(x => x.seat == losingSeat), x =>
        {
            Assert.True(x.r.Is(HttpStatusCode.Conflict, "idempotency_key_conflict"), mix);
            Assert.Equal(created.ReservationId, x.r.Body.GetProperty("reservation_id").GetGuid());
        });

        Assert.Equal(1, await ScalarAsync(Db, $"SELECT count(*) FROM reservations WHERE user_id = '{_alice}'"));
        Assert.Equal(1, await ScalarAsync(Db, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label = '{losingSeat}' AND status = 'available'"));
    }
}
