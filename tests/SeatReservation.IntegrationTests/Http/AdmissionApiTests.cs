using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Api.Admission;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>
/// The <c>db</c> admission queue (T-6.5, lld §8): with 1 permit and a queue of 1, a second request waits, a third is
/// rejected with 503 <c>overloaded</c>, and probes and metrics are never queued. No database needed.
/// </summary>
public sealed class AdmissionApiTests
{
    internal static ApiFactory NewFactory(AdmissionGate gate) => new(
        ApiFactory.UnreachableConnectionString,
        new Dictionary<string, string?> { ["Admission:PermitLimit"] = "1", ["Admission:QueueLimit"] = "1" },
        services =>
        {
            services.AddSingleton(gate);
            services.AddControllers().AddApplicationPart(typeof(AdmissionProbeController).Assembly);
        });

    [Fact]
    public async Task Second_request_queues_third_is_rejected_503_overloaded_and_the_queued_one_then_completes()
    {
        var gate = new AdmissionGate();
        await using var factory = NewFactory(gate);
        using var client = factory.CreateClient();
        var limiter = factory.Services.GetRequiredService<DbAdmissionLimiter>();

        var held = client.GetAsync("/_probe/admission/hold");
        Assert.True(await gate.WaitEnteredAsync(), "the first request never got its permit");
        var queued = client.GetAsync("/_probe/admission/hold");
        await WaitForAsync(() => limiter.QueuedCount == 1);

        using (var rejected = await client.GetAsync("/_probe/admission/hold"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), rejected.Headers.RetryAfter?.Delta);
            Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal("overloaded", body.RootElement.GetProperty("code").GetString());
        }

        // Permit taken and queue full: liveness, readiness and metrics still answer at once (their status is not the point).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);   // no DB here
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/metrics")).StatusCode);
        Assert.False(queued.IsCompleted);

        gate.Release();

        using var first = await held;
        using var second = await queued;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(0, limiter.QueuedCount);
    }

    [Fact]
    public void Permits_default_to_the_main_pool_size()
    {
        using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString, new Dictionary<string, string?> { ["Database:MaxPoolSize"] = "17" });

        var limiter = factory.Services.GetRequiredService<DbAdmissionLimiter>();

        Assert.Equal(17, limiter.PermitLimit);
        Assert.Equal(50_000, limiter.QueueLimit);
    }

    [Fact]
    public void Exactly_the_db_bound_endpoints_carry_the_db_policy()
    {
        using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => !e.RoutePattern.RawText!.StartsWith("_probe", StringComparison.Ordinal))
            .ToList();

        var limited = endpoints
            .Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == AdmissionControl.DbPolicy)
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            ["GET reservations/{id}", "GET shows/{id}", "POST reservations/{id}/cancel", "POST shows", "POST shows/{id}/reserve"],
            limited);
        Assert.All(
            endpoints.Where(e => e.RoutePattern.RawText!.TrimStart('/') is "health/live" or "health/ready" or "metrics" or "auth/token"),
            e => Assert.Null(e.Metadata.GetMetadata<EnableRateLimitingAttribute>()));
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met within 10s");
            await Task.Delay(10);
        }
    }
}
