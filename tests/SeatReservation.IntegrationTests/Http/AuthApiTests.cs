using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>
/// <c>POST /auth/token</c> and JWT bearer validation over HTTP (lld §5.1, D-020). Neither needs the database, so the host
/// points at an unreachable one. Bad tokens are forged with <see cref="TestTokens"/>.
/// </summary>
public sealed class AuthApiTests : IAsyncLifetime
{
    private const string WhoAmI = "/_probe/auth/whoami";

    private readonly ApiFactory _factory = new(
        ApiFactory.UnreachableConnectionString,
        configureServices: services => services.AddControllers().AddApplicationPart(typeof(AuthProbeController).Assembly));

    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // ---- issuance ----

    [Fact]
    public async Task Valid_username_gets_200_with_the_contract_body()
    {
        using var response = await _client.PostAsJsonAsync("/auth/token", new { username = "alice" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("access_token").GetString()));
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(43_200, body.GetProperty("expires_in").GetInt64());
        Assert.Equal("alice", body.GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task Issued_token_is_HS256_with_sub_iss_aud_iat_exp_and_jti()
    {
        var token = await IssueAsync("alice");

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var header = Decode(parts[0]);
        var payload = Decode(parts[1]);
        Assert.Equal("HS256", header.GetProperty("alg").GetString());
        Assert.Equal("alice", payload.GetProperty("sub").GetString());
        Assert.Equal(TestTokens.Issuer, payload.GetProperty("iss").GetString());
        Assert.Equal(TestTokens.Audience, payload.GetProperty("aud").GetString());
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("jti").GetString()));
        var iat = payload.GetProperty("iat").GetInt64();
        Assert.Equal(12 * 3600, payload.GetProperty("exp").GetInt64() - iat);
        Assert.InRange(iat, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Each_issued_token_has_its_own_jti()
    {
        var first = Decode((await IssueAsync("alice")).Split('.')[1]).GetProperty("jti").GetString();
        var second = Decode((await IssueAsync("alice")).Split('.')[1]).GetProperty("jti").GetString();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Issued_token_is_accepted_and_the_user_id_is_its_sub()
    {
        var token = await IssueAsync("Alice.B-1");

        using var response = await SendWithTokenAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Alice.B-1", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user_id").GetString());
    }

    [Theory]
    [InlineData("""{"username":"bad name"}""", "username")]
    [InlineData("""{"username":"alice@example.com"}""", "username")]
    [InlineData("""{"username":""}""", "username")]
    [InlineData("""{"username":null}""", "username")]
    [InlineData("""{}""", "username")]
    [InlineData("""{"username":"01234567890123456789012345678901234567890123456789012345678901234"}""", "username")]
    [InlineData("""{"username":42}""", "username")]
    [InlineData("""not json""", "body")]
    [InlineData("", "body")]
    public async Task Invalid_request_is_400_validation_keyed_by_field(string json, string field)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync("/auth/token", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    // ---- validation ----

    [Fact]
    public async Task Hand_minted_valid_token_is_accepted()
    {
        using var response = await SendWithTokenAsync(TestTokens.Create("bob"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task No_token_is_401_problem_with_a_Bearer_challenge()
    {
        using var response = await _client.GetAsync(WhoAmI);

        await AssertUnauthorizedAsync(response);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task Bad_signature_is_401()
    {
        var token = TestTokens.Create("alice", signingKey: "some-other-signing-key-0123456789abcdef!");

        await AssertUnauthorizedAsync(await SendWithTokenAsync(token));
    }

    [Fact]
    public async Task Tampered_payload_is_401()
    {
        var parts = TestTokens.Create("alice").Split('.');
        var forged = Encode(Decode(parts[1]).ToString().Replace("\"alice\"", "\"mallory\""));

        await AssertUnauthorizedAsync(await SendWithTokenAsync($"{parts[0]}.{forged}.{parts[2]}"));
    }

    [Fact]
    public async Task Expired_token_is_401()
    {
        var token = TestTokens.Create("alice", now: DateTimeOffset.UtcNow.AddHours(-2), lifetime: TimeSpan.FromHours(1));

        await AssertUnauthorizedAsync(await SendWithTokenAsync(token));
    }

    [Fact]
    public async Task Token_expired_beyond_the_30s_clock_skew_is_401()
    {
        // Under the 5-minute default skew this would still be accepted.
        var token = TestTokens.Create("alice", now: DateTimeOffset.UtcNow.AddMinutes(-3), lifetime: TimeSpan.FromMinutes(1));

        await AssertUnauthorizedAsync(await SendWithTokenAsync(token));
    }

    [Fact]
    public async Task Wrong_audience_is_401()
    {
        await AssertUnauthorizedAsync(await SendWithTokenAsync(TestTokens.Create("alice", audience: "some-other-api")));
    }

    [Fact]
    public async Task Wrong_issuer_is_401()
    {
        await AssertUnauthorizedAsync(await SendWithTokenAsync(TestTokens.Create("alice", issuer: "someone-else")));
    }

    [Fact]
    public async Task Unsigned_alg_none_token_is_401()
    {
        var payload = Decode(TestTokens.Create("alice").Split('.')[1]).ToString();
        var token = $"{Encode("""{"alg":"none","typ":"JWT"}""")}.{Encode(payload)}.";

        await AssertUnauthorizedAsync(await SendWithTokenAsync(token));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    public async Task Malformed_token_is_401(string token)
    {
        await AssertUnauthorizedAsync(await SendWithTokenAsync(token));
    }

    [Fact]
    public async Task Valid_token_under_a_non_Bearer_scheme_is_401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, WhoAmI);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", TestTokens.Create("alice"));

        await AssertUnauthorizedAsync(await _client.SendAsync(request));
    }

    // ---- helpers ----

    private async Task<string> IssueAsync(string username)
    {
        using var response = await _client.PostAsJsonAsync("/auth/token", new { username });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> SendWithTokenAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, WhoAmI);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("unauthorized", problem.GetProperty("code").GetString());
            Assert.Equal(401, problem.GetProperty("status").GetInt32());
            Assert.True(problem.TryGetProperty("correlation_id", out _));
        }
    }

    private static JsonElement Decode(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(s));
    }

    private static string Encode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
