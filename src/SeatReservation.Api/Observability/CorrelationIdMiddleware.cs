using System.Text.RegularExpressions;
using SeatReservation.Api.Errors;
using Serilog.Context;

namespace SeatReservation.Api.Observability;

/// <summary>
/// First in the pipeline (lld §10): takes an inbound <c>X-Correlation-ID</c> when it is a safe token, otherwise makes a
/// new one; echoes it on the response (also on error responses, see <see cref="CorrelationIds.Set"/>), quotes it in
/// problem bodies, and puts it on every log event of the request as <c>CorrelationId</c>.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[CorrelationIds.HeaderName].ToString();
        var correlationId = IsValid(inbound) ? inbound : Guid.NewGuid().ToString("N");
        CorrelationIds.Set(context, correlationId);

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    /// <summary>1–64 of <c>[A-Za-z0-9._-]</c>: anything else (too long, spaces, control characters) could forge or flood log lines.</summary>
    public static bool IsValid(string? value) => !string.IsNullOrEmpty(value) && SafeToken().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,64}\z")]
    private static partial Regex SafeToken();
}
