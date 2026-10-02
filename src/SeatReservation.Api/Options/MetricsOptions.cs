using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;

namespace SeatReservation.Api.Options;

public sealed class MetricsOptions
{
    public const string SectionName = "Metrics";

    /// <summary>How long a seat-gauge query result is reused between scrapes.</summary>
    public int SeatGaugeCacheSeconds { get; set; } = 1;

    /// <summary>How many of the most recently created shows get a <c>show_seats{show_id,state}</c> series (D-086).</summary>
    public int MaxShowsInGauges { get; set; } = 200;
}

public sealed class MetricsOptionsValidator : IValidateOptions<MetricsOptions>
{
    public ValidateOptionsResult Validate(string? name, MetricsOptions options) =>
        new OptionsFailures()
            .InRange($"{MetricsOptions.SectionName}:{nameof(options.SeatGaugeCacheSeconds)}", options.SeatGaugeCacheSeconds, 0, 3_600)
            .InRange($"{MetricsOptions.SectionName}:{nameof(options.MaxShowsInGauges)}", options.MaxShowsInGauges, 1, 10_000)
            .ToResult();
}
