using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SeatReservation.Infrastructure.Migrations;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.Infrastructure.Transactions;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>A fresh database with the schema applied and the two pools open on it, for repository-layer tests with no HTTP involved.</summary>
public sealed class MigratedDatabase : IAsyncDisposable
{
    private MigratedDatabase(string connectionString, DataSources sources)
    {
        ConnectionString = connectionString;
        Sources = sources;
    }

    public string ConnectionString { get; }

    public DataSources Sources { get; }

    /// <summary>One metrics instance per database, for repositories and runners the tests build by hand.</summary>
    public DbMetrics Metrics { get; } = TestMetrics.NewDb();

    public static async Task<MigratedDatabase> CreateAsync(PostgresFixture postgres, DatabaseOptions? options = null)
    {
        options ??= new DatabaseOptions { MaxPoolSize = 8, OpsPoolSize = 2 };
        var connectionString = await postgres.CreateDatabaseAsync();
        var sources = DataSources.Create(connectionString, options, includeErrorDetail: true);
        var runner = new MigrationRunner(sources, Options.Create(options), new MigrationState(), NullLogger<MigrationRunner>.Instance);
        await runner.RunOnceAsync(CancellationToken.None);
        return new MigratedDatabase(connectionString, sources);
    }

    /// <summary>Inserts a one-seat show and returns its id.</summary>
    public async Task<Guid> InsertShowAsync()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO shows (id, name, price_paise, total_seats) VALUES ('{id}', 'x', 100, 1)");
        return id;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await Sources.Ops.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(string sql)
    {
        await using var connection = await Sources.Ops.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public ValueTask DisposeAsync() => Sources.DisposeAsync();
}
