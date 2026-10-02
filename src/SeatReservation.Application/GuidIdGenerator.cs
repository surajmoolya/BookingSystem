using SeatReservation.Application.Abstractions;

namespace SeatReservation.Application;

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}
