using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Auth;

namespace SeatReservation.UnitTests.Fakes;

public sealed class FakeTokenIssuer : ITokenIssuer
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>User ids a token was issued for, in order. Empty means the issuer was never called.</summary>
    public List<string> IssuedFor { get; } = [];

    public IssuedToken Issue(string userId)
    {
        IssuedFor.Add(userId);
        return new IssuedToken($"token-for-{userId}", Lifetime);
    }
}
