using Microsoft.Extensions.Logging.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Options;
using SeatReservation.Application.Shows;
using SeatReservation.UnitTests.Fakes;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace SeatReservation.UnitTests.Shows;

public class ShowServiceTests
{
    private readonly InMemoryUnitOfWork _db = new();
    private readonly FakeTransactionRunner _tx;
    private readonly FakeShowReadRepository _reads;
    private readonly ShowCatalog _catalog;
    private readonly SequentialIdGenerator _ids = new();
    private readonly FakeClock _clock = new();
    private readonly FakeReadinessState _readiness = new();

    public ShowServiceTests()
    {
        _tx = new FakeTransactionRunner(_db);
        _reads = new FakeShowReadRepository(_db);
        _catalog = new ShowCatalog(_reads);
    }

    private ShowService Service(int defaultPerUserLimit = 4) => new(
        new CreateShowValidator(MsOptions.Create(new ShowOptions())),
        _catalog,
        _tx,
        _ids,
        _clock,
        _readiness,
        MsOptions.Create(new ReservationOptions { DefaultPerUserLimit = defaultPerUserLimit }),
        NullLogger<ShowService>.Instance);

    private static CreateShowCommand Command(int? limit = null, params string[] seats) =>
        new("friday-night", seats.Length == 0 ? ["A1", "A2", "A3"] : seats, 25_000, limit);

    [Fact]
    public async Task Create_validates_then_inserts_and_caches()
    {
        var outcome = await Service().CreateAsync(Command(), CancellationToken.None);

        var created = Assert.IsType<CreateShowOutcome.Created>(outcome);
        Assert.Equal(["Tx.Begin:create_show", "Shows.InsertShowWithSeats", "Tx.Commit"], _db.Calls);
        Assert.Equal(["create_show"], _tx.Operations);

        // Cached from the command: the next lookup needs no repository call.
        var cached = await _catalog.GetAsync(created.Snapshot.Show.Id, CancellationToken.None);
        Assert.Equal(created.Snapshot.Show, cached!.Show);
        Assert.Equal(["A1", "A2", "A3"], cached.Labels.Order());
        Assert.Equal(0, _reads.GetShowCalls);
    }

    [Fact]
    public async Task Create_assigns_id_from_generator()
    {
        var created = Assert.IsType<CreateShowOutcome.Created>(await Service().CreateAsync(Command(), CancellationToken.None));

        Assert.Equal(SequentialIdGenerator.IdFor(1), created.Snapshot.Show.Id);
        Assert.True(_db.ShowRows.ContainsKey(SequentialIdGenerator.IdFor(1)));
    }

    [Fact]
    public async Task Stored_show_and_seats_match_the_command_in_order()
    {
        await Service().CreateAsync(Command(null, "B2", "A1", "c3"), CancellationToken.None);

        var stored = _db.ShowRows[SequentialIdGenerator.IdFor(1)];
        Assert.Equal(new ShowInfo(SequentialIdGenerator.IdFor(1), "friday-night", 25_000, 4, 3), stored);
        Assert.Equal(1, _db.Seat(stored.Id, "B2").Ordinal);
        Assert.Equal(2, _db.Seat(stored.Id, "A1").Ordinal);
        Assert.Equal(3, _db.Seat(stored.Id, "c3").Ordinal);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public async Task Default_limit_applied_when_omitted(int configuredDefault)
    {
        var created = Assert.IsType<CreateShowOutcome.Created>(
            await Service(defaultPerUserLimit: configuredDefault).CreateAsync(Command(limit: null), CancellationToken.None));

        Assert.Equal(configuredDefault, created.Snapshot.Show.PerUserLimit);
        Assert.Equal(configuredDefault, _db.ShowRows[created.Snapshot.Show.Id].PerUserLimit);
    }

    [Fact]
    public async Task Custom_per_user_limit_is_stored()
    {
        var created = Assert.IsType<CreateShowOutcome.Created>(await Service().CreateAsync(Command(limit: 2), CancellationToken.None));

        Assert.Equal(2, created.Snapshot.Show.PerUserLimit);
        Assert.Equal(2, _db.ShowRows[created.Snapshot.Show.Id].PerUserLimit);
    }

    [Fact]
    public async Task Created_snapshot_has_every_seat_available_in_request_order_as_of_now()
    {
        var created = Assert.IsType<CreateShowOutcome.Created>(await Service().CreateAsync(Command(null, "B2", "A1"), CancellationToken.None));

        var snapshot = created.Snapshot;
        Assert.Equal(new SeatCounts(Total: 2, Available: 2, Held: 0, Confirmed: 0), snapshot.Counts);
        Assert.Equal([new SeatState("B2", SeatStatus.Available), new SeatState("A1", SeatStatus.Available)], snapshot.Seats);
        Assert.Equal(_clock.UtcNow, snapshot.AsOf);
        Assert.Equal(2, snapshot.Show.TotalSeats);
    }

    [Fact]
    public async Task Invalid_command_returns_errors_and_never_opens_a_transaction()
    {
        var outcome = await Service().CreateAsync(new CreateShowCommand("", ["A1", "A1"], -1, 0), CancellationToken.None);

        var invalid = Assert.IsType<CreateShowOutcome.Invalid>(outcome);
        Assert.Equal(["Name", "PerUserLimit", "PricePaise", "Seats"], invalid.Errors.Keys.Order());
        Assert.Equal(0, _tx.Invocations);
        Assert.Empty(_db.ShowRows);
        Assert.Equal(SequentialIdGenerator.IdFor(1), _ids.NewId());   // no id was consumed
    }

    [Fact]
    public async Task Not_ready_throws_NotReadyException_without_a_transaction()
    {
        _readiness.IsReady = false;

        await Assert.ThrowsAsync<NotReadyException>(() => Service().CreateAsync(Command(), CancellationToken.None));

        Assert.Equal(0, _tx.Invocations);
    }

    [Fact]
    public async Task DependencyUnavailable_propagates_and_nothing_is_cached()
    {
        _tx.FailWith = new DependencyUnavailableException();

        await Assert.ThrowsAsync<DependencyUnavailableException>(() => Service().CreateAsync(Command(), CancellationToken.None));

        // Not in the catalog: the lookup falls through to the (empty) database.
        Assert.Null(await _catalog.GetAsync(SequentialIdGenerator.IdFor(1), CancellationToken.None));
        Assert.Equal(1, _reads.GetShowCalls);
    }
}
