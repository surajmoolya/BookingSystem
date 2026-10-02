using SeatReservation.Application.Reservations;

namespace SeatReservation.Application.Abstractions;

/// <summary>
/// Business metrics, called by the services once per final outcome (never inside a retried transaction delegate).
/// The service-level latency histogram (<c>ObserveDuration</c> in lld §1.3) is deferred with T-5.3 (D-089).
/// </summary>
public interface IReservationMetrics
{
    void Confirmed(int seatCount);

    void Declined(DeclineReason reason);

    void Cancelled();
}
