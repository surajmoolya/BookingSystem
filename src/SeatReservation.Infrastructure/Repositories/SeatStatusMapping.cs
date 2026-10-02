using SeatReservation.Application.Shows;

namespace SeatReservation.Infrastructure.Repositories;

/// <summary>The <c>seats.status</c> text values (CHECK in V001) and the logic layer's enum.</summary>
public static class SeatStatusMapping
{
    public static SeatStatus Parse(string value) => value switch
    {
        "available" => SeatStatus.Available,
        "held" => SeatStatus.Held,
        "confirmed" => SeatStatus.Confirmed,
        _ => throw new InvalidOperationException($"Unknown seat status '{value}' in the database."),
    };
}
