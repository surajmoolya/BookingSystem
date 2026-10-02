using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;

namespace SeatReservation.UnitTests.Fakes;

/// <summary>Autocommit show reads over the same in-memory state as the unit of work, with call counters.</summary>
public sealed class FakeShowReadRepository(InMemoryUnitOfWork db) : IShowReadRepository
{
    public int GetShowCalls { get; private set; }

    public int SnapshotCalls { get; private set; }

    /// <summary>When set, returned instead of the real snapshot (e.g. to feed an inconsistent one).</summary>
    public IReadOnlyList<SeatState>? SnapshotOverride { get; set; }

    public Task<ShowInfo?> GetShowAsync(Guid showId, CancellationToken ct)
    {
        GetShowCalls++;
        db.Calls.Add("Read.GetShow");
        return Task.FromResult(db.ShowRows.GetValueOrDefault(showId));
    }

    public Task<IReadOnlyList<SeatState>> GetSeatSnapshotAsync(Guid showId, CancellationToken ct)
    {
        SnapshotCalls++;
        db.Calls.Add("Read.GetSeatSnapshot");
        IReadOnlyList<SeatState> snapshot = SnapshotOverride ?? db.SeatRows
            .Where(s => s.Key.ShowId == showId)
            .OrderBy(s => s.Value.Ordinal)
            .Select(s => new SeatState(s.Key.Label, s.Value.Status))
            .ToList();
        return Task.FromResult(snapshot);
    }
}

/// <summary>Autocommit reservation reads over the same in-memory state as the unit of work, with call counters.</summary>
public sealed class FakeReservationReadRepository(InMemoryUnitOfWork db) : IReservationReadRepository
{
    public int FindByKeyCalls { get; private set; }

    public int GetByIdCalls { get; private set; }

    public int FastPathCalls { get; private set; }

    public Task<Reservation?> FindByKeyAsync(string userId, string idempotencyKey, CancellationToken ct)
    {
        FindByKeyCalls++;
        db.Calls.Add("Read.FindByKey");
        return Task.FromResult(db.FindByKey(userId, idempotencyKey));
    }

    public Task<Reservation?> GetByIdAsync(Guid reservationId, CancellationToken ct)
    {
        GetByIdCalls++;
        db.Calls.Add("Read.GetById");
        return Task.FromResult(db.ReservationRows.GetValueOrDefault(reservationId));
    }

    public Task<FastPathSnapshot> GetFastPathSnapshotAsync(
        string userId,
        string idempotencyKey,
        Guid showId,
        IReadOnlyList<string> labels,
        CancellationToken ct)
    {
        FastPathCalls++;
        db.Calls.Add("Read.FastPath");
        IReadOnlyList<SeatOwner> owners = labels
            .Where(l => db.SeatRows.ContainsKey((showId, l)))
            .Select(l =>
            {
                var row = db.SeatRows[(showId, l)];
                return new SeatOwner(l, row.Status, row.UserId);
            })
            .ToList();
        return Task.FromResult(new FastPathSnapshot(db.FindByKey(userId, idempotencyKey), owners));
    }
}

public sealed class FakeSeatStatisticsQuery : ISeatStatisticsQuery
{
    public SeatStatistics Result { get; set; } = new(new SeatCounts(0, 0, 0, 0), new Dictionary<Guid, SeatCounts>());

    public int Calls { get; private set; }

    public int? LastMaxShows { get; private set; }

    public Task<SeatStatistics> GetCountsAsync(int maxShows, CancellationToken ct)
    {
        Calls++;
        LastMaxShows = maxShows;
        return Task.FromResult(Result);
    }
}

public sealed class FakeReadinessState(bool isReady = true) : IReadinessState
{
    public bool IsReady { get; set; } = isReady;
}
