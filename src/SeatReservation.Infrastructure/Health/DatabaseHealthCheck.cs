using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Diagnostics;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.Infrastructure.Transactions;

namespace SeatReservation.Infrastructure.Health;

/// <summary>
/// Readiness probe: <c>SELECT 1</c> on the ops pool, so a saturated main pool under load never fails the probe.
/// Each probe sets <c>db_up</c> and is timed as operation <c>health</c>.
/// </summary>
public sealed class DatabaseHealthCheck(DataSources dataSources, DbMetrics metrics) : IHealthCheck
{
    public const string Name = "database";

    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var started = Stopwatch.GetTimestamp();

        try
        {
            await using var command = dataSources.Ops.CreateCommand("SELECT 1");
            command.CommandTimeout = (int)Timeout.TotalSeconds;
            await command.ExecuteScalarAsync(timeout.Token);
            metrics.SetUp(true);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            metrics.SetUp(false);
            return HealthCheckResult.Unhealthy($"database did not answer within {Timeout.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.SetUp(false);
            metrics.Failed(DbMetrics.Operations.Health, ex);
            // Type only: Npgsql messages can name the host, and the probe result is not the place to debug it.
            return HealthCheckResult.Unhealthy($"database unreachable ({ex.GetType().Name})");
        }
        finally
        {
            metrics.ObserveDuration(DbMetrics.Operations.Health, Stopwatch.GetElapsedTime(started));
        }
    }
}
