using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>Credentials never reach the logs (T-5.11, lld §10, D-052): not the issued JWT, not the Authorization header.</summary>
[Collection(PostgresCollection.Name)]
public class LogRedactionTests(LoggingFixture api) : IClassFixture<LoggingFixture>
{
    [Fact]
    public async Task The_issued_token_the_authorization_header_and_the_raw_idempotency_key_never_appear_in_logs()
    {
        var user = $"carol-{Guid.NewGuid():N}"[..14];
        const string rawKey = "client-secret-idempotency-key-42";

        // Issue a token through the API (not the test helper), so the exact string the server handed out is known.
        using var tokenResponse = await api.Client.PostAsJsonAsync("/auth/token", new { username = user });
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        var showId = await CreateShowAsync(api.Client, Labels(2));
        using var client = api.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var reserve = await client.PostAsJsonAsync($"/shows/{showId}/reserve", new { seats = new[] { "S1" }, idempotency_key = rawKey }))
        {
            Assert.Equal(HttpStatusCode.Created, reserve.StatusCode);
        }

        using (var replay = await client.PostAsJsonAsync($"/shows/{showId}/reserve", new { seats = new[] { "S1" }, idempotency_key = rawKey }))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        // A tampered token is rejected; the rejection must not echo it either.
        var tampered = token[..^4] + (token.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");
        using (var bad = new HttpRequestMessage(HttpMethod.Get, "/reservations/" + Guid.NewGuid()))
        {
            bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tampered);
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.SendAsync(bad)).StatusCode);
        }

        await WaitForRequestLineAsync(user);
        var logs = api.Sink.RenderAll();

        Assert.Contains(user, logs, StringComparison.Ordinal);   // the capture did see this flow
        Assert.DoesNotContain(token, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(tampered, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Split('.')[2], logs, StringComparison.Ordinal);   // not even the signature part
        Assert.DoesNotContain("Authorization", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer ey", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(rawKey, logs, StringComparison.Ordinal);
    }

    private async Task WaitForRequestLineAsync(string user)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (api.Sink.RequestEvents().Count(e => CapturingSink.Scalar(e, "UserId") == user) < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "the reserve request lines never arrived");
            await Task.Delay(20);
        }

        await Task.Delay(100);   // the 401's completion line is written just after its response
    }
}
