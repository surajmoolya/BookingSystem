using Microsoft.Extensions.Options;

namespace SeatReservation.Application.Options;

public sealed class ReservationOptions
{
    public const string SectionName = "Reservations";

    /// <summary>Used when <c>POST /shows</c> omits <c>per_user_limit</c> (D-085).</summary>
    public int DefaultPerUserLimit { get; set; } = 4;

    /// <summary>Sanity bound on seats in one reserve request; above it the request is a 400.</summary>
    public int MaxSeatsPerRequest { get; set; } = 20;
}

public sealed class ReservationOptionsValidator : IValidateOptions<ReservationOptions>
{
    // Matches the CHECK on shows.per_user_limit (lld §4).
    public const int MaxPerUserLimit = 100;

    public ValidateOptionsResult Validate(string? name, ReservationOptions options) =>
        new OptionsFailures()
            .InRange($"{ReservationOptions.SectionName}:{nameof(options.DefaultPerUserLimit)}", options.DefaultPerUserLimit, 1, MaxPerUserLimit)
            .InRange($"{ReservationOptions.SectionName}:{nameof(options.MaxSeatsPerRequest)}", options.MaxSeatsPerRequest, 1, 1000)
            .ToResult();
}
