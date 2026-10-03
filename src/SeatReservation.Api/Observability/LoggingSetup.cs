using Microsoft.AspNetCore.Routing.Patterns;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace SeatReservation.Api.Observability;

/// <summary>
/// Serilog as the only logging provider (lld §10, D-088): rendered compact JSON, one object per line on stdout, written
/// through a bounded async buffer that drops events when full instead of blocking a request. Levels come from the
/// <c>Serilog</c> configuration section. Sinks registered in DI as <see cref="Serilog.Core.ILogEventSink"/> are added
/// too (the integration tests capture logs that way).
/// </summary>
public static class LoggingSetup
{
    public const string AppName = "seat-reservation";
    public const string RequestEventName = "http.request";

    /// <summary>Events buffered between the request threads and stdout; beyond this they are dropped (D-088).</summary>
    public const int AsyncBufferSize = 10_000;

    public static WebApplicationBuilder AddStructuredLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, logger) => logger
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.With<EventNameEnricher>()
            .Enrich.WithProperty("App", AppName)
            .Enrich.WithProperty("Env", context.HostingEnvironment.EnvironmentName)
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .WriteTo.Async(
                sink => sink.Console(new RenderedCompactJsonFormatter()),
                bufferSize: AsyncBufferSize,
                blockWhenFull: false),
            preserveStaticLogger: true);   // per-host logger; never touch the global Log.Logger (tests run many hosts)
        return builder;
    }

    /// <summary>
    /// One completion line per request (<c>http.request</c>): method, route template, status and elapsed time, plus
    /// whatever the controller added through <c>IDiagnosticContext</c>. Never headers, never the query string.
    /// 5xx is Error; health probes and scrapes are Debug so Render's probe every few seconds doesn't flood the log.
    /// </summary>
    public static IApplicationBuilder UseRequestCompletionLogging(this IApplicationBuilder app) =>
        app.UseSerilogRequestLogging(options =>
        {
            // The host's logger: with preserveStaticLogger the middleware would otherwise write to the empty static Log.Logger.
            options.Logger = app.ApplicationServices.GetRequiredService<Serilog.ILogger>();
            options.MessageTemplate = "http.request {RequestMethod:l} {Endpoint:l} responded {StatusCode} in {Elapsed:0.0} ms";
            options.GetLevel = Level;
            options.EnrichDiagnosticContext = (diagnostics, http) =>
            {
                diagnostics.Set("EventName", RequestEventName);
                diagnostics.Set("Endpoint", RouteTemplate(http));
                diagnostics.Set("RequestId", http.TraceIdentifier);
            };
        });

    internal static LogEventLevel Level(HttpContext http, double elapsedMs, Exception? exception)
    {
        if (exception is not null || http.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            return LogEventLevel.Error;
        }

        var path = http.Request.Path;
        return path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics")
            ? LogEventLevel.Debug
            : LogEventLevel.Information;
    }

    // The route template, like the metrics' endpoint label; a request that matched no route logs "unmatched".
    private static string RouteTemplate(HttpContext http) =>
        http.GetEndpoint() is RouteEndpoint { RoutePattern: RoutePattern pattern } ? pattern.RawText ?? "unknown" : "unmatched";
}
