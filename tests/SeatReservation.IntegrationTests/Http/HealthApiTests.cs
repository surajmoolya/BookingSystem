using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using SeatReservation.Infrastructure.Health;
using SeatReservation.Infrastructure.Migrations;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>
/// <c>/health/ready</c> (T-1.13): 200 only when the database answers <c>SELECT 1</c> on the ops pool and migrations are done;
/// otherwise 503. Liveness never follows the database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HealthApiTests(PostgresFixture postgres)
{
    [Fact]
    public void Database_and_migrations_checks_are_registered_with_the_ready_tag()
    {
        using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString);

        var registrations = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        Assert.Equal(
            [DatabaseHealthCheck.Name, MigrationsHealthCheck.Name],
            registrations.Where(r => r.Tags.Contains("ready")).Select(r => r.Name).Order());
    }
    [Fact]
    public async Task Ready_200_with_db()
    {
        await using var factory = await ApiFactory.StartAsync(postgres);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task Ready_503_when_db_unreachable_and_live_stays_200()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString);
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready");
        using var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Ready_503_when_db_up_but_migrations_not_done()
    {
        // Same database, but the migration runner never runs, so IReadinessState stays false.
        await using var factory = new ApiFactory(
            await postgres.CreateDatabaseAsync(),
            configureServices: services =>
            {
                var runner = services.Single(d => d.ImplementationType == typeof(MigrationRunner));
                services.Remove(runner);
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(factory.Services.GetRequiredService<MigrationState>().IsReady);
    }

    [Fact]
    public async Task Ready_turns_503_when_db_goes_away_and_200_when_it_returns_while_live_stays_200()
    {
        await using var factory = await ApiFactory.StartAsync(postgres);
        using var client = factory.CreateClient();
        var database = new NpgsqlConnectionStringBuilder(factory.ConnectionString).Database!;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        // FORCE also terminates the ops pool's idle connections, as a database restart would.
        await AdminAsync($"DROP DATABASE {database} WITH (FORCE)");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

        await AdminAsync($"CREATE DATABASE {database}");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Ready_answers_within_the_probe_timeout_when_the_db_host_does_not_respond()
    {
        // A non-routable address: the connect attempt hangs rather than being refused, so only the 2s probe timeout ends it.
        await using var factory = new ApiFactory("Host=10.255.255.1;Port=5432;Database=x;Username=x;Password=x;Timeout=30");
        using var client = factory.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync("/health/ready");
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"readiness took {stopwatch.Elapsed}");
    }

    private async Task AdminAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync();
    }
}
