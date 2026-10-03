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
    private static readonly string[] RequiredNames =
    [
        "http_requests_received_total", "http_request_duration_seconds", "http_requests_in_progress",
        "reservations_confirmed_total", "reservation_seats_confirmed_total", "reservations_declined_total", "reservations_cancelled_total",
        "reservation_duration_seconds",
        "show_seats", "show_seats_total", "seats_available", "seats_held", "seats_confirmed", "seats_total", "seats_gauge_stale",
        "db_up", "db_query_duration_seconds", "db_errors_total", "db_transaction_retries_total",
    ];

    private static readonly string[] DeclineReasons =
        ["seat_taken", "per_user_limit", "idempotent_replay", "idempotency_key_conflict", "unknown_seat", "show_not_found", "validation"];

    private readonly string _alice = $"alice-{Guid.NewGuid():N}"[..14];
    private readonly string _bob = $"bob-{Guid.NewGuid():N}"[..12];

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

    [Fact]
    public async Task Every_required_metric_is_exported()
    {
        var showId = await CreateShowAsync(api.Client, Labels(1));   // one show and one request, so the labelled series exist
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S1"], "k-names")).Is(HttpStatusCode.Created));

        var scrape = await MetricsScrape.FetchAsync(api.Client);

        Assert.All(RequiredNames, name => Assert.Contains(name, scrape.DeclaredNames));
        Assert.Equal(DeclineReasons.Order(StringComparer.Ordinal), scrape.Named("reservations_declined_total").Select(s => s.Labels["reason"]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_new_reservation_counts_one_confirmed_and_its_seats_and_no_decline()
    {
        var showId = await CreateShowAsync(api.Client, Labels(4));
        var before = await MetricsScrape.FetchAsync(api.Client);

        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S1", "S2", "S3"], "k1")).Is(HttpStatusCode.Created));

        var after = await MetricsScrape.FetchAsync(api.Client);
        Assert.Equal(1, Delta(before, after, "reservations_confirmed_total"));
        Assert.Equal(3, Delta(before, after, "reservation_seats_confirmed_total"));
        Assert.Equal(0, Delta(before, after, "reservations_declined_total"));
    }

    [Fact]
    public async Task A_replay_counts_only_idempotent_replay()
    {
        var showId = await CreateShowAsync(api.Client, Labels(2));
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S1"], "k1")).Is(HttpStatusCode.Created));
        var before = await MetricsScrape.FetchAsync(api.Client);

        var replay = await ReserveAsync(api.Client, showId, _alice, ["S1"], "k1");
        Assert.True(replay.Is(HttpStatusCode.OK) && replay.Replayed);

        var after = await MetricsScrape.FetchAsync(api.Client);
        Assert.Equal(0, Delta(before, after, "reservations_confirmed_total"));
        Assert.Equal(0, Delta(before, after, "reservation_seats_confirmed_total"));
        Assert.Equal(1, Delta(before, after, "reservations_declined_total", ("reason", "idempotent_replay")));
        Assert.Equal(1, Delta(before, after, "reservations_declined_total"));
    }

    [Fact]
    public async Task Each_decline_reason_counts_once_under_its_own_label()
    {
        var showId = await CreateShowAsync(api.Client, Labels(6));
        Assert.True((await ReserveAsync(api.Client, showId, _bob, ["S1"], "b1")).Is(HttpStatusCode.Created));
        Assert.True((await ReserveAsync(api.Client, showId, _alice, ["S2"], "a1")).Is(HttpStatusCode.Created));

        var cases = new (string Reason, Func<Task<ReserveResult>> Act)[]
        {
            ("seat_taken", () => ReserveAsync(api.Client, showId, _alice, ["S1"], "a2")),
            ("per_user_limit", () => ReserveAsync(api.Client, showId, _alice, ["S2", "S3", "S4", "S5", "S6"], "a3")),
            ("idempotent_replay", () => ReserveAsync(api.Client, showId, _alice, ["S2"], "a1")),
            ("idempotency_key_conflict", () => ReserveAsync(api.Client, showId, _alice, ["S3"], "a1")),
            ("unknown_seat", () => ReserveAsync(api.Client, showId, _alice, ["Z9"], "a4")),
            ("show_not_found", () => ReserveAsync(api.Client, Guid.NewGuid(), _alice, ["S3"], "a5")),
            ("validation", () => ReserveAsync(api.Client, showId, _alice, [], "a6")),
        };

        foreach (var (reason, act) in cases)
        {
            var before = await MetricsScrape.FetchAsync(api.Client);
            var result = await act();
            var after = await MetricsScrape.FetchAsync(api.Client);

            Assert.True((int)result.Status is 200 or 400 or 404 or 409, $"{reason}: {result}");
            Assert.All(DeclineReasons, r => Assert.True(
                Delta(before, after, "reservations_declined_total", ("reason", r)) == (r == reason ? 1 : 0),
                $"{reason}: reason {r} moved by {Delta(before, after, "reservations_declined_total", ("reason", r))} ({result})"));
            Assert.Equal(0, Delta(before, after, "reservations_confirmed_total"));
        }
    }

    [Fact]
    public async Task A_cancel_counts_once_and_a_repeat_cancel_does_not()
    {
        var showId = await CreateShowAsync(api.Client, Labels(2));
        var reserved = await ReserveAsync(api.Client, showId, _alice, ["S1"], "k1");
        var before = await MetricsScrape.FetchAsync(api.Client);

        Assert.True((await CancelAsync(api.Client, reserved.ReservationId, _alice)).Is(HttpStatusCode.OK));
        var once = await MetricsScrape.FetchAsync(api.Client);
        Assert.True((await CancelAsync(api.Client, reserved.ReservationId, _alice)).Is(HttpStatusCode.OK));
        var twice = await MetricsScrape.FetchAsync(api.Client);

        Assert.Equal(1, Delta(before, once, "reservations_cancelled_total"));
        Assert.Equal(0, Delta(once, twice, "reservations_cancelled_total"));
    }

    [Fact]
    public async Task No_series_carries_a_user_reservation_or_request_id_and_show_id_only_on_show_seats()
    {
        var showId = await CreateShowAsync(api.Client, Labels(2));
        var reserved = await ReserveAsync(api.Client, showId, _alice, ["S1"], "k1");
        Assert.True((await CancelAsync(api.Client, reserved.ReservationId, _alice)).Is(HttpStatusCode.OK));
        await api.Client.GetAsync($"/reservations/{reserved.ReservationId}");

        var scrape = await MetricsScrape.FetchAsync(api.Client);

        string[] forbidden = ["user_id", "reservation_id", "request_id", "correlation_id", "idempotency_key"];
        Assert.DoesNotContain(scrape.Samples, s => s.Labels.Keys.Any(k => forbidden.Contains(k)));
        Assert.All(scrape.Samples.Where(s => s.Labels.ContainsKey("show_id")), s => Assert.StartsWith("show_seats", s.Name));
        Assert.DoesNotContain(scrape.Samples, s => s.Labels.Values.Any(v => v.Contains(_alice, StringComparison.Ordinal)
            || v.Contains(reserved.ReservationId.ToString(), StringComparison.OrdinalIgnoreCase)));
    }

    private static double Delta(MetricsScrape before, MetricsScrape after, string name, params (string Label, string Value)[] labels) =>
        after.Value(name, labels) - before.Value(name, labels);
}
