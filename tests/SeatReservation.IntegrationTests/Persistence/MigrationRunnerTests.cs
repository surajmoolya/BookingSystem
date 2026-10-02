using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using SeatReservation.Application.Abstractions;
using SeatReservation.Infrastructure.Migrations;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public class MigrationRunnerTests(PostgresFixture postgres)
{
    private const string V001 = "V001__init";
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Pooling=false";

    private static DatabaseOptions Options(int retrySeconds = 30) => new() { StartupRetrySeconds = retrySeconds, MaxPoolSize = 5 };

    private static MigrationRunner NewRunner(DataSources sources, MigrationState state, DatabaseOptions? options = null) =>
        new(sources, Microsoft.Extensions.Options.Options.Create(options ?? Options()), state, NullLogger<MigrationRunner>.Instance);

    [Fact]
    public void Embedded_scripts_load_in_version_order()
    {
        var scripts = EmbeddedMigrationScripts.Load(typeof(MigrationRunner).Assembly);

        Assert.Equal(V001, scripts[0].Version);
        Assert.Contains("CREATE TABLE seats", scripts[0].Sql);
        Assert.Equal(scripts.OrderBy(s => s.Version, StringComparer.Ordinal).Select(s => s.Version), scripts.Select(s => s.Version));
    }

    [Fact]
    public async Task First_run_applies_V001_and_a_second_run_is_a_no_op()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var sources = DataSources.Create(connection, Options(), includeErrorDetail: false);
        var runner = NewRunner(sources, new MigrationState());

        var first = await runner.RunOnceAsync(CancellationToken.None);
        var second = await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal([V001], first);
        Assert.Empty(second);
        Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM schema_migrations"));
        Assert.Equal(4, await CountAsync(connection, "SELECT count(*) FROM pg_tables WHERE schemaname='public'"));
    }

    [Fact]
    public async Task Two_runners_racing_on_a_fresh_database_apply_the_script_once()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var sources = DataSources.Create(connection, Options(), includeErrorDetail: false);
        var runners = Enumerable.Range(0, 3).Select(_ => NewRunner(sources, new MigrationState())).ToArray();

        var results = await Task.WhenAll(runners.Select(r => Task.Run(() => r.RunOnceAsync(CancellationToken.None))));

        Assert.Equal(1, results.Sum(r => r.Count));
        Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task The_advisory_lock_is_released_even_though_the_ops_pool_does_not_reset_connections()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var sources = DataSources.Create(connection, Options(), includeErrorDetail: false);
        await NewRunner(sources, new MigrationState()).RunOnceAsync(CancellationToken.None);

        await using var other = new NpgsqlConnection(connection);
        await other.OpenAsync();
        await using var probe = new NpgsqlCommand($"SELECT pg_try_advisory_lock({MigrationRunner.AdvisoryLockKey})", other);

        Assert.True((bool)(await probe.ExecuteScalarAsync())!);
        await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({MigrationRunner.AdvisoryLockKey})", other);   // the pooled probe must not leak it
        await unlock.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Background_service_applies_migrations_marks_ready_and_does_not_block_startup()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var sources = DataSources.Create(connection, Options(), includeErrorDetail: false);
        var state = new MigrationState();
        using var runner = NewRunner(sources, state);

        var started = Stopwatch.StartNew();
        await runner.StartAsync(CancellationToken.None);
        var startupTime = started.Elapsed;
        await WaitUntilAsync(() => state.IsReady);
        await runner.StopAsync(CancellationToken.None);

        Assert.True(startupTime < TimeSpan.FromSeconds(1), $"StartAsync took {startupTime}");
        Assert.True(state.IsReady);
        Assert.Equal(0, state.FailedAttempts);
        Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task With_the_database_down_the_service_stays_alive_not_ready_and_keeps_retrying()
    {
        var options = Options(retrySeconds: 60);
        await using var sources = DataSources.Create(UnreachableDatabase, options, includeErrorDetail: false);
        var state = new MigrationState();
        using var runner = NewRunner(sources, state, options);

        await runner.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.FailedAttempts >= 2);

        Assert.False(state.IsReady);
        Assert.False(state.GaveUp);
        Assert.NotNull(state.LastError);
        Assert.False(runner.ExecuteTask!.IsCompleted, "the runner must keep retrying, not exit or crash");
        Assert.DoesNotContain("Password", state.LastError);
        await runner.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task When_the_retry_budget_runs_out_it_gives_up_quietly_with_readiness_red()
    {
        var options = Options(retrySeconds: 1);
        await using var sources = DataSources.Create(UnreachableDatabase, options, includeErrorDetail: false);
        var state = new MigrationState();
        using var runner = NewRunner(sources, state, options);

        await runner.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.GaveUp);
        await runner.ExecuteTask!;   // completes without throwing: no crash loop

        Assert.False(state.IsReady);
        Assert.True(state.FailedAttempts >= 1);
        await runner.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_service_recovers_once_the_database_appears()
    {
        var name = PostgresFixture.NewDatabaseName();
        var options = Options(retrySeconds: 60);
        await using var sources = DataSources.Create(postgres.ConnectionStringFor(name), options, includeErrorDetail: false);
        var state = new MigrationState();
        using var runner = NewRunner(sources, state, options);

        await runner.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.FailedAttempts >= 1);
        Assert.False(state.IsReady);

        await postgres.CreateDatabaseNamedAsync(name);
        await WaitUntilAsync(() => state.IsReady);

        Assert.Null(state.LastError);
        Assert.Equal(1, await CountAsync(postgres.ConnectionStringFor(name), "SELECT count(*) FROM schema_migrations"));
        await runner.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Full_host_becomes_ready_through_IReadinessState_and_has_the_schema()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Postgres", connection));
        using var client = factory.CreateClient();

        var readiness = factory.Services.GetRequiredService<IReadinessState>();
        await WaitUntilAsync(() => readiness.IsReady);

        Assert.Same(factory.Services.GetRequiredService<MigrationState>(), readiness);
        Assert.Equal(4, await CountAsync(connection, "SELECT count(*) FROM pg_tables WHERE schemaname='public'"));
    }

    [Fact]
    public async Task Full_host_with_the_database_down_still_starts_and_answers_liveness()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Postgres", UnreachableDatabase));
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");

        Assert.True(live.IsSuccessStatusCode);
        Assert.False(factory.Services.GetRequiredService<IReadinessState>().IsReady);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met within {seconds}s.");
            }

            await Task.Delay(50);
        }
    }

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
