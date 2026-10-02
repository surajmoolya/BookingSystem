namespace SeatReservation.Application.Exceptions;

/// <summary>The database stayed unreachable after retries; surfaces as 503 <c>dependency_unavailable</c> (D-056).</summary>
public sealed class DependencyUnavailableException : Exception
{
    public DependencyUnavailableException()
        : base("A required dependency is unavailable.")
    {
    }

    public DependencyUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
