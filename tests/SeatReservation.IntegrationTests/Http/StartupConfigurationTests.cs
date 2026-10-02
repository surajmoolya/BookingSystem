using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SeatReservation.Api.Options;
using SeatReservation.Application;
using SeatReservation.Application.Options;
using SeatReservation.Infrastructure;
using SeatReservation.Infrastructure.Persistence;

namespace SeatReservation.IntegrationTests.Http;

public class StartupConfigurationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task App_starts_with_the_default_configuration()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Defaults_from_appsettings_are_bound_in_every_layer()
    {
        var services = factory.Services;

        Assert.Equal(40, services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MaxPoolSize);
        Assert.Equal(4, services.GetRequiredService<IOptions<ReservationOptions>>().Value.DefaultPerUserLimit);
        Assert.False(services.GetRequiredService<IOptions<ShowOptions>>().Value.RequireAuth);
        Assert.Equal("seat-reservation", services.GetRequiredService<IOptions<AuthOptions>>().Value.Issuer);
        Assert.Equal(50_000, services.GetRequiredService<IOptions<AdmissionOptions>>().Value.QueueLimit);
        Assert.Equal(200, services.GetRequiredService<IOptions<MetricsOptions>>().Value.MaxShowsInGauges);
    }

    [Fact]
    public void Environment_style_override_reaches_the_options()
    {
        using var overridden = factory.WithWebHostBuilder(b => b.UseSetting("Database:MaxPoolSize", "12"));

        Assert.Equal(12, overridden.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MaxPoolSize);
    }

    [Theory]
    [InlineData("Database:MaxPoolSize", "0", "Database:MaxPoolSize")]                     // Infrastructure
    [InlineData("Reservations:DefaultPerUserLimit", "500", "Reservations:DefaultPerUserLimit")] // Application
    [InlineData("Shows:MaxSeats", "-5", "Shows:MaxSeats")]                                  // Application
    [InlineData("Auth:SigningKey", "too-short", "Auth:SigningKey")]                         // Api
    [InlineData("Admission:PermitLimit", "0", "Admission:PermitLimit")]                     // Api
    [InlineData("Metrics:MaxShowsInGauges", "0", "Metrics:MaxShowsInGauges")]               // Api
    public async Task Invalid_configuration_fails_startup_naming_the_key(string key, string value, string expectedInMessage)
    {
        // A plain Host rather than WebApplicationFactory: when startup throws, the factory's deferred host races
        // with the entry point's disposal and can surface an ObjectDisposedException instead of the real error.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x",   // as appsettings.json would supply
            [key] = value,
        });
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddValidatedOptions(builder.Configuration);
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(expectedInMessage, ex.Message);
    }
}
