using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>
/// Mints HS256 JWTs by hand, so tests don't depend on the issuing endpoint and can forge the bad variants
/// (wrong signature, expired, wrong audience) that authentication tests need.
/// </summary>
public static class TestTokens
{
    public const string Issuer = "seat-reservation";
    public const string Audience = "seat-reservation-api";

    /// <summary>A valid token for <paramref name="userId"/> against the key <see cref="ApiFactory"/> configures; override pieces to break it.</summary>
    public static string Create(
        string userId,
        string signingKey = ApiFactory.SigningKey,
        string issuer = Issuer,
        string audience = Audience,
        TimeSpan? lifetime = null,
        DateTimeOffset? now = null)
    {
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        var header = new Dictionary<string, object> { ["alg"] = "HS256", ["typ"] = "JWT" };
        var payload = new Dictionary<string, object>
        {
            ["sub"] = userId,
            ["iss"] = issuer,
            ["aud"] = audience,
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = (issuedAt + (lifetime ?? TimeSpan.FromHours(1))).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        var signingInput = $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}";
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>A client for the same server that sends <paramref name="userId"/>'s token as a Bearer header.</summary>
    public static HttpClient AsUser(this HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Create(userId));
        return client;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
