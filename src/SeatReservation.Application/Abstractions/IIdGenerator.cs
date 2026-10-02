namespace SeatReservation.Application.Abstractions;

public interface IIdGenerator
{
    Guid NewId();
}
