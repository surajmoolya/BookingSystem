namespace SeatReservation.Api.Errors;

/// <summary>The machine-readable <c>code</c> of every problem response. Clients and the burst tool classify outcomes by these.</summary>
public static class ErrorCodes
{
    public const string Validation = "validation";
    public const string UnknownSeat = "unknown_seat";
    public const string ShowNotFound = "show_not_found";
    public const string ReservationNotFound = "reservation_not_found";
    public const string SeatTaken = "seat_taken";
    public const string PerUserLimit = "per_user_limit";
    public const string IdempotencyKeyConflict = "idempotency_key_conflict";
    public const string NotOwner = "not_owner";
    public const string DependencyUnavailable = "dependency_unavailable";
    public const string NotReady = "not_ready";
    public const string Overloaded = "overloaded";
    public const string BadRequest = "bad_request";
    public const string PayloadTooLarge = "payload_too_large";
    public const string InternalError = "internal_error";
}
