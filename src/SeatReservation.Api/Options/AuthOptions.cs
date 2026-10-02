using System.Text;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;

namespace SeatReservation.Api.Options;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>HS256 signing key, at least 32 bytes. Required outside Development (enforced with the JWT setup in T-2.2).</summary>
    public string SigningKey { get; set; } = "";

    public string Issuer { get; set; } = "seat-reservation";

    public string Audience { get; set; } = "seat-reservation-api";

    public int TokenLifetimeHours { get; set; } = 12;
}

public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public const int MinSigningKeyBytes = 32;

    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var failures = new OptionsFailures()
            .NotBlank($"{AuthOptions.SectionName}:{nameof(options.Issuer)}", options.Issuer)
            .NotBlank($"{AuthOptions.SectionName}:{nameof(options.Audience)}", options.Audience)
            .InRange($"{AuthOptions.SectionName}:{nameof(options.TokenLifetimeHours)}", options.TokenLifetimeHours, 1, 24 * 30);

        // An absent key is allowed here; T-2.2 fails startup for it outside Development.
        if (!string.IsNullOrEmpty(options.SigningKey) && Encoding.UTF8.GetByteCount(options.SigningKey) < MinSigningKeyBytes)
        {
            failures.Add($"{AuthOptions.SectionName}:{nameof(options.SigningKey)} must be at least {MinSigningKeyBytes} bytes long.");
        }

        return failures.ToResult();
    }
}
