using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;

namespace SeatReservation.Api.Options;

public sealed class AdmissionOptions
{
    public const string SectionName = "Admission";

    /// <summary>Concurrent DB-bound requests. Unset means <c>Database:MaxPoolSize</c> (resolved where the limiter is built, T-6.5).</summary>
    public int? PermitLimit { get; set; }

    /// <summary>Requests queued beyond the permits; overflow is a 503 <c>overloaded</c>.</summary>
    public int QueueLimit { get; set; } = 50_000;
}

public sealed class AdmissionOptionsValidator : IValidateOptions<AdmissionOptions>
{
    public ValidateOptionsResult Validate(string? name, AdmissionOptions options)
    {
        var failures = new OptionsFailures()
            .InRange($"{AdmissionOptions.SectionName}:{nameof(options.QueueLimit)}", options.QueueLimit, 0, 1_000_000);

        if (options.PermitLimit is { } permits)
        {
            failures.InRange($"{AdmissionOptions.SectionName}:{nameof(options.PermitLimit)}", permits, 1, 1_000);
        }

        return failures.ToResult();
    }
}
