namespace SeatReservation.Application.Shows;

/// <summary>One row of a show's seat snapshot (<c>GET /shows/{id}</c>).</summary>
public sealed record SeatState(string Label, SeatStatus Status);
