using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SeatReservation.Api.Options;
using SeatReservation.Application.Options;
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
    public void Invalid_configuration_fails_startup_naming_the_key(string key, string value, string expectedInMessage)
    {
        using var broken = factory.WithWebHostBuilder(b => b.UseSetting(key, value));

        var ex = Assert.Throws<OptionsValidationException>(() => broken.CreateClient());

        Assert.Contains(expectedInMessage, ex.Message);
    }
}
