namespace SeatReservation.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
