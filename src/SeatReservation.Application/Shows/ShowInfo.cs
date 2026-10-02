namespace SeatReservation.Application.Shows;

/// <summary>Immutable show metadata. Seat state is never cached with it; it always comes from the database.</summary>
public sealed record ShowInfo(Guid Id, string Name, long PricePaise, int PerUserLimit, int TotalSeats);
