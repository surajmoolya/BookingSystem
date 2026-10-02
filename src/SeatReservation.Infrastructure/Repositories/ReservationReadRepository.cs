using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Reservations;

namespace SeatReservation.Infrastructure.Repositories;

/// <summary>
/// Autocommit reservation reads. Registered now so the container can build <see cref="ReservationService"/>; the SQL
/// (and the one-round-trip fast-path batch, D-088) is implemented in T-3.6. Nothing calls it before T-3.7.
/// </summary>
public sealed class ReservationReadRepository : IReservationReadRepository
{
    public Task<Reservation?> FindByKeyAsync(string userId, string idempotencyKey, CancellationToken ct) => throw NotYet();

    public Task<Reservation?> GetByIdAsync(Guid reservationId, CancellationToken ct) => throw NotYet();

    public Task<FastPathSnapshot> GetFastPathSnapshotAsync(
        string userId,
        string idempotencyKey,
        Guid showId,
        IReadOnlyList<string> labels,
        CancellationToken ct) => throw NotYet();

    private static NotSupportedException NotYet() => new("The reservation read repository is implemented in T-3.6.");
}
