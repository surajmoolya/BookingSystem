using System.Collections.Concurrent;
using SeatReservation.Application.Abstractions;

namespace SeatReservation.Application.Shows;

/// <summary>
/// Process-local cache of <see cref="ShowDefinition"/>s. Shows never change after creation, so an entry never goes stale and
/// is never evicted (shows are admin-created, so the set stays small). Only shows that exist are cached: an unknown id is
/// re-checked on every call, because it may be created later.
/// </summary>
public sealed class ShowCatalog(IShowReadRepository reads)
{
    private readonly ConcurrentDictionary<Guid, ShowDefinition> _shows = new();

    public async Task<ShowDefinition?> GetAsync(Guid showId, CancellationToken ct)
    {
        if (_shows.TryGetValue(showId, out var cached))
        {
            return cached;
        }

        var show = await reads.GetShowAsync(showId, ct);
        if (show is null)
        {
            return null;
        }

        var seats = await reads.GetSeatSnapshotAsync(showId, ct);

        // Concurrent misses may each load; the data is immutable, so whichever entry lands first is as good as any.
        return _shows.GetOrAdd(showId, ShowDefinition.Create(show, seats.Select(s => s.Label)));
    }

    /// <summary>Called by <see cref="ShowService"/> after the creating transaction committed.</summary>
    public void Add(ShowDefinition definition) => _shows[definition.Show.Id] = definition;
}
