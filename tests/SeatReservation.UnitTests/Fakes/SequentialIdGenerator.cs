using SeatReservation.Application.Abstractions;

namespace SeatReservation.UnitTests.Fakes;

/// <summary>Deterministic ids: 00000000-0000-0000-0000-000000000001, …002, and so on.</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _last;

    public Guid NewId() => IdFor(++_last);

    public static Guid IdFor(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
