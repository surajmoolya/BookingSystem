using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SeatReservation.Infrastructure.Health;

namespace SeatReservation.Api.Health;

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    /// <summary>Registers the readiness checks: the database answers and the schema is migrated.</summary>
    public static IServiceCollection AddReadinessChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name, tags: [ReadyTag])
            .AddCheck<MigrationsHealthCheck>(MigrationsHealthCheck.Name, tags: [ReadyTag]);
        return services;
    }

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: the process is up. No dependency checks, so a DB outage never restarts the container.
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
            .AllowAnonymous();

        // Readiness: 200 only when every "ready" check is Healthy, otherwise 503. Render routes traffic on this.
        // T-6.2 replaces the default plain-text writer with the JSON body from lld §5.7.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains(ReadyTag),
                ResultStatusCodes =
                {
                    [HealthStatus.Healthy] = StatusCodes.Status200OK,
                    [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                    [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
                },
            })
            .AllowAnonymous();

        return app;
    }
}
