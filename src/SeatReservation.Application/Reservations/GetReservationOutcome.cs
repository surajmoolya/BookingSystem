namespace SeatReservation.Application.Reservations;

/// <summary>How an owner-only reservation lookup can end (lld §5.6).</summary>
public abstract record GetReservationOutcome
{
    private GetReservationOutcome()
    {
    }

    public sealed record Found(Reservation Reservation) : GetReservationOutcome;

    /// <summary>The reservation belongs to another user (403, D-022).</summary>
    public sealed record NotOwner : GetReservationOutcome;

    public sealed record NotFound : GetReservationOutcome;
}
