using SeatReservation.Application.Abstractions;

namespace SeatReservation.UnitTests.Fakes;

public sealed class FakeClock(DateTimeOffset? start = null) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = start ?? new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}
