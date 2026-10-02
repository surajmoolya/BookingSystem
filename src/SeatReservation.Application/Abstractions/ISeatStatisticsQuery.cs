using SeatReservation.Application.Shows;

namespace SeatReservation.Application.Abstractions;

public interface ISeatStatisticsQuery
{
    /// <summary>Global totals plus per-show counts for the <paramref name="maxShows"/> most recently created shows, in one round trip.</summary>
    Task<SeatStatistics> GetCountsAsync(int maxShows, CancellationToken ct);
}
