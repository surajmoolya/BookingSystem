using Microsoft.Extensions.Diagnostics.HealthChecks;
using SeatReservation.Infrastructure.Persistence;

namespace SeatReservation.Infrastructure.Health;

/// <summary>
/// Readiness probe: <c>SELECT 1</c> on the ops pool, so a saturated main pool under load never fails the probe.
/// T-6.1 adds the <c>db_up</c> gauge.
/// </summary>
public sealed class DatabaseHealthCheck(DataSources dataSources) : IHealthCheck
{
    public const string Name = "database";

    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await using var command = dataSources.Ops.CreateCommand("SELECT 1");
            command.CommandTimeout = (int)Timeout.TotalSeconds;
            await command.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"database did not answer within {Timeout.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: Npgsql messages can name the host, and the probe result is not the place to debug it.
            return HealthCheckResult.Unhealthy($"database unreachable ({ex.GetType().Name})");
        }
    }
}
