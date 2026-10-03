using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace SeatReservation.Api.Hosting;

/// <summary>
/// Process and Kestrel settings for burst traffic (lld §3 steps 1 and 5, T-6.7). A raised thread-pool minimum lets a
/// burst start without the pool's slow thread injection (about one thread per 0.5 s past the minimum). The 64 KB body
/// limit bounds memory per request; <c>POST /shows</c> overrides it per endpoint because a 10,000-seat show is ~190 KB.
/// </summary>
public static class RuntimeTuning
{
    public const int MinThreads = 200;

    public const long MaxRequestBodyBytes = 64 * 1024;

    public static WebApplicationBuilder AddRuntimeTuning(this WebApplicationBuilder builder)
    {
        // Only ever raise the minimum: a host with more cores may already start higher.
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, MinThreads), Math.Max(io, MinThreads));

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            kestrel.AddServerHeader = false;
        });
        return builder;
    }

    /// <summary>Logs the settings actually in effect once the host has started, so a deploy's first lines prove them.</summary>
    public static WebApplication LogRuntimeTuning(this WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            ThreadPool.GetMinThreads(out var worker, out var io);
            var kestrel = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RuntimeTuning))
                .LogInformation(
                    "runtime.tuned min_worker_threads={MinWorkerThreads} min_io_threads={MinIoThreads} max_request_body_bytes={MaxRequestBodyBytes} server_header={ServerHeader} processors={Processors}",
                    worker, io, kestrel.Limits.MaxRequestBodySize, kestrel.AddServerHeader, Environment.ProcessorCount);
        });
        return app;
    }
}
