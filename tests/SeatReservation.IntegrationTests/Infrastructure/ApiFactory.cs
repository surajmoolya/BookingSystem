using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Application.Abstractions;
using SeatReservation.Infrastructure.Migrations;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>
/// The real application in an in-memory <c>TestServer</c>, pointed at a given database and with a known signing key.
/// Use <see cref="StartAsync"/> for a host backed by its own fresh database that is ready to serve; use the constructor with
/// <see cref="UnreachableConnectionString"/> for tests that must not need a database at all (liveness, configuration).
/// </summary>
public sealed class ApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    /// <summary>Refuses connections immediately; the migration runner keeps retrying in the background.</summary>
    public const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x";

    /// <summary>Signing key the factory configures; tests mint matching tokens with <see cref="TestTokens"/>.</summary>
    public const string SigningKey = "integration-test-signing-key-0123456789abcdef";

    public string ConnectionString { get; } = connectionString;

    /// <summary>Creates a fresh database on the container, starts the host against it and waits until migrations are done.</summary>
    public static async Task<ApiFactory> StartAsync(PostgresFixture postgres, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), settings);
        try
        {
            await factory.WaitUntilReadyAsync();
            return factory;
        }
        catch
        {
            await factory.DisposeAsync();
            throw;
        }
    }

    /// <summary>Starts the host if needed, then waits for <see cref="IReadinessState.IsReady"/>.</summary>
    public async Task WaitUntilReadyAsync(TimeSpan? timeout = null)
    {
        _ = Server;   // forces the host (and the background migration runner) to start
        var readiness = Services.GetRequiredService<IReadinessState>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (!readiness.IsReady)
        {
            if (DateTime.UtcNow > deadline)
            {
                var state = Services.GetRequiredService<MigrationState>();
                throw new TimeoutException($"The API did not become ready. Failed attempts: {state.FailedAttempts}; last error: {state.LastError ?? "none"}.");
            }

            await Task.Delay(25);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
        builder.UseSetting("Auth:SigningKey", SigningKey);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
    }
}
