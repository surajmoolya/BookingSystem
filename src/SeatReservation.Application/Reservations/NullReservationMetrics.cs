using SeatReservation.Application.Abstractions;

namespace SeatReservation.Application.Reservations;

/// <summary>
/// Default <see cref="IReservationMetrics"/> until the Prometheus adapter (T-5.2) is registered by the composition root,
/// which overrides this one. Records nothing.
/// </summary>
public sealed class NullReservationMetrics : IReservationMetrics
{
    public void Confirmed(int seatCount)
    {
    }

    public void Declined(DeclineReason reason)
    {
    }

    public void Cancelled()
    {
    }

    public void ObserveDuration(ReservationResultKind kind, TimeSpan elapsed)
    {
    }
}
