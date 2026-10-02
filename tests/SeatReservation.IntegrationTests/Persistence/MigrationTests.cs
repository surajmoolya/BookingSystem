using Npgsql;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Persistence;

/// <summary>
/// The migration story seen through the whole application: starting the host is what applies the schema.
/// Runner internals (locking, retry, give-up) are covered in <see cref="MigrationRunnerTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Starting_the_api_creates_the_schema_and_records_V001()
    {
        await using var api = await ApiFactory.StartAsync(postgres);

        var tables = await ColumnAsync(api.ConnectionString, "SELECT tablename FROM pg_tables WHERE schemaname='public'");
        var versions = await ColumnAsync(api.ConnectionString, "SELECT version FROM schema_migrations");

        Assert.Equivalent(new[] { "schema_migrations", "shows", "reservations", "seats" }, tables);
        Assert.Equal(["V001__init"], versions);
    }

    [Fact]
    public async Task Restarting_the_api_against_an_existing_schema_changes_nothing()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using (var first = new ApiFactory(connection))
        {
            await first.WaitUntilReadyAsync();
        }

        await using var second = new ApiFactory(connection);
        await second.WaitUntilReadyAsync();

        Assert.Equal(["V001__init"], await ColumnAsync(connection, "SELECT version FROM schema_migrations"));
    }

    [Fact]
    public async Task Several_instances_starting_together_on_an_empty_database_migrate_it_once_and_all_become_ready()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var instances = Enumerable.Range(0, 3).Select(_ => new ApiFactory(connection)).ToArray();
        try
        {
            await TestConcurrency.ConcurrentAsync(instances.Length, async i => await instances[i].WaitUntilReadyAsync());

            Assert.Equal(["V001__init"], await ColumnAsync(connection, "SELECT version FROM schema_migrations"));
        }
        finally
        {
            foreach (var instance in instances)
            {
                await instance.DisposeAsync();
            }
        }
    }

    private static async Task<List<string>> ColumnAsync(string connectionString, string sql)
    {
        var values = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
