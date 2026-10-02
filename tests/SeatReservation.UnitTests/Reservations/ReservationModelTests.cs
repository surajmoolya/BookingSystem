using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Reservations;

namespace SeatReservation.UnitTests.Reservations;

public class ReservationModelTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private static Reservation NewConfirmed() =>
        Reservation.Confirmed(Guid.NewGuid(), Guid.NewGuid(), "alice", "key-1", new byte[32], ["A1", "A2"], 50_000, Created);

    [Fact]
    public void Confirmed_factory_creates_a_confirmed_reservation_that_is_not_cancelled()
    {
        var r = NewConfirmed();

        Assert.Equal(ReservationStatus.Confirmed, r.Status);
        Assert.Null(r.CancelledAt);
        Assert.Equal(Created, r.CreatedAt);
    }

    [Fact]
    public void AsCancelled_flips_status_and_stamps_the_time_without_touching_the_original()
    {
        var original = NewConfirmed();
        var at = Created.AddMinutes(5);

        var cancelled = original.AsCancelled(at);

        Assert.Equal(ReservationStatus.Cancelled, cancelled.Status);
        Assert.Equal(at, cancelled.CancelledAt);
        Assert.Equal(original.Id, cancelled.Id);
        Assert.Equal(ReservationStatus.Confirmed, original.Status);
    }

    [Fact]
    public void TxResult_helpers_set_the_commit_flag()
    {
        Assert.True(TxResult<int>.CommitWith(1).Commit);
        Assert.False(TxResult<int>.RollbackWith(1).Commit);
        Assert.Equal(7, TxResult<int>.RollbackWith(7).Value);
    }
}
