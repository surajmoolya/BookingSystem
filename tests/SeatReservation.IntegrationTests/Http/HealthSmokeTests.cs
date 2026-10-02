using System.Net;
using System.Text.Json;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>Liveness never depends on the database, so this runs against a host whose database is unreachable.</summary>
public sealed class HealthSmokeTests : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(ApiFactory.UnreachableConnectionString);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Live_returns_200_with_live_status()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("live", body.RootElement.GetProperty("status").GetString());
    }
}
