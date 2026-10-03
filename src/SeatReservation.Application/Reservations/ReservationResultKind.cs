namespace SeatReservation.Application.Reservations;

/// <summary>The <c>outcome</c> label of <c>reservation_duration_seconds</c> (lld §9): four values, whatever the decline reason.</summary>
public enum ReservationResultKind
{
    Created,
    Replayed,
    Declined,
    Error,
}
