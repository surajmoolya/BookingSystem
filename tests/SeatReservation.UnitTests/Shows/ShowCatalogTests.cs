using SeatReservation.Application.Shows;
using SeatReservation.UnitTests.Fakes;

namespace SeatReservation.UnitTests.Shows;

public class ShowCatalogTests
{
    private static readonly Guid ShowId = SequentialIdGenerator.IdFor(100);
    private readonly InMemoryUnitOfWork _db = new();
    private readonly FakeShowReadRepository _reads;
    private readonly ShowCatalog _catalog;

    public ShowCatalogTests()
    {
        _reads = new FakeShowReadRepository(_db);
        _catalog = new ShowCatalog(_reads);
    }

    [Fact]
    public async Task Miss_loads_from_repository_once()
    {
        _db.AddShow(ShowId, "A1", "A2");

        var first = await _catalog.GetAsync(ShowId, CancellationToken.None);
        var second = await _catalog.GetAsync(ShowId, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, _reads.GetShowCalls);
        Assert.Equal(1, _reads.SnapshotCalls);
    }

    [Fact]
    public async Task Loaded_entry_has_the_metadata_and_the_label_set()
    {
        var show = _db.AddShow(new ShowInfo(ShowId, "gala", 50_000, 2, 3), ["A1", "a1", "B7"]);

        var definition = await _catalog.GetAsync(ShowId, CancellationToken.None);

        Assert.Equal(show, definition!.Show);
        Assert.True(definition.HasSeat("a1"));
        Assert.True(definition.HasSeat("B7"));
        Assert.False(definition.HasSeat("b7"));   // labels are case-sensitive (D-013)
        Assert.Equal(3, definition.Labels.Count);
    }

    [Fact]
    public async Task Hit_does_not_call_repository()
    {
        var added = ShowDefinition.Create(new ShowInfo(ShowId, "gala", 1, 4, 1), ["A1"]);
        _catalog.Add(added);

        var definition = await _catalog.GetAsync(ShowId, CancellationToken.None);

        Assert.Same(added, definition);
        Assert.Equal(0, _reads.GetShowCalls);
        Assert.Equal(0, _reads.SnapshotCalls);
    }

    [Fact]
    public async Task Unknown_show_not_cached()
    {
        Assert.Null(await _catalog.GetAsync(ShowId, CancellationToken.None));
        Assert.Null(await _catalog.GetAsync(ShowId, CancellationToken.None));
        Assert.Equal(2, _reads.GetShowCalls);
        Assert.Equal(0, _reads.SnapshotCalls);

        // Created later (e.g. by another instance): now it is found.
        _db.AddShow(ShowId, "A1");
        Assert.NotNull(await _catalog.GetAsync(ShowId, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_misses_load_once_or_idempotently()
    {
        var reads = new SlowConcurrentReads(new ShowInfo(ShowId, "gala", 1, 4, 2), ["A1", "A2"]);
        var catalog = new ShowCatalog(reads);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => catalog.GetAsync(ShowId, CancellationToken.None))));

        // The loads overlapped (more than one hit the repository), yet every caller got the same single cached entry.
        Assert.True(reads.GetShowCalls > 1, $"loads did not overlap: {reads.GetShowCalls}");
        var cached = await catalog.GetAsync(ShowId, CancellationToken.None);
        Assert.All(results, r => Assert.Same(cached, r));
    }

    /// <summary>Thread-safe reads that wait a little, so concurrent misses really overlap.</summary>
    private sealed class SlowConcurrentReads(ShowInfo show, string[] labels) : SeatReservation.Application.Abstractions.IShowReadRepository
    {
        private int _getShowCalls;

        public int GetShowCalls => Volatile.Read(ref _getShowCalls);

        public async Task<ShowInfo?> GetShowAsync(Guid showId, CancellationToken ct)
        {
            Interlocked.Increment(ref _getShowCalls);
            await Task.Delay(50, ct);
            return show;
        }

        public Task<IReadOnlyList<SeatState>> GetSeatSnapshotAsync(Guid showId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SeatState>>(labels.Select(l => new SeatState(l, SeatStatus.Available)).ToList());
    }
}
