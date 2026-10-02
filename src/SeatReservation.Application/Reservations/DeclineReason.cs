namespace SeatReservation.Application.Reservations;

/// <summary>
/// Why a reserve attempt did not create a reservation. Also the bounded label set of
/// <c>reservations_declined_total{reason}</c>; the metrics adapter maps these to snake_case.
/// </summary>
public enum DeclineReason
{
    SeatTaken,
    PerUserLimit,
    IdempotentReplay,
    IdempotencyKeyConflict,
    UnknownSeat,
    ShowNotFound,
    Validation,
}
