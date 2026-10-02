using System.Net;
using SeatReservation.IntegrationTests.Http;
using SeatReservation.IntegrationTests.Infrastructure;
using Xunit.Abstractions;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Concurrency;

/// <summary>
/// Cancellation racing with rebooking (T-4.4). Release is keyed by reservation id (D-040), so a cancel can only ever free the
/// seats of its own reservation, however late or often it arrives.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CancelRebookTests(CancellationApiFixture api, ITestOutputHelper output) : IClassFixture<CancellationApiFixture>
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private string Db => api.Factory.ConnectionString;

    private string User(string name) => $"{name}-{_run}";

    private async Task<ReserveResult> ReservedAsync(Guid showId, string user, string seat, string key = "k")
    {
        var result = await ReserveAsync(api.Client, showId, user, [seat], key);
        Assert.True(result.Is(HttpStatusCode.Created), result.ToString());
        return result;
    }

    private Task<long> SeatOwnedByAsync(Guid showId, string label, string user, Guid reservationId) =>
        ScalarAsync(Db, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label = '{label}' AND status = 'confirmed' AND user_id = '{user}' AND reservation_id = '{reservationId}'");

    private Task<long> DeadlocksAsync() =>
        ScalarAsync(Db, "SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()");

    [Fact]
    public async Task A_stale_cancel_after_a_rebook_leaves_the_seat_with_the_new_owner()
    {
        var showId = await CreateShowAsync(api.Client, ["Z", "Y"]);
        var (alice, carol) = (User("alice"), User("carol"));
        var alices = await ReservedAsync(showId, alice, "Z");
        Assert.True((await CancelAsync(api.Client, alices.ReservationId, alice)).Is(HttpStatusCode.OK));
        var carols = await ReservedAsync(showId, carol, "Z");

        // One late duplicate, then a burst of them: each is a 200 for alice's (already cancelled) reservation and frees nothing.
        var stale = await CancelAsync(api.Client, alices.ReservationId, alice);
        var burst = await TestConcurrency.ConcurrentAsync(20, _ => CancelAsync(api.Client, alices.ReservationId, alice));

        Assert.All(burst.Append(stale), r =>
        {
            Assert.True(r.Is(HttpStatusCode.OK), r.ToString());
            Assert.Equal(alices.ReservationId, r.ReservationId);
            Assert.Equal("cancelled", r.Body.GetProperty("status").GetString());
        });
        Assert.Equal(1, await SeatOwnedByAsync(showId, "Z", carol, carols.ReservationId));
        Assert.Equal(1, await ScalarAsync(Db, $"SELECT count(*) FROM reservations WHERE id = '{carols.ReservationId}' AND status = 'confirmed'"));

        // The rebook is real: a third user is turned away from Z.
        var dave = await ReserveAsync(api.Client, showId, User("dave"), ["Z"], "k");
        Assert.True(dave.Is(HttpStatusCode.Conflict, "seat_taken"), dave.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task A_cancel_racing_a_hundred_reserves_for_its_seat_frees_it_for_at_most_one_of_them(int round)
    {
        var showId = await CreateShowAsync(api.Client, ["Z", "Y"]);
        var alice = User($"alice{round}");
        var alices = await ReservedAsync(showId, alice, "Z");
        var deadlocksBefore = await DeadlocksAsync();

        // Index 0 is alice's cancel; 1..100 are other users reserving Z, all released from the same gate. The reserves are
        // spread over 1–100 ms so that some land before the cancel commits and some after, and the seat really changes hands.
        var results = await TestConcurrency.ConcurrentAsync(101, async i =>
        {
            if (i == 0)
            {
                return await CancelAsync(api.Client, alices.ReservationId, alice);
            }

            await Task.Delay(i);
            return await ReserveAsync(api.Client, showId, User($"u{round}-{i}"), ["Z"], "k");
        });
        var cancel = results[0];
        var reserves = results[1..];

        var mix = Histogram(reserves);
        output.WriteLine($"round {round}: {mix}");
        Assert.True(cancel.Is(HttpStatusCode.OK), cancel.ToString());
        Assert.Equal("cancelled", cancel.Body.GetProperty("status").GetString());
        Assert.All(reserves, r => Assert.True(r.Is(HttpStatusCode.Created) || r.Is(HttpStatusCode.Conflict, "seat_taken"), mix));
        var winners = reserves.Where(r => r.Is(HttpStatusCode.Created)).ToArray();
        Assert.True(winners.Length <= 1, mix);

        // Final state agrees with the responses: alice's reservation is cancelled, and Z is either the winner's or free.
        Assert.Equal(1, await ScalarAsync(Db, $"SELECT count(*) FROM reservations WHERE id = '{alices.ReservationId}' AND status = 'cancelled'"));
        Assert.Equal(winners.Length, await ScalarAsync(Db, $"SELECT count(*) FROM reservations WHERE show_id = '{showId}' AND status = 'confirmed'"));
        if (winners is [var winner])
        {
            Assert.Equal(1, await ScalarAsync(Db,
                $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label = 'Z' AND status = 'confirmed' AND reservation_id = '{winner.ReservationId}'"));
        }
        else
        {
            // Every reserve saw Z before the cancel committed; the seat must really be free now.
            Assert.Equal(1, await ScalarAsync(Db,
                $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label = 'Z' AND status = 'available' AND user_id IS NULL AND reservation_id IS NULL"));
            Assert.True((await ReserveAsync(api.Client, showId, User($"late{round}"), ["Z"], "k")).Is(HttpStatusCode.Created));
        }

        Assert.Equal(1, await ScalarAsync(Db, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND label = 'Y' AND status = 'available'"));

        await Task.Delay(TimeSpan.FromSeconds(1.5));   // backends flush statistics about once a second
        Assert.Equal(deadlocksBefore, await DeadlocksAsync());
    }

    [Fact]
    public async Task Fifty_parallel_cancels_by_the_owner_change_state_once_and_all_return_200()
    {
        var showId = await CreateShowAsync(api.Client, ["Z", "Y"]);
        var alice = User("alice");
        var alices = await ReserveAsync(api.Client, showId, alice, ["Z", "Y"], "k");
        Assert.True(alices.Is(HttpStatusCode.Created), alices.ToString());
        var cancelledBefore = api.Metrics.Cancelled;

        var results = await TestConcurrency.ConcurrentAsync(50, _ => CancelAsync(api.Client, alices.ReservationId, alice));

        Assert.All(results, r => Assert.True(r.Is(HttpStatusCode.OK), Histogram(results)));

        // One state change: one cancelled_at shared by every response, and the metric moved once.
        var bodies = results.Select(r => r.Body.GetRawText()).Distinct().ToArray();
        Assert.Single(bodies);
        Assert.Equal("cancelled", results[0].Body.GetProperty("status").GetString());
        Assert.Equal(cancelledBefore + 1, api.Metrics.Cancelled);

        Assert.Equal(2, await ScalarAsync(Db, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND status = 'available' AND reservation_id IS NULL"));
        Assert.Equal(1, await ScalarAsync(Db, $"SELECT count(*) FROM reservations WHERE id = '{alices.ReservationId}' AND status = 'cancelled'"));
    }
}
