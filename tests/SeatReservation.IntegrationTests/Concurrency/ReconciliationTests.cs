using System.Net;
using System.Text.Json;
using SeatReservation.IntegrationTests.Infrastructure;
using Xunit.Abstractions;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Concurrency;

/// <summary>Show state reconciles in every snapshot read while a reserve burst is mutating it (T-3.14).</summary>
[Collection(PostgresCollection.Name)]
public class ReconciliationTests(ApiFixture api, ITestOutputHelper output) : IClassFixture<ApiFixture>
{
    private const int Requests = 500;
    private const int SeatCount = 200;

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Every_snapshot_taken_during_a_reserve_burst_reconciles()
    {
        var seed = Random.Shared.Next();
        output.WriteLine($"seed {seed}");
        var random = new Random(seed);
        var showId = await CreateShowAsync(api.Client, Labels(SeatCount));

        // 1-2 random seats each over 200 seats: ~750 seat asks, so the show fills up gradually and some requests
        // collide (409 seat_taken). Distinct users, so the per-user limit never decides.
        var picks = Enumerable.Range(0, Requests)
            .Select(_ => Labels(SeatCount).OrderBy(_ => random.Next()).Take(random.Next(1, 3)).ToArray())
            .ToArray();

        // The poller reads alongside the burst and keeps going until it ends; checks happen afterwards so a failure
        // reports the offending snapshot rather than faulting a background task.
        var snapshots = new List<(HttpStatusCode Status, string Body)>();
        using var burstDone = new CancellationTokenSource();
        var poller = Task.Run(async () =>
        {
            while (!burstDone.IsCancellationRequested)
            {
                snapshots.Add(await ReadShowAsync(showId));
            }
        });

        var results = await TestConcurrency.ConcurrentAsync(Requests, i =>
            ReserveAsync(api.Client, showId, $"u{i}-{_run}", picks[i], "k"));
        await burstDone.CancelAsync();
        await poller;
        var duringBurst = snapshots.Count;
        snapshots.Add(await ReadShowAsync(showId));

        var mix = Histogram(results);
        output.WriteLine(mix);
        output.WriteLine($"snapshots during burst {duringBurst}");
        Assert.DoesNotContain(results, r => (int)r.Status >= 500);
        // Each GET queues behind the burst, so only ~10 land during it locally; 3 keeps slower CI runners from flaking.
        Assert.True(duringBurst >= 3, $"only {duringBurst} snapshots taken during the burst");

        var previousConfirmed = 0;
        var confirmedSeen = new List<int>();
        foreach (var (status, text) in snapshots)
        {
            Assert.True(status == HttpStatusCode.OK, $"snapshot returned {(int)status}: {text}");
            var body = JsonDocument.Parse(text).RootElement;
            var counts = body.GetProperty("counts");
            int total = counts.GetProperty("total").GetInt32(),
                available = counts.GetProperty("available").GetInt32(),
                held = counts.GetProperty("held").GetInt32(),
                confirmed = counts.GetProperty("confirmed").GetInt32();

            Assert.True(available + held + confirmed == total, $"counts don't add up: {counts}");
            Assert.Equal(SeatCount, total);

            // The per-seat array and the counts must describe the same instant.
            var byStatus = body.GetProperty("seats").EnumerateArray()
                .GroupBy(s => s.GetProperty("status").GetString()!)
                .ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(total, byStatus.Values.Sum());
            Assert.Equal(available, byStatus.GetValueOrDefault("available"));
            Assert.Equal(held, byStatus.GetValueOrDefault("held"));
            Assert.Equal(confirmed, byStatus.GetValueOrDefault("confirmed"));

            // No cancels in this test, so a confirmed seat can never be seen going back.
            Assert.True(confirmed >= previousConfirmed, $"confirmed went {previousConfirmed} -> {confirmed}");
            previousConfirmed = confirmed;
            confirmedSeen.Add(confirmed);
        }

        // Printed, not asserted (timing-dependent): shows the burst was observed mid-flight, not only before or after.
        var finalConfirmed = previousConfirmed;
        var intermediate = confirmedSeen.Where(c => c > 0 && c < finalConfirmed).Distinct().Count();
        output.WriteLine($"distinct intermediate confirmed values {intermediate}, final {finalConfirmed}");
        output.WriteLine($"confirmed per snapshot: {string.Join(" ", confirmedSeen)}");

        var wonSeats = results.Where(r => r.Is(HttpStatusCode.Created)).Sum(r => r.Seats.Length);
        Assert.Equal(wonSeats, finalConfirmed);
        Assert.Equal(finalConfirmed, await ScalarAsync(api.Factory.ConnectionString,
            $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND status = 'confirmed'"));
    }

    // Raw text, so a non-200 with a non-JSON body still reaches the assertion that names it.
    private async Task<(HttpStatusCode, string)> ReadShowAsync(Guid showId)
    {
        using var response = await api.Client.GetAsync($"/shows/{showId}");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
