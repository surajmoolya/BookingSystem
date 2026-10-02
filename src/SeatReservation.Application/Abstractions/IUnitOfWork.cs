namespace SeatReservation.Application.Abstractions;

/// <summary>Repositories bound to one connection and one transaction.</summary>
public interface IUnitOfWork
{
    IUserLock UserLock { get; }

    IReservationRepository Reservations { get; }

    ISeatRepository Seats { get; }

    IShowRepository Shows { get; }

    /// <summary>Transaction-local <c>lock_timeout</c>.</summary>
    Task SetLockTimeoutAsync(TimeSpan timeout, CancellationToken ct);
}
