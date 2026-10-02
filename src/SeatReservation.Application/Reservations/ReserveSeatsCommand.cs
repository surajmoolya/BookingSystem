namespace SeatReservation.Application.Reservations;

/// <summary>
/// <c>POST /shows/{id}/reserve</c> as the logic layer sees it. <see cref="UserId"/> is the token's <c>sub</c>, never the
/// body. <see cref="IdempotencyKey"/> has already been resolved from the header or the body by the controller layer
/// (D-084); its length and characters are still checked by <see cref="ReserveSeatsValidator"/>.
/// </summary>
public sealed record ReserveSeatsCommand(Guid ShowId, string UserId, IReadOnlyList<string?> Seats, string IdempotencyKey);
