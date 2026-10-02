using Npgsql;

namespace SeatReservation.Infrastructure.Persistence;

/// <summary>
/// Converts a <c>postgres://user:pass@host:port/db?sslmode=…</c> URL (what Render injects as DATABASE_URL)
/// into Npgsql connection settings, since Npgsql doesn't accept URI syntax (D-061).
/// Error messages never include the URL, because it carries the password.
/// </summary>
public static class DatabaseUrlParser
{
    private const int DefaultPort = 5432;

    public static bool IsDatabaseUrl(string? value) =>
        value is not null
        && (value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase));

    public static string ToConnectionString(string databaseUrl) => Parse(databaseUrl).ConnectionString;

    /// <exception cref="FormatException">The value is not a usable postgres URL.</exception>
    public static NpgsqlConnectionStringBuilder Parse(string databaseUrl)
    {
        if (!IsDatabaseUrl(databaseUrl) || !Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri))
        {
            throw new FormatException("DATABASE_URL must be a postgres:// or postgresql:// URL.");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new FormatException("DATABASE_URL has no host.");
        }

        var database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (database.Length == 0)
        {
            throw new FormatException("DATABASE_URL has no database name.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.DnsSafeHost,   // strips the brackets from an IPv6 literal
            Port = uri.Port > 0 ? uri.Port : DefaultPort,
            Database = database,
        };

        if (uri.UserInfo.Length > 0)
        {
            // Split on the first ':' of the still-encoded text, so an encoded ':' inside the password survives.
            var separator = uri.UserInfo.IndexOf(':');
            var user = separator < 0 ? uri.UserInfo : uri.UserInfo[..separator];
            builder.Username = Uri.UnescapeDataString(user);
            if (separator >= 0)
            {
                builder.Password = Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);
            }
        }

        if (TryGetQueryValue(uri.Query, "sslmode") is { } sslMode)
        {
            builder.SslMode = ParseSslMode(sslMode);
        }

        return builder;
    }

    private static string? TryGetQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }

    private static SslMode ParseSslMode(string value) => value.ToLowerInvariant() switch
    {
        "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "require" => SslMode.Require,
        "verify-ca" => SslMode.VerifyCA,
        "verify-full" => SslMode.VerifyFull,
        _ => throw new FormatException($"DATABASE_URL has an unsupported sslmode '{value}'."),
    };
}
