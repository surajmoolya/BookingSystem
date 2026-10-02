using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SeatReservation.Infrastructure.Persistence;

namespace SeatReservation.Infrastructure.Migrations;

/// <summary>
/// Applies the embedded SQL migrations in the background, so Kestrel answers <c>/health/live</c> immediately on a cold
/// start (D-006). Runners on several instances serialize on a session advisory lock; each script runs in its own
/// transaction together with its <c>schema_migrations</c> row. A failure (e.g. the database isn't up yet) is retried
/// with exponential backoff until <c>Database:StartupRetrySeconds</c> is spent; after that the process keeps running
/// with readiness red instead of crash-looping.
/// </summary>
public sealed class MigrationRunner(
    DataSources dataSources,
    IOptions<DatabaseOptions> options,
    MigrationState state,
    ILogger<MigrationRunner> logger) : BackgroundService
{
    public const long AdvisoryLockKey = 727001;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(8);
    private static readonly IReadOnlyList<MigrationScript> EmbeddedScripts = EmbeddedMigrationScripts.Load(typeof(MigrationRunner).Assembly);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();   // never block host startup

        var budget = TimeSpan.FromSeconds(options.Value.StartupRetrySeconds);
        var clock = Stopwatch.StartNew();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var applied = await RunOnceAsync(stoppingToken);
                state.MarkReady();
                logger.LogInformation("migrations.ready applied={Applied} attempts={Attempts}", string.Join(",", applied), attempt + 1);
                await WarmUpMainPoolAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                state.RecordFailure(ex);
                var delay = Backoff(attempt);
                if (clock.Elapsed + delay > budget)
                {
                    state.MarkGaveUp();
                    logger.LogCritical(ex, "migrations.failed gave up after {Attempts} attempts in {Elapsed}; staying up with readiness red", attempt + 1, clock.Elapsed);
                    return;
                }

                logger.LogWarning("migrations.retry attempt={Attempt} next_in={Delay} error={Error}", attempt + 1, delay, state.LastError);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// One pass under the advisory lock: ensures <c>schema_migrations</c>, then applies every embedded script that is not
    /// recorded yet. Returns the versions applied by this call (empty when the schema was already current).
    /// </summary>
    public async Task<IReadOnlyList<string>> RunOnceAsync(CancellationToken ct)
    {
        await using var connection = await dataSources.Ops.OpenConnectionAsync(ct);
        await RunSqlAsync(connection, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            await RunSqlAsync(connection, null,
                "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", ct);
            var done = await ReadAppliedAsync(connection, ct);

            var applied = new List<string>();
            foreach (var script in EmbeddedScripts.Where(s => !done.Contains(s.Version)))
            {
                await ApplyAsync(connection, script, ct);
                applied.Add(script.Version);
                logger.LogInformation("migrations.applied version={Version}", script.Version);
            }

            return applied;
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    private async Task ApplyAsync(NpgsqlConnection connection, MigrationScript script, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await RunSqlAsync(connection, transaction, script.Sql, ct);
        await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES (@v)", connection, transaction))
        {
            record.Parameters.AddWithValue("v", script.Version);
            await record.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static async Task<HashSet<string>> ReadAppliedAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var versions = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("SELECT version FROM schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            versions.Add(reader.GetString(0));
        }

        return versions;
    }

    private async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        // The ops pool uses No Reset On Close, so a session lock would outlive this call if it were not released here.
        // Cancellation must not skip it, hence CancellationToken.None.
        try
        {
            await RunSqlAsync(connection, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "migrations.unlock_failed; discarding the pooled connection so the lock cannot leak");
            NpgsqlConnection.ClearPool(connection);
        }
    }

    private async Task WarmUpMainPoolAsync(CancellationToken ct)
    {
        try
        {
            var count = Math.Min(5, options.Value.MaxPoolSize);
            var connections = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => dataSources.Main.OpenConnectionAsync(ct).AsTask()));
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }

            logger.LogInformation("migrations.pool_warmed connections={Count}", count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "migrations.pool_warmup_failed; the pool will fill on demand");
        }
    }

    private static async Task RunSqlAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>0.5s, 1s, 2s, 4s, then 8s, plus up to 250ms of jitter.</summary>
    internal static TimeSpan Backoff(int attempt)
    {
        var exponential = InitialBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 10));
        var capped = Math.Min(exponential, MaxBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped + Random.Shared.Next(0, 250));
    }
}
