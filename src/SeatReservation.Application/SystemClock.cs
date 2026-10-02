using SeatReservation.Application.Abstractions;

namespace SeatReservation.Application;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
