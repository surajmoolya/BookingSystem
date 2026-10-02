using SeatReservation.Application.Shows;

namespace SeatReservation.Api.Contracts.Responses;

/// <summary><c>GET /shows/{id}</c> and <c>POST /shows</c> 201 body (lld §5.3). Only <c>per_user_limit</c> is returned, never the alias.</summary>
public sealed record ShowResponse(
    Guid Id,
    string Name,
    long PricePaise,
    int PerUserLimit,
    SeatCountsResponse Counts,
    IReadOnlyList<SeatResponse> Seats,
    DateTime AsOf);

public sealed record SeatCountsResponse(int Total, int Available, int Held, int Confirmed);

public sealed record SeatResponse(string Label, SeatStatus Status);
