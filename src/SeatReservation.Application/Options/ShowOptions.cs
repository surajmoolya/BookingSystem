using Microsoft.Extensions.Options;

namespace SeatReservation.Application.Options;

public sealed class ShowOptions
{
    public const string SectionName = "Shows";

    /// <summary>When true, <c>POST /shows</c> requires a JWT (D-021).</summary>
    public bool RequireAuth { get; set; }

    /// <summary>Per-show seat cap.</summary>
    public int MaxSeats { get; set; } = 10_000;
}

public sealed class ShowOptionsValidator : IValidateOptions<ShowOptions>
{
    public ValidateOptionsResult Validate(string? name, ShowOptions options) =>
        new OptionsFailures()
            .InRange($"{ShowOptions.SectionName}:{nameof(options.MaxSeats)}", options.MaxSeats, 1, 1_000_000)
            .ToResult();
}
