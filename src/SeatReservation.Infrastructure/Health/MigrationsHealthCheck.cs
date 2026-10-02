using Microsoft.Extensions.Diagnostics.HealthChecks;
using SeatReservation.Infrastructure.Migrations;

namespace SeatReservation.Infrastructure.Health;

/// <summary>Readiness stays red until the migration runner has applied every script.</summary>
public sealed class MigrationsHealthCheck(MigrationState state) : IHealthCheck
{
    public const string Name = "migrations";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = state switch
        {
            { IsReady: true } => HealthCheckResult.Healthy(),
            { GaveUp: true } => HealthCheckResult.Unhealthy("migrations gave up; restart required"),
            _ => HealthCheckResult.Unhealthy($"migrations pending (failed attempts: {state.FailedAttempts})"),
        };
        return Task.FromResult(result);
    }
}
