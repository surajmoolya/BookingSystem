using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;

namespace SeatReservation.Api.Options;

public sealed class ShutdownOptions
{
    public const string SectionName = "Shutdown";

    /// <summary>
    /// Seconds the process keeps serving after SIGTERM before Kestrel stops accepting, so the platform's proxy has switched
    /// traffic to the new instance first (D-092). Render waits up to 30s before SIGKILL.
    /// </summary>
    public int DrainSeconds { get; set; } = 5;
}

public sealed class ShutdownOptionsValidator : IValidateOptions<ShutdownOptions>
{
    public ValidateOptionsResult Validate(string? name, ShutdownOptions options) =>
        new OptionsFailures()
            .InRange($"{ShutdownOptions.SectionName}:{nameof(options.DrainSeconds)}", options.DrainSeconds, 0, 25)
            .ToResult();
}
