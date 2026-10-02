using SeatReservation.Application.Shows;

namespace SeatReservation.Application.Reservations;

/// <summary>A seat row returned by <c>SELECT … FOR UPDATE</c>. The service decides whether its status means "taken".</summary>
public sealed record LockedSeat(string Label, SeatStatus Status);
