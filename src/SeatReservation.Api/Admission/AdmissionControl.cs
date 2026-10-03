using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SeatReservation.Api.Errors;

namespace SeatReservation.Api.Admission;

/// <summary>
/// Admission queue for DB-bound endpoints (lld §8, D-054): policy <c>db</c> is one <see cref="ConcurrencyLimiter"/> with
/// as many permits as the main pool has connections, so a burst waits in memory (latency) instead of on the Npgsql pool
/// (timeouts, 5xx). Only a full queue is rejected: 503 <c>overloaded</c> with <c>Retry-After: 1</c>.
/// Health and metrics endpoints never carry the policy.
/// </summary>
public static class AdmissionControl
{
    public const string DbPolicy = "db";

    private const int RetryAfterSeconds = 1;

    public static IServiceCollection AddAdmissionControl(this IServiceCollection services)
    {
        services.AddSingleton<DbAdmissionLimiter>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
            options.OnRejected = async (rejected, ct) =>
            {
                var context = rejected.HttpContext;
                context.RequestServices.GetRequiredService<ILogger<DbAdmissionLimiter>>()
                    .LogWarning("admission.rejected {Method} {Path}", context.Request.Method, context.Request.Path);
                context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();
                await ProblemFactory.WriteAsync(context, ProblemFactory.Create(context, StatusCodes.Status503ServiceUnavailable, ErrorCodes.Overloaded), ct);
            };
        });
        services.AddOptions<RateLimiterOptions>()
            .Configure<DbAdmissionLimiter>((options, limiter) =>
                options.AddPolicy(DbPolicy, _ => RateLimitPartition.Get(DbPolicy, _ => limiter.Create())));
        return services;
    }
}
