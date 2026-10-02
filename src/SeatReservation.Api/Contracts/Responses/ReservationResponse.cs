using System.Text.Json.Serialization;
using SeatReservation.Application.Reservations;

namespace SeatReservation.Api.Contracts.Responses;

/// <summary>A reservation as the API returns it (lld §5.4). <c>cancelled_at</c> appears only once it's cancelled.</summary>
public sealed record ReservationResponse(
    Guid ReservationId,
    Guid ShowId,
    string UserId,
    IReadOnlyList<string> Seats,
    long AmountPaise,
    ReservationStatus Status,
    string IdempotencyKey,
    DateTime CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? CancelledAt);
