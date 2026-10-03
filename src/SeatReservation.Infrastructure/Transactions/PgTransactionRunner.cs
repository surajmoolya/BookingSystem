using System.Data;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Infrastructure.Persistence;

namespace SeatReservation.Infrastructure.Transactions;

/// <summary>
/// Runs a unit of work in a READ COMMITTED transaction on the main pool (D-083). Commits or rolls back according to the
/// delegate's <see cref="TxResult{T}"/>. Transient failures re-run the WHOLE delegate (up to 3 attempts, 20ms·2ⁿ plus jitter);
/// if the database stays unreachable or keeps failing transiently the caller gets a
/// <see cref="DependencyUnavailableException"/>. Anything else is a bug and is rethrown untouched, with the transaction rolled back.
/// </summary>
public sealed class PgTransactionRunner(DataSources dataSources, DbMetrics metrics, ILogger<PgTransactionRunner> logger) : ITransactionRunner
{
    public const int MaxAttempts = 3;

    public async Task<T> RunAsync<T>(string operation, Func<IUnitOfWork, CancellationToken, Task<TxResult<T>>> work, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(work);

        for (var attempt = 1; ; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await RunOnceAsync(work, ct);
            }
            catch (Exception ex)
            {
                metrics.Failed(operation, ex);
                switch (PgErrorClassifier.Classify(ex))
                {
                    case DbErrorKind.Transient or DbErrorKind.Unavailable when attempt < AttemptLimit(ex):
                        metrics.Retried(operation);
                        logger.LogWarning("db.retry operation={Operation} attempt={Attempt} sql_state={SqlState} error={Error}", operation, attempt, SqlState(ex), Describe(ex));
                        await Task.Delay(Backoff(attempt), ct);
                        continue;

                    case DbErrorKind.Transient or DbErrorKind.Unavailable:
                        logger.LogError("db.unavailable operation={Operation} attempts={Attempts} error={Error}", operation, attempt, Describe(ex));
                        throw new DependencyUnavailableException($"The database could not serve '{operation}' after {attempt} attempts.", ex);

                    case DbErrorKind.IdempotencyRace when ex is not DuplicateIdempotencyKeyException:
                        throw new DuplicateIdempotencyKeyException(ex);

                    default:
                        throw;   // cancellation, application exceptions from the delegate, and bugs pass through unchanged
                }
            }
            finally
            {
                metrics.ObserveDuration(operation, Stopwatch.GetElapsedTime(started));   // per attempt
            }
        }
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

    // A cancelled query (command timeout) is retried once; everything else transient up to MaxAttempts.
    private static int AttemptLimit(Exception ex) => PgErrorClassifier.IsQueryCanceled(ex) ? 2 : MaxAttempts;

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(20 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 20));

    private static string? SqlState(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState;

    // Type and message only: Npgsql messages never carry the password, but the full exception text could carry row data.
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
