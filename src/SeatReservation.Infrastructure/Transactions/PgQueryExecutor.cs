using Microsoft.Extensions.Logging;

namespace SeatReservation.Infrastructure.Transactions;

/// <summary>
/// Runs an autocommit read with the same classifier and retry loop as transactions (lld §7), so a database blip on a
/// read is retried and, if it persists, a 503 <c>dependency_unavailable</c> instead of a raw Npgsql exception (500).
/// </summary>
public sealed class PgQueryExecutor(DbMetrics metrics, ILogger<PgQueryExecutor> logger)
{
    public Task<T> RunAsync<T>(string operation, Func<CancellationToken, Task<T>> query, CancellationToken ct) =>
        DbRetry.RunAsync(operation, query, metrics, logger, ct);
}
