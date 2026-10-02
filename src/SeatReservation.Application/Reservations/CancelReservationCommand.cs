namespace SeatReservation.Application.Reservations;

/// <summary><c>POST /reservations/{id}/cancel</c> as the logic layer sees it. <see cref="UserId"/> is the token's <c>sub</c>.</summary>
public sealed record CancelReservationCommand(Guid ReservationId, string UserId);
