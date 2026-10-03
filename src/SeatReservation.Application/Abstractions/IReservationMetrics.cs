using SeatReservation.Application.Reservations;

namespace SeatReservation.Application.Abstractions;

/// <summary>
/// Business metrics, called by the services once per final outcome (never inside a retried transaction delegate).
/// </summary>
public interface IReservationMetrics
{
    void Confirmed(int seatCount);

    void Declined(DeclineReason reason);

    void Cancelled();

    /// <summary>Service-level latency of one reserve call, excluding HTTP overhead. Called once per call, including when it throws.</summary>
    void ObserveDuration(ReservationResultKind kind, TimeSpan elapsed);
}
