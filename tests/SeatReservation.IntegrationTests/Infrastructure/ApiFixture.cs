namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>
/// One ready API host (own fresh database) per test class. Use as
/// <c>[Collection(PostgresCollection.Name)] public class XTests(ApiFixture api) : IClassFixture&lt;ApiFixture&gt;</c>.
/// Tests isolate themselves by creating a new show each, so they can share the host.
/// </summary>
public sealed class ApiFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public ApiFactory Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = await ApiFactory.StartAsync(postgres);
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }
}
