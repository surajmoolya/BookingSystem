using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public class DataSourcesTests(PostgresFixture postgres)
{
    private static readonly DatabaseOptions Defaults = new();

    [Fact]
    public async Task Both_pools_resolve_from_DI_and_each_opens_a_connection_with_its_own_application_name()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString));
        var sources = factory.Services.GetRequiredService<DataSources>();

        Assert.NotSame(sources.Main, sources.Ops);
        Assert.Equal("seatres-api", await ApplicationNameAsync(sources.Main));
        Assert.Equal("seatres-ops", await ApplicationNameAsync(sources.Ops));
    }

    [Fact]
    public void Pools_get_their_own_sizes_timeouts_and_flags_from_options()
    {
        var options = new DatabaseOptions { MaxPoolSize = 17, OpsPoolSize = 2, ConnectionTimeoutSeconds = 9, CommandTimeoutSeconds = 7 };
        using var _ = Dispose(DataSources.Create(postgres.ConnectionString, options, includeErrorDetail: false), out var sources);

        var main = new NpgsqlConnectionStringBuilder(sources.Main.ConnectionString);
        var ops = new NpgsqlConnectionStringBuilder(sources.Ops.ConnectionString);

        Assert.Equal(17, main.MaxPoolSize);
        Assert.Equal(2, ops.MaxPoolSize);
        Assert.Equal("seatres-api", main.ApplicationName);
        Assert.Equal("seatres-ops", ops.ApplicationName);
        foreach (var b in new[] { main, ops })
        {
            Assert.Equal(9, b.Timeout);
            Assert.Equal(7, b.CommandTimeout);
            Assert.True(b.NoResetOnClose);
            Assert.False(b.IncludeErrorDetail);
        }
    }

    [Fact]
    public async Task The_ops_pool_still_serves_a_probe_while_the_main_pool_is_exhausted()
    {
        var options = new DatabaseOptions { MaxPoolSize = 2, OpsPoolSize = 1, ConnectionTimeoutSeconds = 2 };
        await using var sources = DataSources.Create(postgres.ConnectionString, options, includeErrorDetail: false);
        await using var first = await sources.Main.OpenConnectionAsync();
        await using var second = await sources.Main.OpenConnectionAsync();

        // Npgsql 8 reports pool exhaustion as an NpgsqlException wrapping the TimeoutException.
        var exhausted = await Assert.ThrowsAsync<NpgsqlException>(async () => await sources.Main.OpenConnectionAsync());
        Assert.IsType<TimeoutException>(exhausted.InnerException);

        Assert.Equal("seatres-ops", await ApplicationNameAsync(sources.Ops));
    }

    [Fact]
    public void Error_detail_flag_is_passed_through()
    {
        using var _ = Dispose(DataSources.Create(postgres.ConnectionString, Defaults, includeErrorDetail: true), out var sources);

        Assert.True(new NpgsqlConnectionStringBuilder(sources.Main.ConnectionString).IncludeErrorDetail);
    }

    [Fact]
    public void DATABASE_URL_takes_precedence_over_the_key_value_connection_string()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "postgres://urluser:urlpw@urlhost:6000/urldb",
            ["ConnectionStrings:Postgres"] = "Host=otherhost;Database=otherdb",
        }).Build();

        var b = new NpgsqlConnectionStringBuilder(ConnectionStringResolver.Resolve(config));

        Assert.Equal("urlhost", b.Host);
        Assert.Equal(6000, b.Port);
        Assert.Equal("urldb", b.Database);
    }

    [Fact]
    public void Connection_string_is_used_when_DATABASE_URL_is_absent_or_blank()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "  ",
            ["ConnectionStrings:Postgres"] = "Host=otherhost;Database=otherdb",
        }).Build();

        Assert.Equal("otherhost", new NpgsqlConnectionStringBuilder(ConnectionStringResolver.Resolve(config)).Host);
    }

    [Fact]
    public void Missing_database_configuration_fails_with_a_message_naming_both_keys()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionStringResolver.Resolve(new ConfigurationBuilder().Build()));

        Assert.Contains("DATABASE_URL", ex.Message);
        Assert.Contains("ConnectionStrings:Postgres", ex.Message);
    }

    [Fact]
    public void A_malformed_DATABASE_URL_fails_without_echoing_it()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "postgres://u:TopSecret@host/db?sslmode=bogus",
        }).Build();

        var ex = Assert.Throws<FormatException>(() => ConnectionStringResolver.Resolve(config));

        Assert.DoesNotContain("TopSecret", ex.Message);
    }

    private static async Task<string> ApplicationNameAsync(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT current_setting('application_name')", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static IDisposable Dispose(DataSources value, out DataSources sources)
    {
        sources = value;
        return new Releaser(value);
    }

    private sealed class Releaser(DataSources sources) : IDisposable
    {
        public void Dispose() => sources.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
