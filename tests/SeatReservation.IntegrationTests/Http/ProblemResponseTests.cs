using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Api.Errors;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>RFC 7807 problem bodies and the exception → status mapping, driven through a probe controller that throws on demand.</summary>
public sealed class ProblemResponseTests : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(
        ApiFactory.UnreachableConnectionString,
        configureServices: services => services.AddControllers().AddApplicationPart(typeof(ErrorProbeController).Assembly));

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

    [Fact]
    public async Task NotReadyException_is_503_not_ready_with_Retry_After()
    {
        using var response = await _client.GetAsync("/_probe/not-ready");

        var problem = await ReadProblemAsync(response, HttpStatusCode.ServiceUnavailable, "not_ready");
        Assert.Equal("1", response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task DependencyUnavailableException_is_503_dependency_unavailable_with_Retry_After_and_hides_the_cause()
    {
        using var response = await _client.GetAsync("/_probe/dependency");

        var problem = await ReadProblemAsync(response, HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
        Assert.NotNull(response.Headers.RetryAfter);
        AssertNoLeak(problem);
    }

    [Theory]
    [InlineData("/_probe/boom")]
    [InlineData("/_probe/invariant")]
    public async Task Unknown_exceptions_are_500_internal_error_without_leaking_details(string path)
    {
        using var response = await _client.GetAsync(path);

        var problem = await ReadProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Null(response.Headers.RetryAfter);
        AssertNoLeak(problem);
    }

    [Fact]
    public async Task A_request_body_over_the_limit_is_413_payload_too_large_not_a_500()
    {
        using var response = await _client.GetAsync("/_probe/too-large");

        var problem = await ReadProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        AssertNoLeak(problem);
    }

    [Fact]
    public async Task Other_bad_http_requests_keep_their_status_as_bad_request()
    {
        using var response = await _client.GetAsync("/_probe/malformed");

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest, "bad_request");
        AssertNoLeak(problem);
    }

    [Fact]
    public async Task Every_problem_carries_a_correlation_id()
    {
        using var response = await _client.GetAsync("/_probe/boom");

        var problem = await ReadProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlation_id").GetString()));
    }

    [Fact]
    public async Task A_correlation_id_set_by_middleware_survives_the_exception_handler_in_both_body_and_header()
    {
        // CorrelationIdMiddleware (T-5.9) runs before UseExceptionHandler, which clears the response headers; the id must still arrive.
        await using var factory = new ApiFactory(
            ApiFactory.UnreachableConnectionString,
            configureServices: services =>
            {
                services.AddControllers().AddApplicationPart(typeof(ErrorProbeController).Assembly);
                services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, HeaderStartupFilter>();
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/_probe/boom");

        var problem = await ReadProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal("corr-from-middleware", problem.GetProperty("correlation_id").GetString());
        Assert.Equal("corr-from-middleware", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Binding_errors_are_400_validation_with_snake_case_field_names()
    {
        using var response = await Post("""{ "seats": [], "price_paise": -5 }""");

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _), "missing 'name' error");
        Assert.True(errors.TryGetProperty("seats", out _), "missing 'seats' error");
        Assert.True(errors.TryGetProperty("price_paise", out _), "missing 'price_paise' error");
        Assert.False(errors.TryGetProperty("PricePaise", out _));
        Assert.Equal(JsonValueKind.Array, errors.GetProperty("name").ValueKind);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("""{ "name": "x", "seats": "A1" }""")]
    public async Task Malformed_or_missing_bodies_are_400_validation_keyed_by_body_or_field_without_echoing_parser_internals(string body)
    {
        using var response = await Post(body);

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").EnumerateObject().Any());
        Assert.DoesNotContain("System.Text.Json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_valid_body_passes_binding_and_unknown_members_such_as_user_id_are_ignored()
    {
        using var response = await Post("""{ "name": "n", "seats": ["A1","A2"], "price_paise": 100, "user_id": "mallory" }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("seat_count").GetInt32());
    }

    [Fact]
    public async Task Success_responses_use_snake_case_properties_and_enum_values()
    {
        using var response = await _client.GetAsync("/_probe/ok");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(25000, body.RootElement.GetProperty("price_paise").GetInt64());
        Assert.Equal("available", body.RootElement.GetProperty("seat_status").GetString());
        Assert.Equal(4, body.RootElement.GetProperty("nested").GetProperty("per_user_limit").GetInt32());
    }

    // ---- ModelStateErrors key normalisation (pure function) ----

    [Theory]
    [InlineData("", "body")]
    [InlineData("$", "body")]
    [InlineData("$.pricePaise", "price_paise")]
    [InlineData("PricePaise", "price_paise")]
    [InlineData("$.seats[0]", "seats[0]")]
    [InlineData("Seats", "seats")]
    [InlineData("idempotency_key", "idempotency_key")]
    [InlineData("$.show.PerUserLimit", "show.per_user_limit")]
    public void ModelState_keys_are_normalised_to_snake_case(string raw, string expected)
    {
        Assert.Equal(expected, SeatReservation.Api.Errors.ModelStateErrors.NormalizeKey(raw));
    }

    private Task<HttpResponseMessage> Post(string json) =>
        _client.PostAsync("/_probe/bind", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        return problem;
    }

    private static void AssertNoLeak(JsonElement problem)
    {
        var text = problem.GetRawText();
        Assert.DoesNotContain(ErrorProbeController.SecretDetail, text);
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("   at ", text);
    }

    private sealed class HeaderStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                CorrelationIds.Set(context, "corr-from-middleware");
                await nextMiddleware();
            });
            next(app);
        };
    }
}
