namespace SeatReservation.Application.Reservations;

/// <summary>Result of the single-round-trip fast-path read (D-088): the key's reservation, if any, and the requested seats' owners.</summary>
public sealed record FastPathSnapshot(Reservation? ExistingForKey, IReadOnlyList<SeatOwner> Owners);
