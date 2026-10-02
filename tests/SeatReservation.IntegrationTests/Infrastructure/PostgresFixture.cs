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

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
