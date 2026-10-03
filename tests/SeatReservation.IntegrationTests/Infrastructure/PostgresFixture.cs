using Npgsql;
using Testcontainers.PostgreSql;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>One real <c>postgres:16-alpine</c> container for the whole test run (D-071); tests isolate themselves by creating their own show.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("seatres")
        .WithUsername("seatres")
        .WithPassword("seatres")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Creates an empty, uniquely named database on the container and returns a connection string to it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = NewDatabaseName();
        await CreateDatabaseNamedAsync(name);
        return ConnectionStringFor(name);
    }

    public static string NewDatabaseName() => $"t_{Guid.NewGuid():N}";

    /// <summary>Connection string for a database on the container, whether or not it exists yet.</summary>
    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;

    public async Task CreateDatabaseNamedAsync(string name)
    {
        await using var admin = new NpgsqlConnection(ConnectionString);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
        await create.ExecuteNonQueryAsync();
    }

    public Task InitializeAsync() => _container.StartAsync();

    /// <summary>Stops the container (for outage tests that own their fixture). Its mapped port is not kept across a restart.</summary>
    public Task StopAsync() => _container.StopAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
