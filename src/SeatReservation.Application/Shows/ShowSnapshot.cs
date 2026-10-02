namespace SeatReservation.Application.Shows;

/// <summary>A show's state at <see cref="AsOf"/>: metadata, seats in ordinal order, and counts that reconcile with them.</summary>
public sealed record ShowSnapshot(ShowInfo Show, SeatCounts Counts, IReadOnlyList<SeatState> Seats, DateTimeOffset AsOf);
