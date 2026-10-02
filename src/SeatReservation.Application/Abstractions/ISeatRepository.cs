using SeatReservation.Application.Reservations;

namespace SeatReservation.Application.Abstractions;

public interface ISeatRepository
{
    Task<int> CountConfirmedByUserAsync(Guid showId, string userId, CancellationToken ct);

    /// <summary>Locks the rows with <c>FOR UPDATE</c> in label order. Callers must pass ordinally sorted labels (deadlock freedom).</summary>
    Task<IReadOnlyList<LockedSeat>> LockForUpdateAsync(Guid showId, IReadOnlyList<string> sortedLabels, CancellationToken ct);

    /// <summary>Marks the labels confirmed for the reservation, only where still available. Returns the rows changed.</summary>
    Task<int> ConfirmAsync(Guid showId, IReadOnlyList<string> labels, string userId, Guid reservationId, CancellationToken ct);

    /// <summary>Frees the seats of exactly one reservation; never touches seats owned by another reservation (D-040).</summary>
    Task<int> ReleaseByReservationAsync(Guid reservationId, CancellationToken ct);
}
