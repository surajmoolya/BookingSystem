using SeatReservation.Application.Abstractions;

namespace SeatReservation.Application.Auth;

/// <summary>Demo login (D-020): a valid username gets a token whose subject is exactly that username. No password.</summary>
public sealed class AuthService(ITokenIssuer issuer)
{
    public IssueTokenOutcome IssueToken(string? username)
    {
        var errors = UsernameValidator.Validate(username);
        if (!errors.IsValid)
        {
            return new IssueTokenOutcome.Invalid(errors.ToDictionary());
        }

        return new IssueTokenOutcome.Issued(username!, issuer.Issue(username!));
    }
}
