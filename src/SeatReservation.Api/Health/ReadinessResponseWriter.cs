using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SeatReservation.Api.Health;

/// <summary>
/// The <c>/health/ready</c> body (lld §5.7):
/// <c>{"status":"Healthy","checks":{"database":{"status":"Healthy","duration_ms":3},"migrations":{"status":"Healthy","duration_ms":0}}}</c>.
/// A failing check adds its <c>description</c>, which the checks keep free of hosts and connection details.
/// </summary>
public static class ReadinessResponseWriter
{
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        await using var json = new Utf8JsonWriter(context.Response.Body);
        json.WriteStartObject();
        json.WriteString("status", report.Status.ToString());
        json.WriteStartObject("checks");
        foreach (var (name, entry) in report.Entries)
        {
            json.WriteStartObject(name);
            json.WriteString("status", entry.Status.ToString());
            json.WriteNumber("duration_ms", (long)entry.Duration.TotalMilliseconds);
            if (entry.Status != HealthStatus.Healthy && entry.Description is { } description)
            {
                json.WriteString("description", description);
            }

            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteEndObject();
        await json.FlushAsync(context.RequestAborted);
    }
}
