namespace SeatReservation.Application.Shows;

/// <summary>Global seat totals plus per-show counts for the most recently created shows (D-086).</summary>
public sealed record SeatStatistics(SeatCounts Global, IReadOnlyDictionary<Guid, SeatCounts> PerShow);
