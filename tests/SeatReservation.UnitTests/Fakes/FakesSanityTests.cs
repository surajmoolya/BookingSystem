using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;

namespace SeatReservation.UnitTests.Fakes;

/// <summary>Guards the test doubles themselves, so the service tests built on them can be trusted.</summary>
public class FakesSanityTests
{
    private static readonly Guid ShowId = SequentialIdGenerator.IdFor(100);
    private readonly InMemoryUnitOfWork _uow = new();
    private readonly FakeTransactionRunner _runner;

    public FakesSanityTests()
    {
        _runner = new FakeTransactionRunner(_uow);
        _uow.AddShow(ShowId, "A1", "A2", "A3");
    }

    private Task<bool> ConfirmA1Async(bool commit) =>
        _runner.RunAsync("test", async (uow, ct) =>
        {
            var n = await uow.Seats.ConfirmAsync(ShowId, ["A1"], "alice", SequentialIdGenerator.IdFor(1), ct);
            return new TxResult<bool>(n == 1, commit);
        }, CancellationToken.None);

    [Fact]
    public async Task Runner_reports_Commit_false_for_RollbackWith_and_discards_the_writes()
    {
        var result = await _runner.RunAsync("test", async (uow, ct) =>
        {
            await uow.Seats.ConfirmAsync(ShowId, ["A1"], "alice", SequentialIdGenerator.IdFor(1), ct);
            return TxResult<string>.RollbackWith("declined");
        }, CancellationToken.None);

        Assert.Equal("declined", result);
        Assert.False(_runner.LastCommit);
        Assert.Equal(1, _runner.Rollbacks);
        Assert.Equal(0, _runner.Commits);
        Assert.Equal(SeatStatus.Available, _uow.Seat(ShowId, "A1").Status);
    }

    [Fact]
    public async Task Runner_reports_Commit_true_for_CommitWith_and_keeps_the_writes()
    {
        await ConfirmA1Async(commit: true);

        Assert.True(_runner.LastCommit);
        Assert.Equal(1, _runner.Commits);
        Assert.Equal(SeatStatus.Confirmed, _uow.Seat(ShowId, "A1").Status);
        Assert.Equal("alice", _uow.Seat(ShowId, "A1").UserId);
    }

    [Fact]
    public async Task Runner_rolls_back_and_rethrows_when_the_delegate_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.RunAsync<int>("test", async (uow, ct) =>
        {
            await uow.Seats.ConfirmAsync(ShowId, ["A1"], "alice", SequentialIdGenerator.IdFor(1), ct);
            throw new InvalidOperationException();
        }, CancellationToken.None));

        Assert.False(_runner.LastCommit);
        Assert.Equal(SeatStatus.Available, _uow.Seat(ShowId, "A1").Status);
    }

    [Fact]
    public async Task Simulated_retry_invokes_the_delegate_twice_and_the_first_attempt_leaves_no_trace()
    {
        _runner.SimulatedRetries = 1;
        var runs = 0;

        var result = await _runner.RunAsync("test", async (uow, ct) =>
        {
            runs++;
            var n = await uow.Seats.ConfirmAsync(ShowId, ["A1"], "alice", SequentialIdGenerator.IdFor(1), ct);
            return TxResult<int>.CommitWith(n);
        }, CancellationToken.None);

        Assert.Equal(2, runs);
        Assert.Equal(1, result);   // the retried attempt saw A1 available again
        Assert.Equal(1, _runner.Invocations);
        Assert.Equal(1, _runner.Commits);
        Assert.Contains("Tx.Retry", _uow.Calls);
    }

    [Fact]
    public async Task Runner_can_be_told_to_fail_before_running_any_work()
    {
        _runner.FailWith = new DependencyUnavailableException();

        await Assert.ThrowsAsync<DependencyUnavailableException>(() => ConfirmA1Async(commit: true));

        Assert.DoesNotContain(_uow.Calls, c => c.StartsWith("Seats."));
    }

    [Fact]
    public async Task Confirm_only_updates_seats_that_are_still_available()
    {
        _uow.SetSeat(ShowId, "A2", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(9));

        var updated = await _uow.Seats.ConfirmAsync(ShowId, ["A1", "A2"], "alice", SequentialIdGenerator.IdFor(1), CancellationToken.None);

        Assert.Equal(1, updated);
        Assert.Equal("bob", _uow.Seat(ShowId, "A2").UserId);
    }

    [Fact]
    public async Task LockForUpdate_returns_rows_in_label_order_and_logs_the_labels_as_passed()
    {
        var locked = await _uow.Seats.LockForUpdateAsync(ShowId, ["A3", "A1", "ZZ"], CancellationToken.None);

        Assert.Equal(["A1", "A3"], locked.Select(s => s.Label));
        Assert.Contains("Seats.LockForUpdate:A3,A1,ZZ", _uow.Calls);
    }

    [Fact]
    public async Task Release_is_keyed_by_reservation_id()
    {
        var mine = SequentialIdGenerator.IdFor(1);
        _uow.SetSeat(ShowId, "A1", SeatStatus.Confirmed, "alice", mine);
        _uow.SetSeat(ShowId, "A2", SeatStatus.Confirmed, "bob", SequentialIdGenerator.IdFor(2));

        var released = await _uow.Seats.ReleaseByReservationAsync(mine, CancellationToken.None);

        Assert.Equal(1, released);
        Assert.Equal(SeatStatus.Available, _uow.Seat(ShowId, "A1").Status);
        Assert.Null(_uow.Seat(ShowId, "A1").UserId);
        Assert.Equal("bob", _uow.Seat(ShowId, "A2").UserId);
    }

    [Fact]
    public async Task Duplicate_user_and_key_insert_throws_like_the_unique_constraint()
    {
        var r = Reservation.Confirmed(SequentialIdGenerator.IdFor(1), ShowId, "alice", "key", new byte[32], ["A1"], 1, DateTimeOffset.UnixEpoch);
        await _uow.Reservations.InsertAsync(r, CancellationToken.None);

        await Assert.ThrowsAsync<DuplicateIdempotencyKeyException>(() =>
            _uow.Reservations.InsertAsync(r with { Id = SequentialIdGenerator.IdFor(2) }, CancellationToken.None));
    }

    [Fact]
    public async Task Injected_insert_failure_fires_once()
    {
        _uow.NextInsertFailure = new DuplicateIdempotencyKeyException();
        var r = Reservation.Confirmed(SequentialIdGenerator.IdFor(1), ShowId, "alice", "key", new byte[32], ["A1"], 1, DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<DuplicateIdempotencyKeyException>(() => _uow.Reservations.InsertAsync(r, CancellationToken.None));
        await _uow.Reservations.InsertAsync(r, CancellationToken.None);

        Assert.Contains(r.Id, _uow.ReservationRows.Keys);
    }

    [Fact]
    public async Task Fast_path_read_returns_the_key_reservation_and_seat_owners_in_one_call()
    {
        var reads = new FakeReservationReadRepository(_uow);
        _uow.AddConfirmedReservation(Reservation.Confirmed(SequentialIdGenerator.IdFor(1), ShowId, "bob", "k", new byte[32], ["A2"], 1, DateTimeOffset.UnixEpoch));

        var snap = await reads.GetFastPathSnapshotAsync("bob", "k", ShowId, ["A1", "A2"], CancellationToken.None);

        Assert.Equal(1, reads.FastPathCalls);
        Assert.NotNull(snap.ExistingForKey);
        Assert.Equal(SeatStatus.Confirmed, snap.Owners.Single(o => o.Label == "A2").Status);
        Assert.Equal("bob", snap.Owners.Single(o => o.Label == "A2").UserId);
        Assert.Null(snap.Owners.Single(o => o.Label == "A1").UserId);
    }

    [Fact]
    public async Task Show_snapshot_is_in_ordinal_order_and_can_be_overridden()
    {
        var reads = new FakeShowReadRepository(_uow);

        var snapshot = await reads.GetSeatSnapshotAsync(ShowId, CancellationToken.None);
        Assert.Equal(["A1", "A2", "A3"], snapshot.Select(s => s.Label));

        reads.SnapshotOverride = [new SeatState("X", SeatStatus.Held)];
        Assert.Single(await reads.GetSeatSnapshotAsync(ShowId, CancellationToken.None));
        Assert.Equal(2, reads.SnapshotCalls);
    }

    [Fact]
    public void Sequential_ids_clock_and_token_issuer_are_deterministic()
    {
        var ids = new SequentialIdGenerator();
        Assert.Equal(SequentialIdGenerator.IdFor(1), ids.NewId());
        Assert.Equal(SequentialIdGenerator.IdFor(2), ids.NewId());

        var clock = new FakeClock();
        var start = clock.UtcNow;
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(start.AddMinutes(3), clock.UtcNow);

        var issuer = new FakeTokenIssuer();
        Assert.Equal("token-for-alice", issuer.Issue("alice").AccessToken);
        Assert.Equal(["alice"], issuer.IssuedFor);
    }

    [Fact]
    public void Recording_metrics_captures_each_call()
    {
        var metrics = new RecordingMetrics();

        metrics.Confirmed(2);
        metrics.Declined(DeclineReason.SeatTaken);
        metrics.Cancelled();

        Assert.Equal([2], metrics.ConfirmedSeatCounts);
        Assert.Equal([DeclineReason.SeatTaken], metrics.DeclinedReasons);
        Assert.Equal(1, metrics.CancelledCount);
    }
}
