using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SeatReservation.Api.Health;
using SeatReservation.Api.Options;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>
/// D-092: after SIGTERM the process keeps serving for <c>Shutdown:DrainSeconds</c> before Kestrel stops accepting.
/// Uses a real Kestrel on a loopback port (TestServer has no listener to stop).
/// </summary>
public sealed class ShutdownDrainTests
{
    [Fact]
    public void Production_default_drain_is_5_seconds()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .Build();

        Assert.Equal(5, configuration.GetSection(ShutdownOptions.SectionName).Get<ShutdownOptions>()!.DrainSeconds);
    }

    [Fact]
    public async Task Server_keeps_answering_during_the_drain_then_stops()
    {
        await using var app = BuildApp(drainSeconds: 2);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ping")).StatusCode);

        var stopwatch = Stopwatch.StartNew();
        var stopping = app.StopAsync();   // what SIGTERM triggers
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.False(stopping.IsCompleted);
        using (var duringDrain = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) })
        {
            // A fresh connection, as a proxy opening a new one would.
            Assert.Equal(HttpStatusCode.OK, (await duringDrain.GetAsync("/ping")).StatusCode);
        }

        await stopping;
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1.9), $"stopped after {stopwatch.Elapsed}");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => new HttpClient().GetAsync(new Uri(new Uri(address), "/ping")));
    }

    [Fact]
    public async Task Zero_drain_stops_immediately()
    {
        await using var app = BuildApp(drainSeconds: 0);
        await app.StartAsync();

        var stopwatch = Stopwatch.StartNew();
        await app.StopAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"stopped after {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Drain_ends_early_when_the_shutdown_timeout_expires()
    {
        var drain = new ShutdownDrain(
            Microsoft.Extensions.Options.Options.Create(new ShutdownOptions { DrainSeconds = 25 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShutdownDrain>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var stopwatch = Stopwatch.StartNew();
        await drain.StoppingAsync(timeout.Token);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"drain took {stopwatch.Elapsed}");
    }

    private static WebApplication BuildApp(int drainSeconds)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Shutdown:DrainSeconds"] = drainSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.Services.AddOptions<ShutdownOptions>().Bind(builder.Configuration.GetSection(ShutdownOptions.SectionName));
        builder.Services.AddHostedService<ShutdownDrain>();
        var app = builder.Build();
        app.MapGet("/ping", () => Results.Ok());
        return app;
    }
}
