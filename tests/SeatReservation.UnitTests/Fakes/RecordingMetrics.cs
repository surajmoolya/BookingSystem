using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Reservations;

namespace SeatReservation.UnitTests.Fakes;

public sealed class RecordingMetrics : IReservationMetrics
{
    /// <summary>Seat count of each <c>Confirmed</c> call.</summary>
    public List<int> ConfirmedSeatCounts { get; } = [];

    public List<DeclineReason> DeclinedReasons { get; } = [];

    public int CancelledCount { get; private set; }

    public List<ReservationResultKind> DurationKinds { get; } = [];

    public void Confirmed(int seatCount) => ConfirmedSeatCounts.Add(seatCount);

    public void Declined(DeclineReason reason) => DeclinedReasons.Add(reason);

    public void Cancelled() => CancelledCount++;

    public void ObserveDuration(ReservationResultKind kind, TimeSpan elapsed) => DurationKinds.Add(kind);
}
