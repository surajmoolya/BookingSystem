namespace SeatReservation.Application.Reservations;

internal static class Timestamps
{
    // Postgres keeps timestamps to the microsecond. Stamping at that precision makes a response body and a later read
    // of the same row (a replay, a GET) show the same instant (D-096).
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), value.Offset);
}
