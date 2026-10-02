namespace SeatReservation.Application.Shows;

/// <summary>
/// <c>POST /shows</c> as the logic layer sees it. <see cref="PerUserLimit"/> is null when the request omitted it; the
/// <c>max_seats_per_user</c> alias has already been resolved by the controller layer (D-085).
/// </summary>
public sealed record CreateShowCommand(string? Name, IReadOnlyList<string?>? Seats, long PricePaise, int? PerUserLimit);
