namespace SeatReservation.Application.Abstractions;

public interface ITransactionRunner
{
    /// <summary>
    /// Opens a connection and a READ COMMITTED transaction, runs <paramref name="work"/>, and commits iff the result
    /// says so, otherwise rolls back. Transient DB errors re-run the WHOLE delegate, so it must have no side effects
    /// outside the unit of work: metrics and logs are emitted by the caller after this returns (D-083).
    /// Throws <see cref="Exceptions.DependencyUnavailableException"/> when retries are exhausted.
    /// </summary>
    Task<T> RunAsync<T>(string operation, Func<IUnitOfWork, CancellationToken, Task<TxResult<T>>> work, CancellationToken ct);
}

public readonly record struct TxResult<T>(T Value, bool Commit)
{
    public static TxResult<T> CommitWith(T value) => new(value, true);

    public static TxResult<T> RollbackWith(T value) => new(value, false);
}
