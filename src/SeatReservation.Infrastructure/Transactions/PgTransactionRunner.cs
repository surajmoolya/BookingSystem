using System.Data;
using Microsoft.Extensions.Logging;
using SeatReservation.Application.Abstractions;
using SeatReservation.Infrastructure.Persistence;

namespace SeatReservation.Infrastructure.Transactions;

/// <summary>
/// Runs a unit of work in a READ COMMITTED transaction on the main pool (D-083). Commits or rolls back according to the
/// delegate's <see cref="TxResult{T}"/>. Failures follow <see cref="DbRetry"/>: transient ones re-run the WHOLE delegate,
/// persistent ones become a 503, and anything else is a bug, rethrown untouched with the transaction rolled back.
/// </summary>
public sealed class PgTransactionRunner(DataSources dataSources, DbMetrics metrics, ILogger<PgTransactionRunner> logger) : ITransactionRunner
{
    public const int MaxAttempts = DbRetry.MaxAttempts;

    public Task<T> RunAsync<T>(string operation, Func<IUnitOfWork, CancellationToken, Task<TxResult<T>>> work, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(work);
        return DbRetry.RunAsync(operation, attemptCt => RunOnceAsync(work, attemptCt), metrics, logger, ct);
    }

    private async Task<T> RunOnceAsync<T>(Func<IUnitOfWork, CancellationToken, Task<TxResult<T>>> work, CancellationToken ct)
    {
        await using var connection = await dataSources.Main.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var result = await work(new PgUnitOfWork(connection, transaction), ct);

        if (result.Commit)
        {
            await transaction.CommitAsync(ct);
        }
        else
        {
            await transaction.RollbackAsync(ct);
        }

        return result.Value;
    }
}
