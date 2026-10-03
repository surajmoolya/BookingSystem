using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using SeatReservation.Api.Observability;

namespace SeatReservation.Api.Hosting;

/// <summary>
/// Turns a configuration that fails validation at startup (a missing signing key outside Development, an out-of-range
/// pool size, …) into one JSON line, <c>startup.config_invalid</c>, that names every offending key, and exit code 1.
/// The host's own "Hosting failed to start" line buries the cause in a stack trace, and the unhandled-exception dump
/// that would follow it isn't JSON at all.
/// </summary>
public static class StartupFailure
{
    public const string EventName = "startup.config_invalid";

    /// <summary>Runs the app; on invalid configuration logs <see cref="EventName"/> and returns 1 instead of crashing.</summary>
    public static int RunOrExplain(this WebApplication app)
    {
        var environment = app.Environment.EnvironmentName;   // read now: the container is disposed once Run throws
        try
        {
            app.Run();
            return 0;
        }
        catch (OptionsValidationException ex)
        {
            Write(ex, environment, Console.Out);
            return 1;
        }
    }

    // The host's logger is already disposed by now, so this writes through a one-off logger in the same JSON format.
    public static void Write(OptionsValidationException ex, string environment, TextWriter output)
    {
        using var logger = new LoggerConfiguration()
            .Enrich.WithProperty("EventName", EventName)
            .Enrich.WithProperty("App", LoggingSetup.AppName)
            .Enrich.WithProperty("Env", environment)
            .WriteTo.Sink(new JsonLineSink(output))
            .CreateLogger();

        logger.Fatal("startup.config_invalid {OptionsType:l}: {Failures}", ex.OptionsType.Name, ex.Failures);
    }

    private sealed class JsonLineSink(TextWriter output) : ILogEventSink
    {
        private readonly RenderedCompactJsonFormatter _formatter = new();

        public void Emit(LogEvent logEvent)
        {
            _formatter.Format(logEvent, output);
            output.Flush();
        }
    }
}
