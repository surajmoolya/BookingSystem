using SeatReservation.Application.Shows;

namespace SeatReservation.Application.Reservations;

/// <summary>A seat's current owner as seen by the lock-free fast path. <see cref="UserId"/> is null while available.</summary>
public sealed record SeatOwner(string Label, SeatStatus Status, string? UserId);
