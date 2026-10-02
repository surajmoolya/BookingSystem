using Microsoft.Extensions.Logging;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Options;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;
using SeatReservation.UnitTests.Fakes;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace SeatReservation.UnitTests.Reservations;

public class CancellationServiceTests
{
    private static readonly Guid ShowId = SequentialIdGenerator.IdFor(100);
    private static readonly Guid AliceReservation = SequentialIdGenerator.IdFor(1);
    private static readonly Guid BobReservation = SequentialIdGenerator.IdFor(2);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(1234);
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero).AddTicks(1_234_567);

    private readonly InMemoryUnitOfWork _db = new();
    private readonly FakeTransactionRunner _tx;
    private readonly FakeClock _clock = new(Now);
    private readonly FakeReadinessState _readiness = new();
    private readonly RecordingMetrics _metrics = new();
    private readonly RecordingLogger<CancellationService> _logger = new();

    public CancellationServiceTests()
    {
        _tx = new FakeTransactionRunner(_db);
        _db.AddShow(new ShowInfo(ShowId, "show", 25_000, 4, 4), ["A1", "A2", "A3", "A4"]);
        _db.AddConfirmedReservation(Booking(AliceReservation, "alice", "A1", "A2"));
        _db.AddConfirmedReservation(Booking(BobReservation, "bob", "A3"));
    }

    private static Reservation Booking(Guid id, string userId, params string[] seats) =>
        Reservation.Confirmed(id, ShowId, userId, $"key-{id}", new byte[32], seats, 25_000L * seats.Length, Now.AddMinutes(-5));

    private CancellationService Service() => new(
        _tx,
        _clock,
        _readiness,
        _metrics,
        MsOptions.Create(new ReservationOptions { LockTimeout = LockTimeout }),
        _logger);

    private Task<CancelOutcome> CancelAsync(Guid reservationId, string userId) =>
        Service().CancelAsync(new CancelReservationCommand(reservationId, userId), CancellationToken.None);

    private void AssertNothingChanged()
    {
        Assert.Equal(new SeatRow(1, SeatStatus.Confirmed, "alice", AliceReservation), _db.Seat(ShowId, "A1"));
        Assert.Equal(new SeatRow(2, SeatStatus.Confirmed, "alice", AliceReservation), _db.Seat(ShowId, "A2"));
        Assert.Equal(new SeatRow(3, SeatStatus.Confirmed, "bob", BobReservation), _db.Seat(ShowId, "A3"));
        Assert.Equal(new SeatRow(4, SeatStatus.Available), _db.Seat(ShowId, "A4"));
        Assert.All(_db.ReservationRows.Values, r => Assert.Equal(ReservationStatus.Confirmed, r.Status));
    }

    // ---- declines ----

    [Fact]
    public async Task Not_found_returns_NotFound_and_rolls_back()
    {
        var outcome = await CancelAsync(Guid.NewGuid(), "alice");

        Assert.IsType<CancelOutcome.NotFound>(outcome);
        Assert.False(_tx.LastCommit);
        Assert.DoesNotContain("Seats.ReleaseByReservation", _db.Calls);
        AssertNothingChanged();
        Assert.Equal(0, _metrics.CancelledCount);
    }

    [Fact]
    public async Task Not_owner_returns_NotOwner_and_rolls_back()
    {
        var outcome = await CancelAsync(AliceReservation, "bob");

        Assert.IsType<CancelOutcome.NotOwner>(outcome);
        Assert.False(_tx.LastCommit);
        Assert.DoesNotContain("Seats.ReleaseByReservation", _db.Calls);
        Assert.DoesNotContain("Reservations.MarkCancelled", _db.Calls);
        AssertNothingChanged();
        Assert.Equal(0, _metrics.CancelledCount);
    }

    [Fact]
    public async Task Already_cancelled_returns_AlreadyCancelled_unchanged_with_no_metric()
    {
        await CancelAsync(AliceReservation, "alice");
        var firstCancelledAt = _db.ReservationRows[AliceReservation].CancelledAt;
        _clock.Advance(TimeSpan.FromMinutes(1));
        _db.Calls.Clear();

        var outcome = await CancelAsync(AliceReservation, "alice");

        var r = Assert.IsType<CancelOutcome.AlreadyCancelled>(outcome).Reservation;
        Assert.Equal(ReservationStatus.Cancelled, r.Status);
        Assert.Equal(firstCancelledAt, r.CancelledAt);
        Assert.False(_tx.LastCommit);
        Assert.DoesNotContain("Seats.ReleaseByReservation", _db.Calls);
        Assert.DoesNotContain("Reservations.MarkCancelled", _db.Calls);
        Assert.Equal(1, _metrics.CancelledCount);   // the first cancel's, nothing more
    }

    [Fact]
    public async Task Already_cancelled_reservation_of_another_user_is_still_NotOwner()
    {
        await CancelAsync(AliceReservation, "alice");

        var outcome = await CancelAsync(AliceReservation, "bob");

        Assert.IsType<CancelOutcome.NotOwner>(outcome);
    }

    // ---- the owner's cancel ----

    [Fact]
    public async Task Owner_cancel_releases_by_reservation_id_then_marks_cancelled_and_commits()
    {
        await CancelAsync(AliceReservation, "alice");

        Assert.Equal(
            [
                "Tx.Begin:cancel",
                "SetLockTimeout",
                "UserLock.Acquire:alice",
                "Reservations.GetByIdForUpdate",
                "Seats.ReleaseByReservation",
                "Reservations.MarkCancelled",
                "Tx.Commit",
            ],
            _db.Calls);
        Assert.Equal(["cancel"], _tx.Operations);
        Assert.Equal(LockTimeout, _db.LastLockTimeout);
    }

    [Fact]
    public async Task User_lock_is_acquired_before_the_reservation_lookup()
    {
        await CancelAsync(AliceReservation, "alice");

        Assert.True(
            _db.Calls.IndexOf("UserLock.Acquire:alice") < _db.Calls.IndexOf("Reservations.GetByIdForUpdate"),
            string.Join(", ", _db.Calls));
    }

    [Fact]
    public async Task User_lock_is_the_callers_even_when_they_are_not_the_owner()
    {
        await CancelAsync(AliceReservation, "bob");

        Assert.Contains("UserLock.Acquire:bob", _db.Calls);
        Assert.DoesNotContain("UserLock.Acquire:alice", _db.Calls);
    }

    [Fact]
    public async Task Owner_cancel_frees_only_its_own_seats_and_returns_the_cancelled_reservation()
    {
        var outcome = await CancelAsync(AliceReservation, "alice");

        var r = Assert.IsType<CancelOutcome.Cancelled>(outcome).Reservation;
        var expectedAt = Now.AddTicks(-7);   // truncated to microseconds, what Postgres stores (D-096)
        Assert.Equal(AliceReservation, r.Id);
        Assert.Equal("alice", r.UserId);
        Assert.Equal(["A1", "A2"], r.Seats);
        Assert.Equal(ReservationStatus.Cancelled, r.Status);
        Assert.Equal(expectedAt, r.CancelledAt);

        var row = _db.ReservationRows[AliceReservation];
        Assert.Equal(ReservationStatus.Cancelled, row.Status);
        Assert.Equal(expectedAt, row.CancelledAt);

        Assert.Equal(new SeatRow(1, SeatStatus.Available), _db.Seat(ShowId, "A1"));
        Assert.Equal(new SeatRow(2, SeatStatus.Available), _db.Seat(ShowId, "A2"));
        Assert.Equal(new SeatRow(3, SeatStatus.Confirmed, "bob", BobReservation), _db.Seat(ShowId, "A3"));
        Assert.Equal(ReservationStatus.Confirmed, _db.ReservationRows[BobReservation].Status);
    }

    // ---- recording ----

    [Fact]
    public async Task Cancelled_metric_is_recorded_once()
    {
        await CancelAsync(AliceReservation, "alice");

        Assert.Equal(1, _metrics.CancelledCount);
        Assert.Empty(_metrics.ConfirmedSeatCounts);
        Assert.Empty(_metrics.DeclinedReasons);
    }

    [Fact]
    public async Task Cancelled_metric_is_recorded_once_even_if_the_runner_retried_the_delegate()
    {
        _tx.SimulatedRetries = 2;

        var outcome = await CancelAsync(AliceReservation, "alice");

        Assert.IsType<CancelOutcome.Cancelled>(outcome);
        Assert.Equal(3, _db.Calls.Count(c => c == "Seats.ReleaseByReservation"));
        Assert.Equal(1, _metrics.CancelledCount);
        Assert.Single(_logger.Entries, e => e.Message.StartsWith("reservation.cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_is_logged_at_Information_with_the_reservation_details()
    {
        await CancelAsync(AliceReservation, "alice");

        var (level, message) = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.StartsWith("reservation.cancelled", message, StringComparison.Ordinal);
        Assert.Contains($"reservation_id={AliceReservation}", message, StringComparison.Ordinal);
        Assert.Contains("user_id=alice", message, StringComparison.Ordinal);
        Assert.Contains($"show_id={ShowId}", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declines_log_nothing()
    {
        await CancelAsync(Guid.NewGuid(), "alice");
        await CancelAsync(AliceReservation, "bob");

        Assert.Empty(_logger.Entries);
    }

    // ---- failures ----

    [Fact]
    public async Task Not_ready_throws_NotReadyException_before_any_transaction()
    {
        _readiness.IsReady = false;

        await Assert.ThrowsAsync<NotReadyException>(() => CancelAsync(AliceReservation, "alice"));

        Assert.Equal(0, _tx.Invocations);
        Assert.Empty(_db.Calls);
    }

    [Fact]
    public async Task DependencyUnavailable_propagates_and_records_nothing()
    {
        _tx.FailWith = new DependencyUnavailableException();

        await Assert.ThrowsAsync<DependencyUnavailableException>(() => CancelAsync(AliceReservation, "alice"));

        Assert.Equal(0, _metrics.CancelledCount);
        Assert.Empty(_logger.Entries);
        AssertNothingChanged();
    }
}
