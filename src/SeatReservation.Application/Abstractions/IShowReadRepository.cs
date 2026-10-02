using SeatReservation.Application.Shows;

namespace SeatReservation.Application.Abstractions;

/// <summary>Autocommit reads, outside any transaction.</summary>
public interface IShowReadRepository
{
    Task<ShowInfo?> GetShowAsync(Guid showId, CancellationToken ct);

    /// <summary>All seats of the show in ordinal order, from a single statement so it is one snapshot (D-039).</summary>
    Task<IReadOnlyList<SeatState>> GetSeatSnapshotAsync(Guid showId, CancellationToken ct);
}
