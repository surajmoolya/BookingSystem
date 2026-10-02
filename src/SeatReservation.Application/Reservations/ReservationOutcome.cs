namespace SeatReservation.Application.Reservations;

/// <summary>Every way a reserve call can end (lld §6.2). Only <see cref="Created"/> wrote anything.</summary>
public abstract record ReservationOutcome
{
    private ReservationOutcome()
    {
    }

    /// <summary>The metrics label for a non-created outcome; null for <see cref="Created"/>.</summary>
    public abstract DeclineReason? DeclineReason { get; }

    /// <summary>A new reservation was committed.</summary>
    public sealed record Created(Reservation Reservation) : ReservationOutcome
    {
        public override DeclineReason? DeclineReason => null;
    }

    /// <summary>The same user, key and request were already reserved; this is the original reservation (D-033).</summary>
    public sealed record Replayed(Reservation Reservation) : ReservationOutcome
    {
        public override DeclineReason? DeclineReason => Reservations.DeclineReason.IdempotentReplay;
    }

    /// <summary>One or more requested seats are not available; lists every one of them (all-or-nothing, D-010).</summary>
    public sealed record SeatTaken(IReadOnlyList<string> UnavailableSeats) : ReservationOutcome
    {
        public override DeclineReason? DeclineReason => Reservations.DeclineReason.SeatTaken;
    }

    /// <summary><paramref name="Held"/> + <paramref name="Requested"/> would exceed the show's <paramref name="Limit"/> (D-085).</summary>
    public sealed record PerUserLimit(int Limit, int Held, int Requested) : ReservationOutcome
    {
        public override DeclineReason? DeclineReason => Reservations.DeclineReason.PerUserLimit;
    }

    /// <summary>The key was already used by this user for a different request.</summary>
    public sealed record KeyConflict(Guid OriginalReservationId) : ReservationOutcome
    {
        public override DeclineReason? DeclineReason => Reservations.DeclineReason.IdempotencyKeyConflict;
    }

    /// <summary>Labels that are not seats of the show (case-sensitive, D-013).</summary>
    public sealed record UnknownSeat(IReadOnlyList<string> UnknownSeats) : ReservationOutcome
    {
        public override DeclineReason? DeclineReason => Reservations.DeclineReason.UnknownSeat;
    }

    /// <summary>The command is malformed; keys are command property names, as in <see cref="Validation.ValidationErrors"/>.</summary>
    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : ReservationOutcome
    {
        public override DeclineReason? DeclineReason => Reservations.DeclineReason.Validation;
    }

    public sealed record ShowNotFound : ReservationOutcome
    {
        public override DeclineReason? DeclineReason => Reservations.DeclineReason.ShowNotFound;
    }
}
