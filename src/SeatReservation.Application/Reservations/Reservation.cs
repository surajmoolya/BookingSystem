namespace SeatReservation.Application.Reservations;

/// <summary>
/// A reservation row. <see cref="Seats"/> are the ordinally sorted labels and <see cref="RequestHash"/> is the
/// SHA-256 of the canonical request (D-013). Compare hashes with <c>SequenceEqual</c>, not record equality.
/// </summary>
public sealed record Reservation(
    Guid Id,
    Guid ShowId,
    string UserId,
    string IdempotencyKey,
    byte[] RequestHash,
    IReadOnlyList<string> Seats,
    long AmountPaise,
    ReservationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CancelledAt = null)
{
    /// <summary>A freshly created reservation. v1 confirms immediately (D-011).</summary>
    public static Reservation Confirmed(
        Guid id,
        Guid showId,
        string userId,
        string idempotencyKey,
        byte[] requestHash,
        IReadOnlyList<string> sortedSeats,
        long amountPaise,
        DateTimeOffset createdAt) =>
        new(id, showId, userId, idempotencyKey, requestHash, sortedSeats, amountPaise, ReservationStatus.Confirmed, createdAt);

    public Reservation AsCancelled(DateTimeOffset at) => this with { Status = ReservationStatus.Cancelled, CancelledAt = at };
}
