namespace SeatReservation.Application.Abstractions;

public interface IReadinessState
{
    /// <summary>True once the schema migrations have completed.</summary>
    bool IsReady { get; }
}
