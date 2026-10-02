namespace SeatReservation.Api.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: the process is up. No dependency checks, so a DB outage never restarts the container.
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
            .AllowAnonymous();

        return app;
    }
}
