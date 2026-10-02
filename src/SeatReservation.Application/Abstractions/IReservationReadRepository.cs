using SeatReservation.Application.Reservations;

namespace SeatReservation.Application.Abstractions;

/// <summary>Autocommit reads, outside any transaction.</summary>
public interface IReservationReadRepository
{
    Task<Reservation?> FindByKeyAsync(string userId, string idempotencyKey, CancellationToken ct);

    Task<Reservation?> GetByIdAsync(Guid reservationId, CancellationToken ct);

    /// <summary>The key lookup and the requested seats' owners in ONE round trip (D-088).</summary>
    Task<FastPathSnapshot> GetFastPathSnapshotAsync(
        string userId,
        string idempotencyKey,
        Guid showId,
        IReadOnlyList<string> labels,
        CancellationToken ct);
}
