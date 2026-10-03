using System.Net;
using System.Text.Json;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Http;

/// <summary><c>X-Correlation-ID</c> handling (T-5.9, lld §10).</summary>
[Collection(PostgresCollection.Name)]
public class CorrelationIdTests(LoggingFixture api) : IClassFixture<LoggingFixture>
{
    private const string Header = "X-Correlation-ID";

    private async Task<HttpResponseMessage> GetAsync(string path, string? correlationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (correlationId is not null)
        {
            request.Headers.TryAddWithoutValidation(Header, correlationId);
        }

        return await api.Client.SendAsync(request);
    }

    [Fact]
    public async Task A_valid_inbound_id_is_echoed()
    {
        using var response = await GetAsync("/health/live", "abc.DEF_123-x");

        Assert.Equal("abc.DEF_123-x", response.Headers.GetValues(Header).Single());
    }

    [Fact]
    public async Task A_missing_id_is_generated_and_differs_per_request()
    {
        using var first = await GetAsync("/health/live", null);
        using var second = await GetAsync("/health/live", null);

        var a = first.Headers.GetValues(Header).Single();
        var b = second.Headers.GetValues(Header).Single();
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("quote\"d")]
    [InlineData("")]
    public async Task An_unsafe_inbound_id_is_replaced(string inbound)
    {
        using var response = await GetAsync("/health/live", inbound);

        var echoed = response.Headers.GetValues(Header).Single();
        Assert.NotEqual(inbound, echoed);
        Assert.Matches("^[0-9a-f]{32}$", echoed);
    }

    [Fact]
    public async Task A_65_character_id_is_replaced_but_64_is_kept()
    {
        var ok = new string('a', 64);
        using var kept = await GetAsync("/health/live", ok);
        using var replaced = await GetAsync("/health/live", ok + "a");

        Assert.Equal(ok, kept.Headers.GetValues(Header).Single());
        Assert.NotEqual(ok + "a", replaced.Headers.GetValues(Header).Single());
    }

    [Fact]
    public async Task The_problem_body_and_the_log_line_quote_the_same_id()
    {
        var id = $"corr-{Guid.NewGuid():N}";
        using var response = await GetAsync($"/shows/{Guid.NewGuid()}", id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(id, response.Headers.GetValues(Header).Single());
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(id, problem.GetProperty("correlation_id").GetString());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!api.Sink.RequestEvents().Any(e => CapturingSink.Scalar(e, "CorrelationId") == id))
        {
            Assert.True(DateTime.UtcNow < deadline, "no log line carried the correlation id");
            await Task.Delay(20);
        }

        var line = Assert.Single(api.Sink.RequestEvents(), e => CapturingSink.Scalar(e, "CorrelationId") == id);
        Assert.Equal("404", CapturingSink.Scalar(line, "StatusCode"));
    }
}
