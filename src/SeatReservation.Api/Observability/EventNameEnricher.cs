using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace SeatReservation.Api.Observability;

/// <summary>
/// Gives every event an <c>EventName</c> property (lld §10), so a log query can filter on it in any layer:
/// the <see cref="Microsoft.Extensions.Logging.EventId"/> name when the event has one (the logic layer's
/// <c>LogEvents</c>), otherwise the dotted event name our message templates start with (<c>db.retry …</c>,
/// <c>migrations.applied …</c>, <c>unhandled.exception …</c>). Events that already set it (the request completion
/// line) and framework events with neither are left alone.
/// </summary>
public sealed partial class EventNameEnricher : ILogEventEnricher
{
    public const string PropertyName = "EventName";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.ContainsKey(PropertyName))
        {
            return;
        }

        var name = FromEventId(logEvent) ?? FromTemplate(logEvent.MessageTemplate.Text);
        if (name is not null)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, name));
        }
    }

    private static string? FromEventId(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("EventId", out var id)
        && id is StructureValue structure
        && structure.Properties.FirstOrDefault(p => p.Name == "Name")?.Value is ScalarValue { Value: string name }
        && LeadingEventName().IsMatch(name)
            ? name
            : null;

    private static string? FromTemplate(string template)
    {
        var match = LeadingEventName().Match(template);
        return match.Success ? match.Value : null;
    }

    // Lower-case dotted words at the very start, e.g. "reservation.confirmed" or "migrations.pool_warmed".
    [GeneratedRegex(@"^[a-z][a-z_]*(\.[a-z][a-z_]*)+(?=$|[\s;:,])")]
    private static partial Regex LeadingEventName();
}
