using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;

namespace SeatReservation.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Main pool for API traffic. instances × (MaxPoolSize + OpsPoolSize) must stay below the server's max_connections.</summary>
    public int MaxPoolSize { get; set; } = 40;

    /// <summary>Separate small pool for readiness and gauges, so a burst can never starve them (D-053).</summary>
    public int OpsPoolSize { get; set; } = 3;

    /// <summary>Npgsql <c>Timeout</c>: how long to wait for a pool slot.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>Npgsql <c>Command Timeout</c>.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary><c>SET LOCAL lock_timeout</c> inside reserve and cancel transactions.</summary>
    public int LockTimeoutMs { get; set; } = 10_000;

    /// <summary>Total backoff budget for migrations at startup; readiness stays red meanwhile.</summary>
    public int StartupRetrySeconds { get; set; } = 120;
}

public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options) =>
        new OptionsFailures()
            .InRange($"{DatabaseOptions.SectionName}:{nameof(options.MaxPoolSize)}", options.MaxPoolSize, 1, 500)
            .InRange($"{DatabaseOptions.SectionName}:{nameof(options.OpsPoolSize)}", options.OpsPoolSize, 1, 50)
            .InRange($"{DatabaseOptions.SectionName}:{nameof(options.ConnectionTimeoutSeconds)}", options.ConnectionTimeoutSeconds, 1, 300)
            .InRange($"{DatabaseOptions.SectionName}:{nameof(options.CommandTimeoutSeconds)}", options.CommandTimeoutSeconds, 1, 300)
            .InRange($"{DatabaseOptions.SectionName}:{nameof(options.LockTimeoutMs)}", options.LockTimeoutMs, 1, 300_000)
            .InRange($"{DatabaseOptions.SectionName}:{nameof(options.StartupRetrySeconds)}", options.StartupRetrySeconds, 1, 3_600)
            .ToResult();
}
