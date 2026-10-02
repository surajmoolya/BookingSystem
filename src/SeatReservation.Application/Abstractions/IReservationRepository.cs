using SeatReservation.Application.Reservations;

namespace SeatReservation.Application.Abstractions;

public interface IReservationRepository
{
    Task<Reservation?> FindByKeyAsync(string userId, string idempotencyKey, CancellationToken ct);

    /// <summary>Reads the reservation with a row lock.</summary>
    Task<Reservation?> GetByIdForUpdateAsync(Guid reservationId, CancellationToken ct);

    /// <exception cref="Exceptions.DuplicateIdempotencyKeyException">The (user, key) pair already exists.</exception>
    Task InsertAsync(Reservation reservation, CancellationToken ct);

    Task MarkCancelledAsync(Guid reservationId, DateTimeOffset at, CancellationToken ct);
}
