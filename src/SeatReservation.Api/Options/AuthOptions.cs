using System.Text;
using Microsoft.Extensions.Options;
using SeatReservation.Application.Options;

namespace SeatReservation.Api.Options;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>The key committed in <c>appsettings.Development.json</c>; it's public, so it's refused in every other environment.</summary>
    public const string DevelopmentSigningKey = "dev-only-signing-key-do-not-use-in-production-0123456789";

    /// <summary>HS256 signing key, at least 32 bytes. Required outside Development; there a missing key means a random per-process one.</summary>
    public string SigningKey { get; set; } = "";

    public string Issuer { get; set; } = "seat-reservation";

    public string Audience { get; set; } = "seat-reservation-api";

    public int TokenLifetimeHours { get; set; } = 12;
}

public sealed class AuthOptionsValidator(IHostEnvironment environment) : IValidateOptions<AuthOptions>
{
    public const int MinSigningKeyBytes = 32;

    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var failures = new OptionsFailures()
            .NotBlank($"{AuthOptions.SectionName}:{nameof(options.Issuer)}", options.Issuer)
            .NotBlank($"{AuthOptions.SectionName}:{nameof(options.Audience)}", options.Audience)
            .InRange($"{AuthOptions.SectionName}:{nameof(options.TokenLifetimeHours)}", options.TokenLifetimeHours, 1, 24 * 30);

        if (string.IsNullOrEmpty(options.SigningKey))
        {
            // Outside Development a missing key must stop the host: an ephemeral key would silently invalidate every token on restart.
            if (!environment.IsDevelopment())
            {
                failures.Add($"{AuthOptions.SectionName}:{nameof(options.SigningKey)} is required outside Development.");
            }
        }
        else if (options.SigningKey == AuthOptions.DevelopmentSigningKey && !environment.IsDevelopment())
        {
            failures.Add($"{AuthOptions.SectionName}:{nameof(options.SigningKey)} is the public Development key; set a secret one outside Development.");
        }
        else if (Encoding.UTF8.GetByteCount(options.SigningKey) < MinSigningKeyBytes)
        {
            failures.Add($"{AuthOptions.SectionName}:{nameof(options.SigningKey)} must be at least {MinSigningKeyBytes} bytes long.");
        }

        return failures.ToResult();
    }
}
