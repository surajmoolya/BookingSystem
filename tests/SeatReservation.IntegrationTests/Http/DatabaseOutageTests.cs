using System.Diagnostics;
using System.Net;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>
/// A database that goes away mid-run (T-6.6, D-056): after the retries, DB-bound requests answer an honest
/// 503 <c>dependency_unavailable</c> with <c>Retry-After</c>, promptly, never a 500 and never a hang. Uses its own
/// container (not the shared <see cref="PostgresFixture"/>) because the test stops it.
/// </summary>
public sealed class DatabaseOutageTests : IAsyncLifetime
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(20);

    private readonly PostgresFixture _postgres = new();

    public Task InitializeAsync() => _postgres.InitializeAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync();

    [Fact]
    public async Task Requests_after_the_database_stops_are_503_dependency_unavailable_not_500_or_a_hang()
    {
        await using var factory = await ApiFactory.StartAsync(_postgres, new Dictionary<string, string?> { ["Database:ConnectionTimeoutSeconds"] = "5" });
        using var client = factory.CreateClient();
        var showId = await CreateShowAsync(client, Labels(4));
        var first = await ReserveAsync(client, showId, "alice", ["S1"], "before-outage");
        Assert.True(first.Is(HttpStatusCode.Created), first.ToString());

        await _postgres.StopAsync();

        var stopwatch = Stopwatch.StartNew();
        var reserve = await ReserveAsync(client, showId, "bob", ["S2"], "during-outage");
        stopwatch.Stop();
        Assert.True(reserve.Is(HttpStatusCode.ServiceUnavailable, "dependency_unavailable"), reserve.ToString());
        Assert.True(stopwatch.Elapsed < Prompt, $"reserve took {stopwatch.Elapsed}");

        var cancel = await CancelAsync(client, first.ReservationId, "alice");
        Assert.True(cancel.Is(HttpStatusCode.ServiceUnavailable, "dependency_unavailable"), cancel.ToString());

        using (var show = await client.GetAsync($"/shows/{showId}"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, show.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), show.Headers.RetryAfter?.Delta);
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
}
