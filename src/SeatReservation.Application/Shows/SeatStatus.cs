namespace SeatReservation.Application.Shows;

/// <summary>Seat lifecycle. <see cref="Held"/> is modelled but never written in v1 (D-012).</summary>
public enum SeatStatus
{
    Available,
    Held,
    Confirmed,
}
