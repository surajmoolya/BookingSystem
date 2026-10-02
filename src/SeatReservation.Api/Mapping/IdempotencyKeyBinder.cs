namespace SeatReservation.Api.Mapping;

/// <summary>
/// Where the idempotency key comes from (D-084): the <c>Idempotency-Key</c> header or the <c>idempotency_key</c> body
/// field. Either alone is used; both equal is fine; both different, or neither, is a 400. This is HTTP binding only: the
/// key's length and characters are checked by the logic layer's <c>ReserveSeatsValidator</c>.
/// </summary>
public static class IdempotencyKeyBinder
{
    public const string HeaderName = "Idempotency-Key";
    public const string Field = "idempotency_key";

    /// <returns>False with <paramref name="errors"/> (keyed by wire name) when no single key can be resolved.</returns>
    public static bool TryResolve(string? header, string? body, out string key, out IReadOnlyDictionary<string, string[]> errors)
    {
        // An empty value is treated as absent, so "Idempotency-Key:" plus a body key still works.
        var fromHeader = string.IsNullOrEmpty(header) ? null : header;
        var fromBody = string.IsNullOrEmpty(body) ? null : body;
        errors = new Dictionary<string, string[]>();

        switch (fromHeader, fromBody)
        {
            case (null, null):
                key = "";
                errors = Error($"An idempotency key is required, in the {HeaderName} header or the {Field} body field.");
                return false;

            case ({ } h, { } b) when !string.Equals(h, b, StringComparison.Ordinal):
                key = "";
                errors = Error($"The {HeaderName} header and the {Field} body field were both given with different values.");
                return false;

            default:
                key = fromHeader ?? fromBody!;
                return true;
        }
    }

    private static Dictionary<string, string[]> Error(string message) => new() { [Field] = [message] };
}
