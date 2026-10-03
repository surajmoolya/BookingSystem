using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>
/// Collects every Serilog event of a host. Register it as an <see cref="ILogEventSink"/> through the factory's
/// <c>configureServices</c>; the app's <c>ReadFrom.Services</c> adds it next to the console sink.
/// </summary>
public sealed class CapturingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events => [.. _events];

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    /// <summary>Every captured event as the production formatter writes it: one rendered compact JSON object per line.</summary>
    public string RenderAll()
    {
        var formatter = new RenderedCompactJsonFormatter();
        using var writer = new StringWriter();
        foreach (var e in Events)
        {
            formatter.Format(e, writer);
        }

        return writer.ToString();
    }

    public static string? Scalar(LogEvent e, string property) =>
        e.Properties.TryGetValue(property, out var value) && value is ScalarValue { Value: var v } ? v?.ToString() : null;

    /// <summary>The <c>http.request</c> completion events.</summary>
    public IReadOnlyList<LogEvent> RequestEvents() => Events.Where(e => Scalar(e, "EventName") == "http.request").ToList();
}
