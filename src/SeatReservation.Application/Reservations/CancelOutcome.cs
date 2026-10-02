namespace SeatReservation.Application.Reservations;

/// <summary>Every way a cancel call can end (lld §6.4). Only <see cref="Cancelled"/> wrote anything.</summary>
public abstract record CancelOutcome
{
    private CancelOutcome()
    {
    }

    /// <summary>The owner's confirmed reservation is now cancelled and its seats are free.</summary>
    public sealed record Cancelled(Reservation Reservation) : CancelOutcome;

    /// <summary>Cancelled earlier; returned as it is, so a repeated cancel is idempotent (200, no metric).</summary>
    public sealed record AlreadyCancelled(Reservation Reservation) : CancelOutcome;

    /// <summary>The reservation belongs to another user (403, D-022).</summary>
    public sealed record NotOwner : CancelOutcome;

    public sealed record NotFound : CancelOutcome;
}
