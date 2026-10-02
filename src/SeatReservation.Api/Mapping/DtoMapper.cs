using SeatReservation.Api.Contracts.Requests;
using SeatReservation.Api.Contracts.Responses;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;

namespace SeatReservation.Api.Mapping;

/// <summary>HTTP DTOs ↔ logic-layer commands and models. Wire-format concerns only, no business rules.</summary>
public static class DtoMapper
{
    /// <summary>
    /// Resolves the <c>max_seats_per_user</c> alias (D-085): either field alone is used, both equal is fine, both different is a
    /// 400 on <c>per_user_limit</c>. Returns false with <paramref name="errors"/> set in that case.
    /// </summary>
    public static bool TryToCommand(CreateShowRequest body, out CreateShowCommand command, out IReadOnlyDictionary<string, string[]> errors)
    {
        if (body.PerUserLimit is { } limit && body.MaxSeatsPerUser is { } alias && limit != alias)
        {
            command = null!;
            errors = new Dictionary<string, string[]>
            {
                ["per_user_limit"] = ["per_user_limit and its alias max_seats_per_user were both given with different values."],
            };
            return false;
        }

        command = new CreateShowCommand(body.Name, body.Seats, body.PricePaise!.Value, body.PerUserLimit ?? body.MaxSeatsPerUser);
        errors = new Dictionary<string, string[]>();
        return true;
    }

    public static ShowResponse ToResponse(ShowSnapshot snapshot) => new(
        snapshot.Show.Id,
        snapshot.Show.Name,
        snapshot.Show.PricePaise,
        snapshot.Show.PerUserLimit,
        new SeatCountsResponse(snapshot.Counts.Total, snapshot.Counts.Available, snapshot.Counts.Held, snapshot.Counts.Confirmed),
        snapshot.Seats.Select(s => new SeatResponse(s.Label, s.Status)).ToArray(),
        snapshot.AsOf.UtcDateTime);   // UTC DateTime serialises with a trailing "Z"

    public static ReservationResponse ToResponse(Reservation r) => new(
        r.Id,
        r.ShowId,
        r.UserId,
        r.Seats,
        r.AmountPaise,
        r.Status,
        r.IdempotencyKey,
        r.CreatedAt.UtcDateTime,
        r.CancelledAt?.UtcDateTime);
}
