using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using SeatReservation.Api.Hosting;
using SeatReservation.Api.Options;
using SeatReservation.Application;
using SeatReservation.Application.Options;
using SeatReservation.Infrastructure;
using SeatReservation.Infrastructure.Persistence;
using SeatReservation.IntegrationTests.Infrastructure;
using Serilog.Core;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>Configuration binding needs no database, so the host here points at an unreachable one and never waits for readiness.</summary>
public sealed class StartupConfigurationTests : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(ApiFactory.UnreachableConnectionString);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task App_starts_with_the_default_configuration()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Runtime_tuning_is_applied_and_logged_at_startup()
    {
        var sink = new CapturingSink();
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString, configureServices: s => s.AddSingleton<ILogEventSink>(sink));
        using var client = factory.CreateClient();   // starts the host

        ThreadPool.GetMinThreads(out var worker, out var io);
        Assert.True(worker >= RuntimeTuning.MinThreads && io >= RuntimeTuning.MinThreads, $"min threads {worker}/{io}");
        var kestrel = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        Assert.Equal(64 * 1024, kestrel.Limits.MaxRequestBodySize);
        Assert.False(kestrel.AddServerHeader);

        var line = Assert.Single(sink.Events, e => e.MessageTemplate.Text.StartsWith("runtime.tuned", StringComparison.Ordinal));
        Assert.Equal("65536", CapturingSink.Scalar(line, "MaxRequestBodyBytes"));
        Assert.Equal("False", CapturingSink.Scalar(line, "ServerHeader"));
        Assert.True(int.Parse(CapturingSink.Scalar(line, "MinWorkerThreads")!) >= RuntimeTuning.MinThreads);
    }

    [Fact]
    public void Defaults_from_appsettings_are_bound_in_every_layer()
    {
        var services = _factory.Services;

        Assert.Equal(40, services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MaxPoolSize);
        Assert.Equal(4, services.GetRequiredService<IOptions<ReservationOptions>>().Value.DefaultPerUserLimit);
        Assert.False(services.GetRequiredService<IOptions<ShowOptions>>().Value.RequireAuth);
        Assert.Equal("seat-reservation", services.GetRequiredService<IOptions<AuthOptions>>().Value.Issuer);
        Assert.Equal(50_000, services.GetRequiredService<IOptions<AdmissionOptions>>().Value.QueueLimit);
        Assert.Equal(200, services.GetRequiredService<IOptions<MetricsOptions>>().Value.MaxShowsInGauges);
    }

    [Fact]
    public async Task Environment_style_override_reaches_the_options()
    {
        await using var overridden = new ApiFactory(ApiFactory.UnreachableConnectionString, new Dictionary<string, string?> { ["Database:MaxPoolSize"] = "12" });

        Assert.Equal(12, overridden.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MaxPoolSize);
    }

    [Fact]
    public async Task Reservation_lock_timeout_follows_Database_LockTimeoutMs()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), _factory.Services.GetRequiredService<IOptions<ReservationOptions>>().Value.LockTimeout);

        await using var overridden = new ApiFactory(ApiFactory.UnreachableConnectionString, new Dictionary<string, string?> { ["Database:LockTimeoutMs"] = "2500" });

        Assert.Equal(TimeSpan.FromMilliseconds(2500), overridden.Services.GetRequiredService<IOptions<ReservationOptions>>().Value.LockTimeout);
    }

    [Theory]
    [InlineData("Database:MaxPoolSize", "0", "Database:MaxPoolSize")]                     // Infrastructure
    [InlineData("Reservations:DefaultPerUserLimit", "500", "Reservations:DefaultPerUserLimit")] // Application
    [InlineData("Shows:MaxSeats", "-5", "Shows:MaxSeats")]                                  // Application
    [InlineData("Auth:SigningKey", "too-short", "Auth:SigningKey")]                         // Api
    [InlineData("Admission:PermitLimit", "0", "Admission:PermitLimit")]                     // Api
    [InlineData("Metrics:MaxShowsInGauges", "0", "Metrics:MaxShowsInGauges")]               // Api
    [InlineData("Shutdown:DrainSeconds", "26", "Shutdown:DrainSeconds")]                    // Api
    public async Task Invalid_configuration_fails_startup_naming_the_key(string key, string value, string expectedInMessage)
    {
        // A plain Host rather than WebApplicationFactory: when startup throws, the _factory's deferred host races
        // with the entry point's disposal and can surface an ObjectDisposedException instead of the real error.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x",   // as appsettings.json would supply
            ["Auth:SigningKey"] = ApiFactory.SigningKey,   // required outside Development; the case under test may override it
            [key] = value,
        });
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddValidatedOptions(builder.Configuration);
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(expectedInMessage, ex.Message);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    public async Task Missing_signing_key_fails_startup_only_outside_Development(string environment, bool shouldFail)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x",
        });
        builder.Services.AddValidatedOptions(builder.Configuration);
        using var host = builder.Build();

        if (shouldFail)
        {
            var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
            Assert.Contains("Auth:SigningKey is required outside Development", ex.Message);
        }
        else
        {
            await host.StartAsync();
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task Committed_development_key_is_refused_outside_Development(string environment, bool shouldFail)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:SigningKey"] = AuthOptions.DevelopmentSigningKey,
        });
        builder.Services.AddValidatedOptions(builder.Configuration);
        using var host = builder.Build();

        if (shouldFail)
        {
            var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
            Assert.Contains("Auth:SigningKey is the public Development key", ex.Message);
        }
        else
        {
            await host.StartAsync();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Development_key_constant_matches_appsettings_Development()
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"));
        using var json = await JsonDocument.ParseAsync(stream);

        Assert.Equal(AuthOptions.DevelopmentSigningKey, json.RootElement.GetProperty("Auth").GetProperty("SigningKey").GetString());
    }

    [Fact]
    public void Startup_failure_is_one_json_line_naming_every_failure()
    {
        var ex = new OptionsValidationException("", typeof(AuthOptions), ["Auth:SigningKey is required outside Development."]);
        using var output = new StringWriter();

        StartupFailure.Write(ex, "Production", output);

        var line = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var json = JsonDocument.Parse(line);
        Assert.Equal("Fatal", json.RootElement.GetProperty("@l").GetString());
        Assert.Equal(StartupFailure.EventName, json.RootElement.GetProperty("EventName").GetString());
        Assert.Equal("AuthOptions", json.RootElement.GetProperty("OptionsType").GetString());
        Assert.Equal("Auth:SigningKey is required outside Development.", json.RootElement.GetProperty("Failures")[0].GetString());
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public void Error_detail_is_included_only_in_Development(string environment, bool expected)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Asking for it in the connection string must not switch it on in production.
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Include Error Detail=true",
        });
        builder.Services.AddInfrastructure(builder.Configuration);
        using var host = builder.Build();

        var pools = host.Services.GetRequiredService<DataSources>();

        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(pools.Main.ConnectionString).IncludeErrorDetail);
        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(pools.Ops.ConnectionString).IncludeErrorDetail);
    }
}
