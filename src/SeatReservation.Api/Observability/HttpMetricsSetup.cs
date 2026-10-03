using Prometheus;
using Prometheus.HttpMetrics;

namespace SeatReservation.Api.Observability;

/// <summary>
/// prometheus-net wiring for <c>/metrics</c> and the HTTP request metrics (lld §9).
/// Every instrument lives in one <see cref="CollectorRegistry"/> owned by the host (D-097), not the static default
/// registry, so two hosts in one process (the integration tests) never share counters or scrape callbacks.
/// HTTP labels are <c>code</c>, <c>method</c> and <c>endpoint</c> only. <c>endpoint</c> is the route template
/// as ASP.NET Core stores it (<c>shows/{id}/reserve</c>, no leading slash), never the raw path, so ids never become label values.
/// </summary>
public static class HttpMetricsSetup
{
    private static readonly string[] RequestLabels = [HttpRequestLabelNames.Code, HttpRequestLabelNames.Method, HttpRequestLabelNames.Endpoint];
    private static readonly string[] InProgressLabels = [HttpRequestLabelNames.Method, HttpRequestLabelNames.Endpoint];

    /// <summary>lld §9 buckets: fine below 100 ms for the steady state, wide enough to see a 30 s queue during a burst.</summary>
    internal static readonly double[] DurationBuckets =
        [.001, .0025, .005, .01, .025, .05, .075, .1, .15, .25, .4, .6, .8, 1, 1.5, 2, 3, 5, 8, 13, 20, 30];

    /// <summary>
    /// Registers the host's registry and its <see cref="IMetricFactory"/> (used by every layer's metrics), with the
    /// <c>process_*</c> / <c>dotnet_*</c> collectors. The default registry's EventCounter and all-meters bridges are not
    /// used: they export many series we don't read (including ASP.NET Core's own duplicate request metrics) and
    /// sample on a timer, which costs CPU on a small instance. Only Npgsql's meter is bridged (<see cref="NpgsqlPoolMetrics"/>).
    /// </summary>
    public static IServiceCollection AddMetricsRegistry(this IServiceCollection services)
    {
        var registry = Metrics.NewCustomRegistry();
        DotNetStats.Register(registry);
        services.AddSingleton(registry);
        services.AddSingleton<IMetricFactory>(Metrics.WithCustomRegistry(registry));
        services.AddSingleton<SeatGaugeCollector>();
        services.AddSingleton<NpgsqlPoolMetrics>();
        return services;
    }

    /// <summary>Call after <c>UseRouting()</c>, so the endpoint's route template is known when the label is taken.</summary>
    public static IApplicationBuilder UseRouteTemplateHttpMetrics(this IApplicationBuilder app)
    {
        var factory = app.ApplicationServices.GetRequiredService<IMetricFactory>();
        return app.UseHttpMetrics(options =>
        {
            options.SetMetricFactory(factory);
            options.RequestCount.Counter = factory.CreateCounter(
                "http_requests_received_total",
                "Provides the count of HTTP requests that have been processed by the ASP.NET Core pipeline.",
                new CounterConfiguration { LabelNames = RequestLabels });
            options.RequestDuration.Histogram = factory.CreateHistogram(
                "http_request_duration_seconds",
                "The duration of HTTP requests processed by an ASP.NET Core application.",
                new HistogramConfiguration { LabelNames = RequestLabels, Buckets = DurationBuckets });
            options.InProgress.Gauge = factory.CreateGauge(
                "http_requests_in_progress",
                "The number of requests currently in progress in the ASP.NET Core pipeline.",
                new GaugeConfiguration { LabelNames = InProgressLabels });
        });
    }

    /// <summary>
    /// Maps <c>GET /metrics</c> onto the host's registry (anonymous, outside any rate limit), and creates the seat gauge
    /// collector (its constructor hooks its refresh into every scrape) and the Npgsql pool bridge.
    /// </summary>
    public static IEndpointConventionBuilder MapHostMetrics(this IEndpointRouteBuilder endpoints)
    {
        _ = endpoints.ServiceProvider.GetRequiredService<SeatGaugeCollector>();
        _ = endpoints.ServiceProvider.GetRequiredService<NpgsqlPoolMetrics>();
        return endpoints.MapMetrics("/metrics", endpoints.ServiceProvider.GetRequiredService<CollectorRegistry>())
            .AllowAnonymous();
    }
}
