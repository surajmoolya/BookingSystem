using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Api.Observability;
using SeatReservation.Application.Abstractions;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Http;

/// <summary><c>GET /metrics</c> (lld §9, D-086, D-097). Each class fixture is its own host, so its own registry.</summary>
[Collection(PostgresCollection.Name)]
public class MetricsApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private readonly string _alice = $"alice-{Guid.NewGuid():N}"[..14];

    [Fact]
    public async Task Http_metrics_are_labelled_with_the_route_template_not_the_raw_path()
    {
        var showId = await CreateShowAsync(api.Client, Labels(2));
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S1"], "k1")).Is(HttpStatusCode.Created));

        var scrape = await MetricsScrape.FetchAsync(api.Client);

        var reserve = scrape.Named("http_requests_received_total").Where(s => s.Has("endpoint", "shows/{id}/reserve")).ToList();
        Assert.Contains(reserve, s => s.Has("code", "201") && s.Has("method", "POST") && s.Value >= 1);
        Assert.Contains(scrape.Named("http_request_duration_seconds_bucket"), s => s.Has("endpoint", "shows/{id}/reserve") && s.Has("le", "0.0025"));
        Assert.Contains(scrape.Named("http_request_duration_seconds_bucket"), s => s.Has("le", "30"));
        Assert.Contains("http_requests_in_progress", scrape.DeclaredNames);

        // No series carries an id (the per-show seat gauges are the one exception, D-086).
        Assert.DoesNotContain(scrape.Samples.Where(s => !s.Name.StartsWith("show_seats", StringComparison.Ordinal)), s => s.Labels.Values.Any(v => v.Contains(showId.ToString(), StringComparison.OrdinalIgnoreCase)));
        Assert.All(scrape.Named("http_requests_received_total"), s => Assert.Equal(["code", "endpoint", "method"], s.Labels.Keys.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task Process_and_runtime_collectors_are_exported() =>
        Assert.Contains((await MetricsScrape.FetchAsync(api.Client)).DeclaredNames, n => n.StartsWith("process_", StringComparison.Ordinal));

    [Fact]
    public void The_logic_layer_records_into_the_prometheus_adapter_not_the_null_default() =>
        Assert.IsType<PrometheusReservationMetrics>(api.Factory.Services.GetRequiredService<IReservationMetrics>());

    [Fact]
    public async Task Reservation_duration_is_observed_by_outcome()
    {
        var before = await MetricsScrape.FetchAsync(api.Client);
        var showId = await CreateShowAsync(api.Client, Labels(2));
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S1"], "k-dur")).Is(HttpStatusCode.Created));
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S1"], "k-dur")).Is(HttpStatusCode.OK));

        var after = await MetricsScrape.FetchAsync(api.Client);
        Assert.Equal(1, Delta(before, after, "reservation_duration_seconds_count", ("outcome", "created")));
        Assert.Equal(1, Delta(before, after, "reservation_duration_seconds_count", ("outcome", "replayed")));
        Assert.Equal(
            ["created", "declined", "error", "replayed"],
            after.Named("reservation_duration_seconds_count").Select(s => s.Labels["outcome"]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Db_up_follows_the_readiness_probe()
    {
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(1, (await MetricsScrape.FetchAsync(api.Client)).Value("db_up"));

        await using var down = new ApiFactory(ApiFactory.UnreachableConnectionString);
        using var client = down.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        var scrape = await MetricsScrape.FetchAsync(client);   // the scrape itself still answers 200
        Assert.Equal(0, scrape.Value("db_up"));
        Assert.Equal(1, scrape.Value("seats_gauge_stale"));
    }

    [Fact]
    public async Task Npgsql_pool_metrics_are_bridged_with_the_pool_name_not_the_connection_string()
    {
        var showId = await CreateShowAsync(api.Client, Labels(1));
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S1"], "k-pool")).Is(HttpStatusCode.Created));

        var scrape = await MetricsScrape.FetchAsync(api.Client);

        // Npgsql's meter is process-wide, so other hosts' pools in this test process show up too; ours are named.
        Assert.Equal(40, scrape.Value("npgsql_db_client_connections_max", ("pool_name", "seatres-api")));
        Assert.Equal(3, scrape.Value("npgsql_db_client_connections_max", ("pool_name", "seatres-ops")));
        Assert.Contains(scrape.Named("npgsql_db_client_connections_usage"), s => s.Has("pool_name", "seatres-api") && s.Has("state", "idle"));
        Assert.Contains(scrape.Named("npgsql_db_client_connections_usage"), s => s.Has("pool_name", "seatres-api") && s.Has("state", "used"));
        Assert.Contains("npgsql_db_client_connections_create_time", scrape.DeclaredNames);
        Assert.DoesNotContain(scrape.DeclaredNames, n => n.StartsWith("npgsql_db_client_commands", StringComparison.Ordinal));
        Assert.DoesNotContain(scrape.Samples, s => s.Labels.Values.Any(v => v.Contains("Password", StringComparison.OrdinalIgnoreCase)));
    }

    private static double Delta(MetricsScrape before, MetricsScrape after, string name, params (string Label, string Value)[] labels) =>
        after.Value(name, labels) - before.Value(name, labels);
}
