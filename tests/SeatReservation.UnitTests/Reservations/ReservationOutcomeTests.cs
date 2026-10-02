using SeatReservation.Application.Reservations;

namespace SeatReservation.UnitTests.Reservations;

public class ReservationOutcomeTests
{
    private static readonly Reservation Sample =
        Reservation.Confirmed(Guid.NewGuid(), Guid.NewGuid(), "alice", "key-1", new byte[32], ["A1"], 25_000, DateTimeOffset.UnixEpoch);

    public static TheoryData<ReservationOutcome, DeclineReason?> Outcomes => new()
    {
        { new ReservationOutcome.Created(Sample), null },
        { new ReservationOutcome.Replayed(Sample), DeclineReason.IdempotentReplay },
        { new ReservationOutcome.SeatTaken(["A1"]), DeclineReason.SeatTaken },
        { new ReservationOutcome.PerUserLimit(4, 3, 2), DeclineReason.PerUserLimit },
        { new ReservationOutcome.KeyConflict(Guid.NewGuid()), DeclineReason.IdempotencyKeyConflict },
        { new ReservationOutcome.UnknownSeat(["Z9"]), DeclineReason.UnknownSeat },
        { new ReservationOutcome.ValidationFailed(new Dictionary<string, string[]>()), DeclineReason.Validation },
        { new ReservationOutcome.ShowNotFound(), DeclineReason.ShowNotFound },
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Each_outcome_maps_to_its_decline_reason(ReservationOutcome outcome, DeclineReason? expected) =>
        Assert.Equal(expected, outcome.DeclineReason);

    [Fact]
    public void Every_decline_reason_has_an_outcome()
    {
        var covered = Outcomes.Select(row => (DeclineReason?)row[1]).OfType<DeclineReason>().ToHashSet();

        Assert.Equal(Enum.GetValues<DeclineReason>().ToHashSet(), covered);
    }
}
