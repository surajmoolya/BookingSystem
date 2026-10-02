using Microsoft.Extensions.Options;
using SeatReservation.Api.Options;

namespace SeatReservation.Api.Health;

/// <summary>
/// Delays shutdown by <see cref="ShutdownOptions.DrainSeconds"/> while the server keeps serving (D-092).
/// The host runs every <see cref="IHostedLifecycleService.StoppingAsync"/> before any <see cref="IHostedService.StopAsync"/>,
/// so Kestrel is still accepting during the delay; without it, requests the proxy routes to the old instance during a
/// deploy's switchover get a 502.
/// </summary>
public sealed class ShutdownDrain(IOptions<ShutdownOptions> options, ILogger<ShutdownDrain> logger) : IHostedLifecycleService
{
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        var drain = TimeSpan.FromSeconds(options.Value.DrainSeconds);
        if (drain <= TimeSpan.Zero)
        {
            return;
        }

        logger.LogInformation("shutdown.draining seconds={DrainSeconds}", options.Value.DrainSeconds);
        try
        {
            await Task.Delay(drain, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The host's shutdown timeout ran out first; stop now.
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
