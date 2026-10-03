using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>A ready host whose Serilog events are also captured in memory.</summary>
public sealed class LoggingFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public CapturingSink Sink { get; } = new();

    public ApiFactory Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = new ApiFactory(await postgres.CreateDatabaseAsync(), configureServices: s => s.AddSingleton<ILogEventSink>(Sink));
        await Factory.WaitUntilReadyAsync();
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>One structured completion line per request (T-5.8, lld §10, D-088).</summary>
[Collection(PostgresCollection.Name)]
public class RequestLoggingTests(LoggingFixture api) : IClassFixture<LoggingFixture>
{
    private readonly string _alice = $"alice-{Guid.NewGuid():N}"[..14];
    private readonly string _bob = $"bob-{Guid.NewGuid():N}"[..12];

    [Fact]
    public async Task Each_request_produces_exactly_one_completion_event_with_route_status_and_outcome()
    {
        var showId = await CreateShowAsync(api.Client, Labels(2));
        var created = await ReserveAsync(api.Client, showId, _alice, ["S1"], "k1");
        var taken = await ReserveAsync(api.Client, showId, _bob, ["S1"], "k1");
        Assert.True(created.Is(HttpStatusCode.Created) && taken.Is(HttpStatusCode.Conflict, "seat_taken"));

        var createdLine = await SingleRequestEventAsync(e => CapturingSink.Scalar(e, "UserId") == _alice);
        Assert.Equal(LogEventLevel.Information, createdLine.Level);
        Assert.Equal("shows/{id}/reserve", CapturingSink.Scalar(createdLine, "Endpoint"));
        Assert.Equal("POST", CapturingSink.Scalar(createdLine, "RequestMethod"));
        Assert.Equal("201", CapturingSink.Scalar(createdLine, "StatusCode"));
        Assert.Equal("created", CapturingSink.Scalar(createdLine, "Outcome"));
        Assert.Equal(created.ReservationId.ToString(), CapturingSink.Scalar(createdLine, "ReservationId"));
        Assert.Equal(showId.ToString(), CapturingSink.Scalar(createdLine, "ShowId"));
        Assert.Null(CapturingSink.Scalar(createdLine, "Reason"));
        Assert.NotNull(CapturingSink.Scalar(createdLine, "RequestId"));

        var declinedLine = await SingleRequestEventAsync(e => CapturingSink.Scalar(e, "UserId") == _bob);
        Assert.Equal("409", CapturingSink.Scalar(declinedLine, "StatusCode"));
        Assert.Equal("seat_taken", CapturingSink.Scalar(declinedLine, "Outcome"));
        Assert.Equal("seat_taken", CapturingSink.Scalar(declinedLine, "Reason"));
        Assert.Null(CapturingSink.Scalar(declinedLine, "ReservationId"));
    }

    [Fact]
    public async Task Cancel_lines_carry_the_reservation_and_outcome()
    {
        var showId = await CreateShowAsync(api.Client, Labels(1));
        var created = await ReserveAsync(api.Client, showId, _alice, ["S1"], "k1");
        Assert.True((await CancelAsync(api.Client, created.ReservationId, _bob)).Is(HttpStatusCode.Forbidden));

        var line = await SingleRequestEventAsync(e => CapturingSink.Scalar(e, "UserId") == _bob);
        Assert.Equal("reservations/{id}/cancel", CapturingSink.Scalar(line, "Endpoint"));
        Assert.Equal("not_owner", CapturingSink.Scalar(line, "Outcome"));
        Assert.Equal(created.ReservationId.ToString(), CapturingSink.Scalar(line, "ReservationId"));
    }

    [Fact]
    public async Task Health_probes_log_at_debug_and_a_request_writes_no_framework_information_lines()
    {
        var before = api.Sink.Events.Count;
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/health/live")).StatusCode);

        await Eventually(() => api.Sink.Events.Skip(before).Any(e => CapturingSink.Scalar(e, "Endpoint") == "/health/live"));
        var events = api.Sink.Events.Skip(before).ToList();
        Assert.Equal(LogEventLevel.Debug, Assert.Single(events, e => CapturingSink.Scalar(e, "EventName") == "http.request").Level);
        Assert.DoesNotContain(events, e => e.Level >= LogEventLevel.Information);   // no Microsoft.AspNetCore "Request starting/finished" pair
    }

    [Fact]
    public async Task Lines_render_as_one_compact_json_object_each_with_the_app_enrichers()
    {
        await CreateShowAsync(api.Client, Labels(1));
        await Eventually(() => api.Sink.RequestEvents().Any(e => CapturingSink.Scalar(e, "Endpoint") == "shows"));

        foreach (var line in api.Sink.RenderAll().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line);
            Assert.True(json.RootElement.TryGetProperty("@t", out _));
            Assert.True(json.RootElement.TryGetProperty("@m", out _));   // rendered message
            Assert.Equal("seat-reservation", json.RootElement.GetProperty("App").GetString());
        }
    }

    private async Task<LogEvent> SingleRequestEventAsync(Func<LogEvent, bool> match)
    {
        await Eventually(() => api.Sink.RequestEvents().Any(match));
        return Assert.Single(api.Sink.RequestEvents(), e => match(e));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "log event not seen within 5 s");
            await Task.Delay(20);
        }
    }
}
