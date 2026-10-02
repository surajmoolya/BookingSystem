using System.Text.RegularExpressions;
using SeatReservation.Application.Validation;

namespace SeatReservation.Application.Auth;

/// <summary>The demo-login username, which becomes the JWT <c>sub</c> and so the user id everywhere (lld §5.1).</summary>
public static partial class UsernameValidator
{
    public const string Field = "Username";
    public const int MaxLength = 64;

    public static ValidationErrors Validate(string? username)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrEmpty(username))
        {
            errors.Add(Field, "The username is required.");
        }
        else if (!Pattern().IsMatch(username))
        {
            errors.Add(Field, $"The username must be 1-{MaxLength} characters of A-Z, a-z, 0-9, '_', '.' or '-'.");
        }

        return errors;
    }

    // \z, not $: '$' also matches before a trailing '\n', which would let "alice\n" through as a distinct user id.
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
