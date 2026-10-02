using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Application.Abstractions;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>Guards the test infrastructure itself, so later concurrency tests can be trusted to be real races.</summary>
[Collection(PostgresCollection.Name)]
public class InfrastructureTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task ConcurrentAsync_runs_every_action_at_the_same_time_not_one_after_another()
    {
        const int count = 40;
        var inFlight = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Each action returns only once all 40 have started; a sequential runner would hit the timeout.
        var results = await TestConcurrency.ConcurrentAsync(count, async i =>
        {
            if (Interlocked.Increment(ref inFlight) == count)
            {
                allArrived.SetResult();
            }

            await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return i * 2;
        });

        Assert.Equal(Enumerable.Range(0, count).Select(i => i * 2), results);
    }

    [Fact]
    public async Task ConcurrentAsync_surfaces_a_failing_action()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestConcurrency.ConcurrentAsync(5, i => i == 3 ? throw new InvalidOperationException() : Task.CompletedTask));
    }

    [Fact]
    public void Test_tokens_are_three_part_HS256_jwts_with_the_expected_claims()
    {
        var token = TestTokens.Create("alice");
        var parts = token.Split('.');

        Assert.Equal(3, parts.Length);
        using var header = JsonDocument.Parse(Decode(parts[0]));
        using var payload = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal("HS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("alice", payload.RootElement.GetProperty("sub").GetString());
        Assert.Equal(TestTokens.Issuer, payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal(TestTokens.Audience, payload.RootElement.GetProperty("aud").GetString());
        Assert.True(payload.RootElement.GetProperty("exp").GetInt64() > payload.RootElement.GetProperty("iat").GetInt64());
        Assert.NotEqual(token, TestTokens.Create("alice"));   // fresh jti
    }

    [Fact]
    public void Test_token_signature_depends_on_the_key_and_can_be_forged_wrong_on_purpose()
    {
        var now = DateTimeOffset.UtcNow;

        var good = TestTokens.Create("alice", now: now);
        var otherKey = TestTokens.Create("alice", signingKey: "a-completely-different-signing-key-0123456789", now: now);

        Assert.NotEqual(good.Split('.')[2], otherKey.Split('.')[2]);
    }

    [Fact]
    public void AsUser_sets_a_bearer_header()
    {
        using var client = new HttpClient();

        client.AsUser("alice");

        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Contains(".", client.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public async Task ApiFixture_hands_out_a_host_that_is_already_ready_and_serving()
    {
        Assert.True(api.Factory.Services.GetRequiredService<IReadinessState>().IsReady);

        using var response = await api.Client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void ApiFactory_wires_the_signing_key_and_connection_string_into_configuration()
    {
        var config = api.Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();

        Assert.Equal(ApiFactory.SigningKey, config["Auth:SigningKey"]);
        Assert.Equal(api.Factory.ConnectionString, config["ConnectionStrings:Postgres"]);
        Assert.True(Encoding.UTF8.GetByteCount(ApiFactory.SigningKey) >= 32);
    }

    private static string Decode(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
