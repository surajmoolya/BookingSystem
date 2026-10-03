using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>A ready host whose seat gauges keep only the 3 most recent shows and never cache, for the bounded-series test.</summary>
public sealed class SmallGaugeFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public const int MaxShows = 3;

    public ApiFactory Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = await ApiFactory.StartAsync(postgres, new Dictionary<string, string?>
        {
            ["Metrics:MaxShowsInGauges"] = MaxShows.ToString(),
            ["Metrics:SeatGaugeCacheSeconds"] = "0",
        });
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>DB-backed seat gauges (T-5.4, lld §9, D-086): <c>show_seats</c> reconciles with <c>GET /shows/{id}</c>.</summary>
[Collection(PostgresCollection.Name)]
public class SeatGaugeTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private readonly string _alice = $"alice-{Guid.NewGuid():N}"[..14];
    private readonly string _bob = $"bob-{Guid.NewGuid():N}"[..12];

    [Fact]
    public async Task Per_show_gauges_equal_the_show_counts_after_reserves_and_a_cancel()
    {
        var showId = await CreateShowAsync(api.Client, Labels(6));
        await AssertGaugesMatchShowAsync(api.Client, showId, expectedConfirmed: 0);

        var first = await ReserveAsync(api.Client, showId, _alice, ["S1", "S2"], "k1");
        Assert.True(first.Is(HttpStatusCode.Created));
        Assert.True((await ReserveAsync(api.Client, showId, _bob, ["S3"], "k1")).Is(HttpStatusCode.Created));
        await AssertGaugesMatchShowAsync(api.Client, showId, expectedConfirmed: 3);

        Assert.True((await CancelAsync(api.Client, first.ReservationId, _alice)).Is(HttpStatusCode.OK));
        await AssertGaugesMatchShowAsync(api.Client, showId, expectedConfirmed: 1);
    }

    [Fact]
    public async Task Global_gauges_equal_the_seats_table()
    {
        var showId = await CreateShowAsync(api.Client, Labels(3));
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S1"], "k1")).Is(HttpStatusCode.Created));

        // Other tests in this class write too, so compare against the table at a moment when the gauges have caught up.
        await Eventually(async () =>
        {
            var scrape = await MetricsScrape.FetchAsync(api.Client);
            var cs = api.Factory.ConnectionString;
            return scrape.Value("seats_available") == await ScalarAsync(cs, "SELECT count(*) FROM seats WHERE status = 'available'")
                && scrape.Value("seats_confirmed") == await ScalarAsync(cs, "SELECT count(*) FROM seats WHERE status = 'confirmed'")
                && scrape.Value("seats_total") == await ScalarAsync(cs, "SELECT count(*) FROM seats")
                && scrape.Value("seats_held") == 0
                && scrape.Value("seats_gauge_stale") == 0;
        });
    }

    [Fact]
    public async Task Every_state_is_exported_even_when_zero()
    {
        var showId = await CreateShowAsync(api.Client, Labels(2));
        await Eventually(async () =>
        {
            var series = (await MetricsScrape.FetchAsync(api.Client)).Named("show_seats").Where(s => s.Has("show_id", showId.ToString())).ToList();
            return series.Select(s => s.Labels["state"]).Order(StringComparer.Ordinal).SequenceEqual(["available", "confirmed", "held"]);
        });
    }

    private static readonly string[] States = ["available", "held", "confirmed"];

    internal static async Task AssertGaugesMatchShowAsync(HttpClient client, Guid showId, int expectedConfirmed)
    {
        var counts = (await client.GetFromJsonAsync<JsonElement>($"/shows/{showId}")).GetProperty("counts");
        Assert.Equal(expectedConfirmed, counts.GetProperty("confirmed").GetInt32());

        // The gauges may be up to Metrics:SeatGaugeCacheSeconds (1 s) behind the API; wait that out.
        var id = showId.ToString();
        MetricsScrape? last = null;
        await Eventually(async () =>
        {
            last = await MetricsScrape.FetchAsync(client);
            return States.All(state =>
                    last.Value("show_seats", ("show_id", id), ("state", state)) == counts.GetProperty(state).GetInt32()
                    && last.Named("show_seats").Any(s => s.Has("show_id", id) && s.Has("state", state)))
                && last.Value("show_seats_total", ("show_id", id)) == counts.GetProperty("total").GetInt32();
        }, () => $"gauges never matched {counts}; last: " + string.Join(", ",
            last?.Samples.Where(s => s.Has("show_id", id)).Select(s => $"{s.Name}{{{string.Join(",", s.Labels.Values)}}}={s.Value}") ?? []));
    }

    internal static async Task Eventually(Func<Task<bool>> condition, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, describe?.Invoke() ?? "condition not met within 5 s");
            await Task.Delay(100);
        }
    }
}

/// <summary>The per-show series stay bounded by <c>Metrics:MaxShowsInGauges</c> (D-086).</summary>
[Collection(PostgresCollection.Name)]
public class SeatGaugeBoundTests(SmallGaugeFixture api) : IClassFixture<SmallGaugeFixture>
{
    [Fact]
    public async Task Only_the_most_recent_shows_have_series_and_older_ones_are_removed()
    {
        var shows = new List<Guid>();
        for (var i = 0; i < SmallGaugeFixture.MaxShows + 2; i++)
        {
            shows.Add(await CreateShowAsync(api.Client, Labels(2)));
            _ = await MetricsScrape.FetchAsync(api.Client);   // each scrape refreshes (cache 0), so older shows had series once
        }

        var scrape = await MetricsScrape.FetchAsync(api.Client);
        var exported = scrape.Named("show_seats").Select(s => s.Labels["show_id"]).Distinct().ToHashSet();
        Assert.Equal(shows.TakeLast(SmallGaugeFixture.MaxShows).Select(s => s.ToString()).ToHashSet(), exported);
        Assert.Equal(SmallGaugeFixture.MaxShows * 3, scrape.Named("show_seats").Count());
        Assert.Equal(SmallGaugeFixture.MaxShows, scrape.Named("show_seats_total").Count());
    }
}
