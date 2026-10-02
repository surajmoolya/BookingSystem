using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SeatReservation.Api.Options;

namespace SeatReservation.Api.Auth;

/// <summary>
/// The one HS256 key that both signs (<see cref="JwtTokenIssuer"/>) and validates (JwtBearer) tokens. It's the UTF-8 bytes
/// of <c>Auth:SigningKey</c>. An empty key only gets past <see cref="AuthOptionsValidator"/> in Development, where a random
/// per-process key is used instead, so tokens stop working on restart.
/// </summary>
public sealed class SigningKeyProvider(IOptions<AuthOptions> options)
{
    private readonly Lazy<SymmetricSecurityKey> _key = new(() =>
    {
        var configured = options.Value.SigningKey;
        var bytes = string.IsNullOrEmpty(configured)
            ? RandomNumberGenerator.GetBytes(AuthOptionsValidator.MinSigningKeyBytes)
            : Encoding.UTF8.GetBytes(configured);
        return new SymmetricSecurityKey(bytes);
    });

    public SymmetricSecurityKey Key => _key.Value;
}
