namespace SeatReservation.Application.Auth;

public abstract record IssueTokenOutcome
{
    private IssueTokenOutcome()
    {
    }

    /// <summary>A token was issued; <see cref="UserId"/> is its <c>sub</c>.</summary>
    public sealed record Issued(string UserId, IssuedToken Token) : IssueTokenOutcome;

    /// <summary>The username failed validation; no token was issued.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : IssueTokenOutcome;
}
