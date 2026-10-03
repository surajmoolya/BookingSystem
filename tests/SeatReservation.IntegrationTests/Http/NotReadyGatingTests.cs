using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Infrastructure.Migrations;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>
/// Until migrations complete (<c>IReadinessState.IsReady</c>), every DB-bound endpoint answers 503 <c>not_ready</c> with
/// <c>Retry-After</c>, never a 500 or a half-done write (T-6.4). Readiness is delayed by removing the migration runner and
/// flipping <see cref="MigrationState"/> by hand once the schema (applied up front) should count as ready.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NotReadyGatingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Db_bound_endpoints_are_503_not_ready_until_readiness_flips_then_serve_normally()
    {
        await using var database = await MigratedDatabase.CreateAsync(postgres);
        await using var factory = new ApiFactory(
            database.ConnectionString,
            configureServices: services => services.Remove(services.Single(d => d.ImplementationType == typeof(MigrationRunner))));
        using var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<MigrationState>();
        var someId = Guid.NewGuid();

        await AssertNotReadyAsync(await client.PostAsJsonAsync("/shows", new { name = "early", seats = new[] { "A1" }, price_paise = 100 }));
        await AssertNotReadyAsync(await client.GetAsync($"/shows/{someId}"));
        var reserve = await ReserveCalls.ReserveAsync(client, someId, "alice", ["A1"], "early-1");
        Assert.True(reserve.Is(HttpStatusCode.ServiceUnavailable, "not_ready"), reserve.ToString());
        var cancel = await ReserveCalls.CancelAsync(client, someId, "alice");
        Assert.True(cancel.Is(HttpStatusCode.ServiceUnavailable, "not_ready"), cancel.ToString());
        using (var owner = factory.CreateClient().AsUser("alice"))
        {
            await AssertNotReadyAsync(await owner.GetAsync($"/reservations/{someId}"));
        }

        // Probes and token issuance need no schema: liveness and auth keep answering, readiness says why traffic is held.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/token", new { username = "alice" })).StatusCode);
        Assert.Equal(0, await database.CountAsync("SELECT count(*) FROM shows"));

        state.MarkReady();

        var showId = await ReserveCalls.CreateShowAsync(client, ["A1"]);
        var created = await ReserveCalls.ReserveAsync(client, showId, "alice", ["A1"], "early-1");
        Assert.True(created.Is(HttpStatusCode.Created), created.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    private static async Task AssertNotReadyAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("not_ready", body.RootElement.GetProperty("code").GetString());
        }
    }
}
