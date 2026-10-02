namespace SeatReservation.Application.Exceptions;

/// <summary>The repository hit the unique constraint on (user_id, idempotency_key): a concurrent request won the race.</summary>
public sealed class DuplicateIdempotencyKeyException : Exception
{
    private const string DefaultMessage = "A reservation with this user and idempotency key already exists.";

    public DuplicateIdempotencyKeyException()
        : base(DefaultMessage)
    {
    }

    public DuplicateIdempotencyKeyException(Exception innerException)
        : base(DefaultMessage, innerException)
    {
    }
}
