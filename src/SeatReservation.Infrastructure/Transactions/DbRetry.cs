using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using SeatReservation.Application.Exceptions;

namespace SeatReservation.Infrastructure.Transactions;

/// <summary>
/// The retry matrix of lld §7 (D-055), shared by <see cref="PgTransactionRunner"/> and <see cref="PgQueryExecutor"/>.
/// Transient and unavailable failures re-run the whole attempt (up to 3 times, 20ms·2ⁿ plus jitter; a cancelled query
/// only once); if they persist the caller gets a <see cref="DependencyUnavailableException"/> (503). Anything else is a
/// bug and is rethrown untouched. Each attempt is timed and each failure counted under <paramref name="operation"/>.
/// </summary>
internal static class DbRetry
{
    public const int MaxAttempts = 3;

    public static async Task<T> RunAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> attemptOnce,
        DbMetrics metrics,
        ILogger logger,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                return await attemptOnce(ct);
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

    // A cancelled query (command timeout) is retried once; everything else transient up to MaxAttempts.
    private static int AttemptLimit(Exception ex) => PgErrorClassifier.IsQueryCanceled(ex) ? 2 : MaxAttempts;

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(20 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 20));

    private static string? SqlState(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState;

    // Type and message only: Npgsql messages never carry the password, but the full exception text could carry row data.
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
