namespace SeatReservation.Application.Abstractions;

public interface IUserLock
{
    /// <summary>Transaction-scoped per-user advisory lock; released automatically at commit or rollback (D-035).</summary>
    Task AcquireAsync(string userId, CancellationToken ct);
}
