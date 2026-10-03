using Microsoft.Extensions.Logging;
using SeatReservation.Application.Reservations;

namespace SeatReservation.Application.Logging;

/// <summary>
/// The logic layer's domain log events (lld §10), source-generated so a disabled level costs nothing. Each has a stable
/// <see cref="EventId"/> whose name is the event name; the composition root copies that name into an <c>EventName</c>
/// property. Messages start with the event name so a plain-text reader can grep them too.
/// </summary>
public static partial class LogEvents
{
    public const string ReservationAttemptName = "reservation.attempt";
    public const string ReservationConfirmedName = "reservation.confirmed";
    public const string ReservationDeclinedName = "reservation.declined";
    public const string ReservationReplayedName = "reservation.replayed";
    public const string ReservationCancelledName = "reservation.cancelled";
    public const string ShowCreatedName = "show.created";
    public const string InvariantViolationName = "invariant.violation";

    /// <summary>The raw idempotency key is never logged (D-052): only the first 8 hex characters of its SHA-256.</summary>
    [LoggerMessage(EventId = 1001, EventName = ReservationAttemptName, Level = LogLevel.Debug,
        Message = "reservation.attempt user_id={UserId} show_id={ShowId} seat_count={SeatCount} idempotency_key_hash={IdempotencyKeyHash}")]
    public static partial void ReservationAttempt(this ILogger logger, string userId, Guid showId, int seatCount, string idempotencyKeyHash);

    [LoggerMessage(EventId = 1002, EventName = ReservationConfirmedName, Level = LogLevel.Information,
        Message = "reservation.confirmed reservation_id={ReservationId} user_id={UserId} show_id={ShowId} seats={Seats} amount_paise={AmountPaise} duration_ms={DurationMs}")]
    public static partial void ReservationConfirmed(
        this ILogger logger, Guid reservationId, string userId, Guid showId, IReadOnlyList<string> seats, long amountPaise, double durationMs);

    /// <summary>Debug, not Information: during a burst the request log line already carries the outcome (D-088).</summary>
    [LoggerMessage(EventId = 1003, EventName = ReservationDeclinedName, Level = LogLevel.Debug,
        Message = "reservation.declined reason={Reason} user_id={UserId} show_id={ShowId} unavailable_seats={UnavailableSeats}")]
    public static partial void ReservationDeclined(this ILogger logger, DeclineReason reason, string userId, Guid showId, IReadOnlyList<string>? unavailableSeats);

    /// <summary>Debug for the same reason as <see cref="ReservationDeclined"/>.</summary>
    [LoggerMessage(EventId = 1004, EventName = ReservationReplayedName, Level = LogLevel.Debug,
        Message = "reservation.replayed reservation_id={ReservationId} user_id={UserId}")]
    public static partial void ReservationReplayed(this ILogger logger, Guid reservationId, string userId);

    [LoggerMessage(EventId = 1005, EventName = ReservationCancelledName, Level = LogLevel.Information,
        Message = "reservation.cancelled reservation_id={ReservationId} user_id={UserId} show_id={ShowId} seats={Seats}")]
    public static partial void ReservationCancelled(this ILogger logger, Guid reservationId, string userId, Guid showId, IReadOnlyList<string> seats);

    [LoggerMessage(EventId = 1006, EventName = ShowCreatedName, Level = LogLevel.Information,
        Message = "show.created show_id={ShowId} seats={Seats} per_user_limit={PerUserLimit}")]
    public static partial void ShowCreated(this ILogger logger, Guid showId, int seats, int perUserLimit);

    [LoggerMessage(EventId = 1007, EventName = InvariantViolationName, Level = LogLevel.Error,
        Message = "invariant.violation show_id={ShowId} total={Total} available={Available} held={Held} confirmed={Confirmed} expected_total={ExpectedTotal}")]
    public static partial void InvariantViolation(
        this ILogger logger, Guid showId, int total, int available, int held, int confirmed, int expectedTotal);
}
