using Microsoft.Extensions.Logging;
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
    private readonly RecordingLogger<ShowService> _logger = new();

    public ShowServiceTests()
    {
        _tx = new FakeTransactionRunner(_db);
        _reads = new FakeShowReadRepository(_db);
        _catalog = new ShowCatalog(_reads);
    }

    private ShowService Service(int defaultPerUserLimit = 4) => new(
        new CreateShowValidator(MsOptions.Create(new ShowOptions())),
        _catalog,
        _reads,
        _tx,
        _ids,
        _clock,
        _readiness,
        MsOptions.Create(new ReservationOptions { DefaultPerUserLimit = defaultPerUserLimit }),
        _logger);

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

    // ---- GetStateAsync ----

    private static readonly Guid ShowId = SequentialIdGenerator.IdFor(500);

    private ShowInfo SeedShow(params string[] labels) => _db.AddShow(new ShowInfo(ShowId, "gala", 30_000, 3, labels.Length), labels);

    [Fact]
    public async Task GetState_aggregates_counts_and_reconciles()
    {
        var show = SeedShow("A1", "A2", "A3", "A4", "A5");
        _db.SetSeat(ShowId, "A2", SeatStatus.Confirmed, "alice", SequentialIdGenerator.IdFor(1));
        _db.SetSeat(ShowId, "A4", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(2));
        _db.SetSeat(ShowId, "A5", SeatStatus.Held, "bob", SequentialIdGenerator.IdFor(2));

        var found = Assert.IsType<GetShowOutcome.Found>(await Service().GetStateAsync(ShowId, CancellationToken.None));

        var snapshot = found.Snapshot;
        Assert.Equal(show, snapshot.Show);
        Assert.Equal(new SeatCounts(Total: 5, Available: 2, Held: 1, Confirmed: 2), snapshot.Counts);
        Assert.Equal(snapshot.Counts.Total, snapshot.Counts.Available + snapshot.Counts.Held + snapshot.Counts.Confirmed);
        Assert.Equal(["A1", "A2", "A3", "A4", "A5"], snapshot.Seats.Select(s => s.Label));
        Assert.Equal(SeatStatus.Confirmed, snapshot.Seats[1].Status);
        Assert.Equal(_clock.UtcNow, snapshot.AsOf);
        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task GetState_counts_come_from_a_single_snapshot_call()
    {
        SeedShow("A1", "A2");
        var service = Service();
        await service.GetStateAsync(ShowId, CancellationToken.None);   // warms the catalog (its miss reads the snapshot once for labels)
        var before = _reads.SnapshotCalls;

        await service.GetStateAsync(ShowId, CancellationToken.None);

        Assert.Equal(1, _reads.SnapshotCalls - before);
        Assert.Equal(1, _reads.GetShowCalls);   // metadata stayed cached
        Assert.Equal(0, _tx.Invocations);       // reads never open a transaction
    }

    [Fact]
    public async Task GetState_reflects_changes_after_the_show_was_cached()
    {
        SeedShow("A1", "A2");
        var service = Service();
        await service.GetStateAsync(ShowId, CancellationToken.None);
        _db.SetSeat(ShowId, "A1", SeatStatus.Confirmed, "alice", SequentialIdGenerator.IdFor(1));

        var found = Assert.IsType<GetShowOutcome.Found>(await service.GetStateAsync(ShowId, CancellationToken.None));

        Assert.Equal(new SeatCounts(2, 1, 0, 1), found.Snapshot.Counts);   // seat state is never cached
    }

    [Fact]
    public async Task GetState_logs_invariant_violation_if_counts_mismatch()
    {
        SeedShow("A1", "A2", "A3");
        _reads.SnapshotOverride = [new SeatState("A1", SeatStatus.Available), new SeatState("A2", (SeatStatus)99)];

        var found = Assert.IsType<GetShowOutcome.Found>(await Service().GetStateAsync(ShowId, CancellationToken.None));

        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.StartsWith("invariant.violation", error.Message);
        Assert.Contains($"show_id={ShowId}", error.Message);
        Assert.Equal(new SeatCounts(Total: 2, Available: 1, Held: 0, Confirmed: 0), found.Snapshot.Counts);   // still answered
    }

    [Fact]
    public async Task GetState_logs_invariant_violation_if_seat_rows_disagree_with_total_seats()
    {
        SeedShow("A1", "A2", "A3");
        _reads.SnapshotOverride = [new SeatState("A1", SeatStatus.Available), new SeatState("A2", SeatStatus.Confirmed)];

        Assert.IsType<GetShowOutcome.Found>(await Service().GetStateAsync(ShowId, CancellationToken.None));

        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("expected_total=3", error.Message);
    }

    [Fact]
    public async Task GetState_unknown_show_returns_NotFound()
    {
        var outcome = await Service().GetStateAsync(ShowId, CancellationToken.None);

        Assert.IsType<GetShowOutcome.ShowNotFound>(outcome);
        Assert.Equal(0, _reads.SnapshotCalls);
    }

    [Fact]
    public async Task GetState_after_create_needs_no_metadata_read()
    {
        var service = Service();
        var created = Assert.IsType<CreateShowOutcome.Created>(await service.CreateAsync(Command(), CancellationToken.None));

        var found = Assert.IsType<GetShowOutcome.Found>(await service.GetStateAsync(created.Snapshot.Show.Id, CancellationToken.None));

        Assert.Equal(created.Snapshot.Counts, found.Snapshot.Counts);
        Assert.Equal(created.Snapshot.Seats, found.Snapshot.Seats);
        Assert.Equal(0, _reads.GetShowCalls);
    }

    [Fact]
    public async Task GetState_not_ready_throws_NotReadyException()
    {
        SeedShow("A1");
        _readiness.IsReady = false;

        await Assert.ThrowsAsync<NotReadyException>(() => Service().GetStateAsync(ShowId, CancellationToken.None));

        Assert.Equal(0, _reads.GetShowCalls + _reads.SnapshotCalls);
    }
}
