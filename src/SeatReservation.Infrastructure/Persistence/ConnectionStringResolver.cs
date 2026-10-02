using Microsoft.Extensions.Configuration;

namespace SeatReservation.Infrastructure.Persistence;

public static class ConnectionStringResolver
{
    public const string DatabaseUrlKey = "DATABASE_URL";
    public const string ConnectionStringKey = "ConnectionStrings:Postgres";

    /// <summary>
    /// <c>DATABASE_URL</c> wins when present (Render injects it as a URL, D-061); otherwise
    /// <c>ConnectionStrings:Postgres</c> is used as a key/value string.
    /// </summary>
    public static string Resolve(IConfiguration configuration)
    {
        var url = configuration[DatabaseUrlKey];
        if (!string.IsNullOrWhiteSpace(url))
        {
            return DatabaseUrlParser.ToConnectionString(url.Trim());
        }

        var connectionString = configuration[ConnectionStringKey];
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        throw new InvalidOperationException(
            $"No database configured: set {DatabaseUrlKey} or {ConnectionStringKey}.");
    }
}
