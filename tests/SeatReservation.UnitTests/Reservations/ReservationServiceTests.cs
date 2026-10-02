using Microsoft.Extensions.Logging;
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
    private readonly FakeReservationReadRepository _reservationReads;
    private readonly ShowCatalog _catalog;
    private readonly SequentialIdGenerator _ids = new();
    private readonly FakeClock _clock = new();
    private readonly FakeReadinessState _readiness = new();
    private readonly RecordingMetrics _metrics = new();
    private readonly RecordingLogger<ReservationService> _logger = new();

    public ReservationServiceTests()
    {
        _tx = new FakeTransactionRunner(_db);
        _showReads = new FakeShowReadRepository(_db);
        _reservationReads = new FakeReservationReadRepository(_db);
        _catalog = new ShowCatalog(_showReads);
        _db.AddShow(new ShowInfo(ShowId, "show", 25_000, 4, 5), ["A1", "A2", "A3", "A10", "B1"]);
    }

    private ReservationService Service() => new(
        _catalog,
        new ReserveSeatsValidator(MsOptions.Create(new ReservationOptions())),
        _reservationReads,
        _tx,
        _ids,
        _clock,
        _readiness,
        _metrics,
        MsOptions.Create(new ReservationOptions { LockTimeout = LockTimeout }),
        _logger);

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
                "Read.FastPath",
                "Tx.Begin:reserve",
                "SetLockTimeout",
                "UserLock.Acquire:alice",
                "Reservations.FindByKey",
                "Seats.CountConfirmedByUser",
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
        // Taken after the fast-path read, so only the locked path can see it.
        _reservationReads.AfterFastPath = () =>
        {
            _db.SetSeat(ShowId, "A3", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(50));
            _db.SetSeat(ShowId, "A2", SeatStatus.Held, null, null);
        };

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
    public async Task Created_at_is_stamped_at_the_microsecond_precision_postgres_keeps()
    {
        _clock.UtcNow = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);   // 0.1234567 s

        var r = Assert.IsType<ReservationOutcome.Created>(await ReserveAsync(Command("A1"))).Reservation;

        Assert.Equal(_clock.UtcNow.AddTicks(-7), r.CreatedAt);   // 0.123456 s
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

    // ---- per-user limit ----

    private void GiveAliceSeats(params string[] labels) =>
        _db.AddConfirmedReservation(Reservation.Confirmed(
            SequentialIdGenerator.IdFor(60), ShowId, "alice", "earlier", new byte[32], labels, 25_000 * labels.Length, _clock.UtcNow));

    [Fact]
    public async Task Request_larger_than_the_limit_returns_PerUserLimit_without_touching_the_db()
    {
        await WarmCatalogAsync();

        var outcome = await ReserveAsync(Command("A1", "A2", "A3", "A10", "B1"));   // limit is 4

        Assert.Equal(new ReservationOutcome.PerUserLimit(4, 0, 5), outcome);
        Assert.Empty(_db.Calls);
        Assert.Equal(0, _tx.Invocations);
    }

    [Fact]
    public async Task Held_plus_requested_over_the_limit_returns_PerUserLimit_and_rolls_back()
    {
        GiveAliceSeats("A1", "A2", "A3");

        var outcome = await ReserveAsync(Command("A10", "B1"));

        Assert.Equal(new ReservationOutcome.PerUserLimit(4, 3, 2), outcome);
        Assert.False(_tx.LastCommit);
        Assert.DoesNotContain(_db.Calls, c => c.StartsWith("Seats.LockForUpdate"));
        Assert.DoesNotContain("Reservations.Insert", _db.Calls);
        Assert.Equal(SeatStatus.Available, _db.Seat(ShowId, "A10").Status);
    }

    [Fact]
    public async Task Held_plus_requested_exactly_at_the_limit_succeeds()
    {
        GiveAliceSeats("A1", "A2", "A3");

        Assert.IsType<ReservationOutcome.Created>(await ReserveAsync(Command("A10")));
    }

    [Fact]
    public async Task Only_this_users_seats_count_towards_the_limit()
    {
        _db.SetSeat(ShowId, "A1", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(70));
        _db.SetSeat(ShowId, "A2", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(70));

        Assert.IsType<ReservationOutcome.Created>(await ReserveAsync(Command("A3", "A10", "B1")));
    }

    [Fact]
    public async Task Held_count_happens_after_the_user_lock_and_before_the_seat_lock()
    {
        await ReserveAsync(Command("A1"));

        var userLock = _db.Calls.IndexOf("UserLock.Acquire:alice");
        var count = _db.Calls.IndexOf("Seats.CountConfirmedByUser");
        var seatLock = _db.Calls.FindIndex(c => c.StartsWith("Seats.LockForUpdate"));
        Assert.True(userLock < count && count < seatLock, string.Join(" → ", _db.Calls));
    }

    [Fact]
    public async Task A_custom_per_show_limit_is_respected()
    {
        var showId = SequentialIdGenerator.IdFor(300);
        _db.AddShow(new ShowInfo(showId, "tight", 100, 2, 4), ["A1", "A2", "A3", "A4"]);
        var service = Service();

        Assert.IsType<ReservationOutcome.Created>(
            await service.ReserveAsync(new ReserveSeatsCommand(showId, "alice", ["A1"], "k1"), CancellationToken.None));
        Assert.IsType<ReservationOutcome.Created>(
            await service.ReserveAsync(new ReserveSeatsCommand(showId, "alice", ["A2"], "k2"), CancellationToken.None));

        var third = await service.ReserveAsync(new ReserveSeatsCommand(showId, "alice", ["A3"], "k3"), CancellationToken.None);
        Assert.Equal(new ReservationOutcome.PerUserLimit(2, 2, 1), third);

        var tooBig = await service.ReserveAsync(new ReserveSeatsCommand(showId, "carol", ["A3", "A4", "A1"], "k1"), CancellationToken.None);
        Assert.Equal(new ReservationOutcome.PerUserLimit(2, 0, 3), tooBig);
    }

    // ---- idempotency ----

    /// <summary>Reserves A1+A2 as alice with key-1 and returns the reservation.</summary>
    private async Task<Reservation> FirstReservationAsync()
    {
        var created = Assert.IsType<ReservationOutcome.Created>(await ReserveAsync(Command("A1", "A2")));
        _db.Calls.Clear();
        return created.Reservation;
    }

    [Fact]
    public async Task Same_key_and_same_request_returns_Replayed_and_rolls_back()
    {
        var original = await FirstReservationAsync();

        var outcome = await ReserveAsync(Command("A1", "A2"));

        Assert.Same(original, Assert.IsType<ReservationOutcome.Replayed>(outcome).Reservation);
        Assert.False(_tx.LastCommit);
        Assert.Equal(
            ["Read.FastPath", "Tx.Begin:reserve", "SetLockTimeout", "UserLock.Acquire:alice", "Reservations.FindByKey", "Tx.Rollback"],
            _db.Calls);
        Assert.Single(_db.ReservationRows);
    }

    [Fact]
    public async Task Seat_order_permutation_is_a_replay()
    {
        var original = await FirstReservationAsync();

        var outcome = await ReserveAsync(Command("A2", "A1"));

        Assert.Equal(original.Id, Assert.IsType<ReservationOutcome.Replayed>(outcome).Reservation.Id);
    }

    [Fact]
    public async Task Same_key_with_different_seats_returns_KeyConflict_with_the_original_id()
    {
        var original = await FirstReservationAsync();

        var outcome = await ReserveAsync(Command("A3"));

        Assert.Equal(new ReservationOutcome.KeyConflict(original.Id), outcome);
        Assert.False(_tx.LastCommit);
        Assert.Equal(SeatStatus.Available, _db.Seat(ShowId, "A3").Status);
        Assert.Single(_db.ReservationRows);
    }

    [Fact]
    public async Task Same_key_on_a_different_show_returns_KeyConflict()
    {
        var original = await FirstReservationAsync();
        var otherShow = SequentialIdGenerator.IdFor(400);
        _db.AddShow(otherShow, "A1", "A2");

        var outcome = await ReserveAsync(new ReserveSeatsCommand(otherShow, "alice", ["A1", "A2"], "key-1"));

        Assert.Equal(new ReservationOutcome.KeyConflict(original.Id), outcome);
    }

    [Fact]
    public async Task The_key_is_scoped_to_the_user()
    {
        await FirstReservationAsync();

        var outcome = await ReserveAsync(Command("A3") with { UserId = "bob" });   // bob reuses alice's key string

        Assert.IsType<ReservationOutcome.Created>(outcome);
    }

    [Fact]
    public async Task A_replay_is_returned_even_when_the_user_is_at_the_limit()
    {
        var full = Assert.IsType<ReservationOutcome.Created>(await ReserveAsync(Command("A1", "A2", "A3", "A10"))).Reservation;
        _db.Calls.Clear();

        var outcome = await ReserveAsync(Command("A10", "A3", "A2", "A1"));

        Assert.Equal(full.Id, Assert.IsType<ReservationOutcome.Replayed>(outcome).Reservation.Id);
        Assert.DoesNotContain("Seats.CountConfirmedByUser", _db.Calls);
    }

    [Fact]
    public async Task A_replay_is_returned_even_when_the_seats_are_now_its_own()
    {
        // The replay's seats are confirmed (by this very reservation); it must not turn into seat_taken.
        var original = await FirstReservationAsync();

        var outcome = await ReserveAsync(Command("A1", "A2"));

        Assert.Equal(original.Id, Assert.IsType<ReservationOutcome.Replayed>(outcome).Reservation.Id);
        Assert.DoesNotContain(_db.Calls, c => c.StartsWith("Seats."));
    }

    [Fact]
    public async Task A_declined_attempt_does_not_bind_the_key()
    {
        _db.SetSeat(ShowId, "A1", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(80));
        Assert.IsType<ReservationOutcome.SeatTaken>(await ReserveAsync(Command("A1")));

        var outcome = await ReserveAsync(Command("A2"));   // same key-1, different seats

        Assert.IsType<ReservationOutcome.Created>(outcome);
    }

    private Reservation Winner(params string[] seats) => Reservation.Confirmed(
        SequentialIdGenerator.IdFor(90), ShowId, "alice", "key-1", RequestHasher.Compute(ShowId, seats), seats, 25_000 * seats.Length, _clock.UtcNow);

    [Fact]
    public async Task DuplicateIdempotencyKeyException_rereads_and_replays()
    {
        var winner = Winner("A1", "A2");
        _db.ConcurrentWinner = winner;

        var outcome = await ReserveAsync(Command("A2", "A1"));

        Assert.Equal(winner.Id, Assert.IsType<ReservationOutcome.Replayed>(outcome).Reservation.Id);
        Assert.Equal(1, _reservationReads.FindByKeyCalls);
        Assert.False(_tx.LastCommit);
        Assert.Equal([winner.Id], _db.ReservationRows.Keys);
    }

    [Fact]
    public async Task DuplicateIdempotencyKeyException_with_another_request_returns_KeyConflict()
    {
        var winner = Winner("A3");
        _db.ConcurrentWinner = winner;

        var outcome = await ReserveAsync(Command("A1", "A2"));

        Assert.Equal(new ReservationOutcome.KeyConflict(winner.Id), outcome);
        Assert.Equal(SeatStatus.Available, _db.Seat(ShowId, "A1").Status);
    }

    [Fact]
    public async Task DuplicateIdempotencyKeyException_without_a_row_is_an_invariant_violation()
    {
        _db.NextInsertFailure = new DuplicateIdempotencyKeyException();

        await Assert.ThrowsAsync<InvariantViolationException>(() => ReserveAsync(Command("A1")));
    }

    // ---- fast path ----

    [Fact]
    public async Task Fast_path_uses_a_single_snapshot_call()
    {
        await ReserveAsync(Command("A1", "A2"));

        Assert.Equal(1, _reservationReads.FastPathCalls);
        Assert.Equal(0, _reservationReads.FindByKeyCalls);
    }

    [Fact]
    public async Task Fast_path_declines_seats_owned_by_other_users_without_a_transaction()
    {
        _db.SetSeat(ShowId, "B1", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(50));
        _db.SetSeat(ShowId, "A10", SeatStatus.Held, null, null);

        var outcome = await ReserveAsync(Command("B1", "A1", "A10"));

        Assert.Equal(["A10", "B1"], Assert.IsType<ReservationOutcome.SeatTaken>(outcome).UnavailableSeats);
        Assert.Equal(0, _tx.Invocations);
        Assert.Equal(1, _reservationReads.FastPathCalls);
    }

    [Fact]
    public async Task Fast_path_does_not_decline_a_seat_owned_by_the_same_user()
    {
        _db.AddConfirmedReservation(Reservation.Confirmed(
            SequentialIdGenerator.IdFor(60), ShowId, "alice", "earlier", new byte[32], ["A1"], 25_000, _clock.UtcNow));

        var outcome = await ReserveAsync(Command("A1"));   // a new key: the locked path decides (and declines)

        Assert.Equal(["A1"], Assert.IsType<ReservationOutcome.SeatTaken>(outcome).UnavailableSeats);
        Assert.Equal(1, _tx.Invocations);
    }

    [Fact]
    public async Task An_existing_key_skips_the_fast_path_decline()
    {
        var original = await FirstReservationAsync();                                  // alice, key-1, A1+A2
        _db.SetSeat(ShowId, "A3", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(50));

        var outcome = await ReserveAsync(Command("A3"));                               // key-1 reused for bob's seat

        Assert.Equal(new ReservationOutcome.KeyConflict(original.Id), outcome);         // not seat_taken
        Assert.Equal(2, _tx.Invocations);                                              // the first reserve, then this one
    }

    // ---- metrics and logs ----

    [Fact]
    public async Task Metrics_confirmed_only_on_Created()
    {
        await ReserveAsync(Command("A1", "A2"));

        Assert.Equal([2], _metrics.ConfirmedSeatCounts);
        Assert.Empty(_metrics.DeclinedReasons);
    }

    [Fact]
    public async Task Metrics_replay_records_idempotent_replay_and_not_confirmed()
    {
        await FirstReservationAsync();
        _metrics.ConfirmedSeatCounts.Clear();

        await ReserveAsync(Command("A1", "A2"));

        Assert.Empty(_metrics.ConfirmedSeatCounts);
        Assert.Equal([DeclineReason.IdempotentReplay], _metrics.DeclinedReasons);
    }

    public static TheoryData<string, DeclineReason> Declines => new()
    {
        { "seat_taken_fast", DeclineReason.SeatTaken },
        { "seat_taken_locked", DeclineReason.SeatTaken },
        { "per_user_limit", DeclineReason.PerUserLimit },
        { "key_conflict", DeclineReason.IdempotencyKeyConflict },
        { "unknown_seat", DeclineReason.UnknownSeat },
        { "show_not_found", DeclineReason.ShowNotFound },
        { "validation", DeclineReason.Validation },
    };

    [Theory]
    [MemberData(nameof(Declines))]
    public async Task Metrics_each_decline_reason_recorded_once(string scenario, DeclineReason expected)
    {
        var command = Command("A3");
        switch (scenario)
        {
            case "seat_taken_fast":
                _db.SetSeat(ShowId, "A3", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(50));
                break;
            case "seat_taken_locked":
                _reservationReads.AfterFastPath = () => _db.SetSeat(ShowId, "A3", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(50));
                break;
            case "per_user_limit":
                command = Command("A1", "A2", "A3", "A10", "B1");
                break;
            case "key_conflict":
                await FirstReservationAsync();
                _metrics.ConfirmedSeatCounts.Clear();
                break;
            case "unknown_seat":
                command = Command("Z9");
                break;
            case "show_not_found":
                command = command with { ShowId = Guid.NewGuid() };
                break;
            case "validation":
                command = command with { IdempotencyKey = "" };
                break;
        }

        await ReserveAsync(command);

        Assert.Equal([expected], _metrics.DeclinedReasons);
        Assert.Empty(_metrics.ConfirmedSeatCounts);
    }

    [Fact]
    public async Task Metrics_recorded_once_even_if_the_runner_retried_the_delegate()
    {
        _tx.SimulatedRetries = 2;

        await ReserveAsync(Command("A1"));

        Assert.Equal([1], _metrics.ConfirmedSeatCounts);
        Assert.Empty(_metrics.DeclinedReasons);
    }

    [Fact]
    public async Task Exceptions_record_no_metrics()
    {
        _tx.FailWith = new DependencyUnavailableException();
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => ReserveAsync(Command()));

        _readiness.IsReady = false;
        await Assert.ThrowsAsync<NotReadyException>(() => ReserveAsync(Command()));

        Assert.Empty(_metrics.ConfirmedSeatCounts);
        Assert.Empty(_metrics.DeclinedReasons);
    }

    [Fact]
    public async Task Created_logs_reservation_confirmed_at_information()
    {
        var r = Assert.IsType<ReservationOutcome.Created>(await ReserveAsync(Command("A1"))).Reservation;

        var entry = Assert.Single(_logger.Entries, e => e.Message.StartsWith("reservation.confirmed"));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(r.Id.ToString(), entry.Message);
    }

    [Fact]
    public async Task Declines_and_replays_log_at_debug_only()
    {
        await FirstReservationAsync();
        _logger.Entries.Clear();

        await ReserveAsync(Command("A1", "A2"));   // replay
        await ReserveAsync(Command("A3"));         // key conflict

        Assert.All(_logger.Entries, e => Assert.Equal(LogLevel.Debug, e.Level));
        Assert.Single(_logger.Entries, e => e.Message.StartsWith("reservation.replayed"));
        Assert.Single(_logger.Entries, e => e.Message.StartsWith("reservation.declined") && e.Message.Contains("IdempotencyKeyConflict"));
    }

    [Fact]
    public async Task The_raw_idempotency_key_is_never_logged()
    {
        const string key = "super-secret-client-key-123";
        var command = Command("A1") with { IdempotencyKey = key };

        await ReserveAsync(command);
        await ReserveAsync(command);   // replay

        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains(key));
        var attempt = _logger.Entries.First(e => e.Message.StartsWith("reservation.attempt")).Message;
        Assert.Contains($"idempotency_key_hash={RequestHasherTests.Sha256Prefix(key)}", attempt);
    }
}
