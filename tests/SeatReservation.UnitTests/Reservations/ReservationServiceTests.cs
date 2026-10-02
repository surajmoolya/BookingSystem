using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Options;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;
using SeatReservation.UnitTests.Fakes;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace SeatReservation.UnitTests.Reservations;

public class ReservationServiceTests
{
    private static readonly Guid ShowId = SequentialIdGenerator.IdFor(100);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(1234);

    private readonly InMemoryUnitOfWork _db = new();
    private readonly FakeTransactionRunner _tx;
    private readonly FakeShowReadRepository _showReads;
    private readonly ShowCatalog _catalog;
    private readonly SequentialIdGenerator _ids = new();
    private readonly FakeClock _clock = new();
    private readonly FakeReadinessState _readiness = new();

    public ReservationServiceTests()
    {
        _tx = new FakeTransactionRunner(_db);
        _showReads = new FakeShowReadRepository(_db);
        _catalog = new ShowCatalog(_showReads);
        _db.AddShow(new ShowInfo(ShowId, "show", 25_000, 4, 5), ["A1", "A2", "A3", "A10", "B1"]);
    }

    private ReservationService Service() => new(
        _catalog,
        new ReserveSeatsValidator(MsOptions.Create(new ReservationOptions())),
        _tx,
        _ids,
        _clock,
        _readiness,
        MsOptions.Create(new ReservationOptions { LockTimeout = LockTimeout }));

    private static ReserveSeatsCommand Command(params string[] seats) =>
        new(ShowId, "alice", seats.Length == 0 ? ["A1"] : seats, "key-1");

    private Task<ReservationOutcome> ReserveAsync(ReserveSeatsCommand command) => Service().ReserveAsync(command, CancellationToken.None);

    /// <summary>Loads the show into the catalog and clears the call log, so it holds only the reserve's own calls.</summary>
    private async Task WarmCatalogAsync()
    {
        await _catalog.GetAsync(ShowId, CancellationToken.None);
        _db.Calls.Clear();
    }

    // ---- before the transaction ----

    [Fact]
    public async Task Unknown_show_returns_ShowNotFound_without_a_transaction()
    {
        var outcome = await ReserveAsync(Command() with { ShowId = Guid.NewGuid() });

        Assert.IsType<ReservationOutcome.ShowNotFound>(outcome);
        Assert.Equal(0, _tx.Invocations);
    }

    [Fact]
    public async Task Unknown_seat_returns_UnknownSeat_and_never_opens_a_transaction()
    {
        var outcome = await ReserveAsync(Command("A1", "Z9"));

        Assert.Equal(["Z9"], Assert.IsType<ReservationOutcome.UnknownSeat>(outcome).UnknownSeats);
        Assert.Equal(0, _tx.Invocations);
    }

    [Fact]
    public async Task Duplicate_seats_return_ValidationFailed_without_a_transaction()
    {
        var outcome = await ReserveAsync(Command("A1", "A1"));

        Assert.Contains("Seats", Assert.IsType<ReservationOutcome.ValidationFailed>(outcome).Errors.Keys);
        Assert.Equal(0, _tx.Invocations);
    }

    [Fact]
    public async Task Not_ready_throws_NotReadyException_before_any_read()
    {
        _readiness.IsReady = false;

        await Assert.ThrowsAsync<NotReadyException>(() => ReserveAsync(Command()));

        Assert.Empty(_db.Calls);
        Assert.Equal(0, _tx.Invocations);
    }

    // ---- the locked path ----

    [Fact]
    public async Task Transaction_calls_happen_in_contract_order()
    {
        await WarmCatalogAsync();

        await ReserveAsync(Command("A2", "A1"));

        Assert.Equal(
            [
                "Tx.Begin:reserve",
                "SetLockTimeout",
                "UserLock.Acquire:alice",
                "Seats.LockForUpdate:A1,A2",
                "Reservations.Insert",
                "Seats.Confirm:A1,A2",
                "Tx.Commit",
            ],
            _db.Calls);
        Assert.Equal(["reserve"], _tx.Operations);
    }

    [Fact]
    public async Task Lock_timeout_comes_from_options()
    {
        await ReserveAsync(Command());

        Assert.Equal(LockTimeout, _db.LastLockTimeout);
    }

    [Fact]
    public async Task Seats_are_passed_ordinally_sorted_to_lock_and_confirm()
    {
        // Ordinal: "A10" < "A2" < "B1"; a culture-aware or numeric sort would differ.
        await ReserveAsync(Command("B1", "A2", "A10"));

        Assert.Contains("Seats.LockForUpdate:A10,A2,B1", _db.Calls);
        Assert.Contains("Seats.Confirm:A10,A2,B1", _db.Calls);
    }

    [Fact]
    public async Task Unavailable_locked_seats_return_SeatTaken_listing_all_of_them_with_no_insert()
    {
        _db.SetSeat(ShowId, "A3", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(50));
        _db.SetSeat(ShowId, "A2", SeatStatus.Held, null, null);

        var outcome = await ReserveAsync(Command("A3", "A1", "A2"));

        Assert.Equal(["A2", "A3"], Assert.IsType<ReservationOutcome.SeatTaken>(outcome).UnavailableSeats);
        Assert.DoesNotContain("Reservations.Insert", _db.Calls);
        Assert.DoesNotContain(_db.Calls, c => c.StartsWith("Seats.Confirm"));
        Assert.False(_tx.LastCommit);
        Assert.Equal(SeatStatus.Available, _db.Seat(ShowId, "A1").Status);   // all-or-nothing
        Assert.Empty(_db.ReservationRows);
    }

    [Fact]
    public async Task Success_commits_and_returns_Created_with_amount_price_times_n()
    {
        var outcome = await ReserveAsync(Command("A2", "A1"));

        var r = Assert.IsType<ReservationOutcome.Created>(outcome).Reservation;
        Assert.True(_tx.LastCommit);
        Assert.Equal(SequentialIdGenerator.IdFor(1), r.Id);
        Assert.Equal(ShowId, r.ShowId);
        Assert.Equal("alice", r.UserId);
        Assert.Equal("key-1", r.IdempotencyKey);
        Assert.Equal(["A1", "A2"], r.Seats);
        Assert.Equal(50_000, r.AmountPaise);
        Assert.Equal(ReservationStatus.Confirmed, r.Status);
        Assert.Equal(_clock.UtcNow, r.CreatedAt);
        Assert.Equal(RequestHasher.Compute(ShowId, ["A1", "A2"]), r.RequestHash);

        Assert.Same(r, _db.ReservationRows[r.Id]);
        foreach (var label in new[] { "A1", "A2" })
        {
            Assert.Equal(new SeatRow(_db.Seat(ShowId, label).Ordinal, SeatStatus.Confirmed, "alice", r.Id), _db.Seat(ShowId, label));
        }
    }

    [Fact]
    public async Task Amount_overflow_throws_and_rolls_back()
    {
        _db.AddShow(new ShowInfo(SequentialIdGenerator.IdFor(200), "pricey", long.MaxValue, 4, 2), ["A1", "A2"]);

        await Assert.ThrowsAsync<OverflowException>(() =>
            ReserveAsync(new ReserveSeatsCommand(SequentialIdGenerator.IdFor(200), "alice", ["A1", "A2"], "key-1")));

        Assert.False(_tx.LastCommit);
        Assert.Empty(_db.ReservationRows);
    }

    [Fact]
    public async Task Confirm_count_mismatch_throws_InvariantViolation_and_rolls_back()
    {
        _db.ConfirmResultOverride = 1;

        await Assert.ThrowsAsync<InvariantViolationException>(() => ReserveAsync(Command("A1", "A2")));

        Assert.False(_tx.LastCommit);
        Assert.Empty(_db.ReservationRows);
        Assert.Equal(SeatStatus.Available, _db.Seat(ShowId, "A1").Status);
    }

    [Fact]
    public async Task DependencyUnavailable_propagates()
    {
        _tx.FailWith = new DependencyUnavailableException();

        await Assert.ThrowsAsync<DependencyUnavailableException>(() => ReserveAsync(Command()));
    }

    [Fact]
    public async Task A_retried_delegate_still_creates_exactly_one_reservation()
    {
        _tx.SimulatedRetries = 1;

        var outcome = await ReserveAsync(Command("A1"));

        var r = Assert.IsType<ReservationOutcome.Created>(outcome).Reservation;
        Assert.Equal([r.Id], _db.ReservationRows.Keys);
        Assert.Equal(r.Id, _db.Seat(ShowId, "A1").ReservationId);
    }
}
