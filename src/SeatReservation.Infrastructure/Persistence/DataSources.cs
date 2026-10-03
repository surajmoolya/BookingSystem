using Npgsql;

namespace SeatReservation.Infrastructure.Persistence;

/// <summary>
/// The two Npgsql pools (D-053): <see cref="Main"/> carries API traffic, <see cref="Ops"/> is a small separate pool for
/// readiness probes, migrations and gauge queries, so a burst that exhausts the main pool can never starve them.
/// </summary>
public sealed class DataSources(NpgsqlDataSource main, NpgsqlDataSource ops) : IAsyncDisposable
{
    public const string MainApplicationName = "seatres-api";
    public const string OpsApplicationName = "seatres-ops";

    public NpgsqlDataSource Main { get; } = main;

    public NpgsqlDataSource Ops { get; } = ops;

    /// <param name="connectionString">Key/value connection string (already resolved from DATABASE_URL if needed).</param>
    /// <param name="includeErrorDetail">Npgsql <c>Include Error Detail</c>; off outside Development because details can echo row data.</param>
    public static DataSources Create(string connectionString, DatabaseOptions options, bool includeErrorDetail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);

        return new DataSources(
            Build(connectionString, options, options.MaxPoolSize, MainApplicationName, includeErrorDetail),
            Build(connectionString, options, options.OpsPoolSize, OpsApplicationName, includeErrorDetail));
    }

    public async ValueTask DisposeAsync()
    {
        await Main.DisposeAsync();
        await Ops.DisposeAsync();
    }

    private static NpgsqlDataSource Build(string connectionString, DatabaseOptions options, int poolSize, string applicationName, bool includeErrorDetail)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = poolSize,
            Timeout = options.ConnectionTimeoutSeconds,
            CommandTimeout = options.CommandTimeoutSeconds,
            ApplicationName = applicationName,
            NoResetOnClose = true,
            IncludeErrorDetail = includeErrorDetail,
        };

        // Name = the pool's label in Npgsql's metrics (pool.name); without it Npgsql uses the connection string, host and all.
        return new NpgsqlDataSourceBuilder(builder.ConnectionString) { Name = applicationName }.Build();
    }
}
