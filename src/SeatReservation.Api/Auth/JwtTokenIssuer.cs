using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SeatReservation.Api.Options;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Auth;

namespace SeatReservation.Api.Auth;

/// <summary>Signs HS256 JWTs carrying <c>sub</c> (the user id), <c>iss</c>, <c>aud</c>, <c>iat</c>, <c>nbf</c>, <c>exp</c> and <c>jti</c> (lld §5.1).</summary>
public sealed class JwtTokenIssuer(IOptions<AuthOptions> options, SigningKeyProvider signingKey, IClock clock) : ITokenIssuer
{
    private static readonly JsonWebTokenHandler Handler = new();

    public IssuedToken Issue(string userId)
    {
        var auth = options.Value;
        var lifetime = TimeSpan.FromHours(auth.TokenLifetimeHours);
        var now = clock.UtcNow.UtcDateTime;

        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = auth.Issuer,
            Audience = auth.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(signingKey.Key, SecurityAlgorithms.HmacSha256),
        });

        return new IssuedToken(token, lifetime);
    }
}
