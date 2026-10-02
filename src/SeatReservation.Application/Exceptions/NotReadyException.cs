namespace SeatReservation.Application.Exceptions;

/// <summary>Thrown for DB operations before migrations have completed; surfaces as 503 <c>not_ready</c>.</summary>
public sealed class NotReadyException : Exception
{
    public NotReadyException()
        : base("The service is not ready yet: database migrations have not completed.")
    {
    }
}
