namespace SeatReservation.Api.Contracts.Requests;

/// <summary>
/// <c>POST /shows/{id}/reserve</c> body (lld §5.4). There is deliberately no <c>user_id</c>: the user is the token's
/// <c>sub</c>, and a spoofed one in the body is ignored like any unknown member.
/// </summary>
public sealed class ReserveRequest
{
    public List<string?>? Seats { get; init; }

    /// <summary>Optional here: the key may come from the <c>Idempotency-Key</c> header instead (D-084).</summary>
    public string? IdempotencyKey { get; init; }
}
