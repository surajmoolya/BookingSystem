using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Reservations;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>A ready host whose <see cref="IReservationMetrics"/> counts cancels, so tests can check the metric moves once.</summary>
public sealed class CancellationApiFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public CountingMetrics Metrics { get; } = new();

    public ApiFactory Factory { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = new ApiFactory(await postgres.CreateDatabaseAsync(), configureServices: s => s.AddSingleton<IReservationMetrics>(Metrics));
        await Factory.WaitUntilReadyAsync();
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }

    public sealed class CountingMetrics : IReservationMetrics
    {
        private int _cancelled;

        public int Cancelled => Volatile.Read(ref _cancelled);

        public void Confirmed(int seatCount)
        {
        }

        public void Declined(DeclineReason reason)
        {
        }

        void IReservationMetrics.Cancelled() => Interlocked.Increment(ref _cancelled);

        public void ObserveDuration(ReservationResultKind kind, TimeSpan elapsed)
        {
        }
    }
}

/// <summary><c>POST /reservations/{id}/cancel</c> and <c>GET /reservations/{id}</c> (T-4.3, lld §5.5–§5.6, D-022, D-040).</summary>
[Collection(PostgresCollection.Name)]
public class CancellationApiTests(CancellationApiFixture api) : IClassFixture<CancellationApiFixture>
{
    // Keys are per user and the class shares one host, so each test (a new instance) gets its own users.
    private readonly string _alice = $"alice-{Guid.NewGuid():N}"[..14];
    private readonly string _bob = $"bob-{Guid.NewGuid():N}"[..12];

    private sealed record Response(HttpStatusCode Status, string Raw)
    {
        public JsonElement Json => JsonDocument.Parse(Raw).RootElement;

        public string? Code => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    private async Task<Response> SendAsync(HttpMethod method, string path, string? userId)
    {
        using var request = new HttpRequestMessage(method, path);
        if (userId is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(userId));
        }

        using var response = await api.Client.SendAsync(request);
        return new Response(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private Task<Response> CancelAsync(object reservationId, string? userId) =>
        SendAsync(HttpMethod.Post, $"/reservations/{reservationId}/cancel", userId);

    private Task<Response> GetAsync(object reservationId, string? userId) =>
        SendAsync(HttpMethod.Get, $"/reservations/{reservationId}", userId);

    /// <summary>A new show A1–A4 where alice holds A1 and A2. Returns the show, the reservation id and the raw 201 body.</summary>
    private async Task<(Guid ShowId, Guid ReservationId, string CreatedBody)> AliceReservesAsync()
    {
        var showId = await CreateShowAsync(api.Client, ["A1", "A2", "A3", "A4"]);
        var created = await ReserveAsync(api.Client, showId, _alice, ["A1", "A2"], "key-1");
        Assert.Equal(HttpStatusCode.Created, created.Status);
        return (showId, created.ReservationId, created.Body.GetRawText());
    }

    private async Task<JsonElement> ShowAsync(Guid showId) =>
        await api.Client.GetFromJsonAsync<JsonElement>($"/shows/{showId}");

    private static Dictionary<string, string> SeatStatuses(JsonElement show) =>
        show.GetProperty("seats").EnumerateArray().ToDictionary(s => s.GetProperty("label").GetString()!, s => s.GetProperty("status").GetString()!);

    private Task<long> DbAsync(string sql) => ScalarAsync(api.Factory.ConnectionString, sql);

    private static void AssertProblem(Response response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.Status);
        Assert.Equal(code, response.Code);
    }

    // ---- cancel ----

    [Fact]
    public async Task Owner_cancel_returns_the_cancelled_reservation_and_frees_its_seats()
    {
        var (showId, reservationId, _) = await AliceReservesAsync();
        var before = api.Metrics.Cancelled;

        var cancelled = await CancelAsync(reservationId, _alice);

        Assert.Equal(HttpStatusCode.OK, cancelled.Status);
        var body = cancelled.Json;
        Assert.Equal(reservationId, body.GetProperty("reservation_id").GetGuid());
        Assert.Equal("cancelled", body.GetProperty("status").GetString());
        Assert.Equal(_alice, body.GetProperty("user_id").GetString());
        Assert.Equal(["A1", "A2"], body.GetProperty("seats").EnumerateArray().Select(s => s.GetString()));
        Assert.True(body.GetProperty("cancelled_at").GetDateTime() >= body.GetProperty("created_at").GetDateTime());
        Assert.Equal(before + 1, api.Metrics.Cancelled);

        var show = await ShowAsync(showId);
        var counts = show.GetProperty("counts");
        Assert.Equal(4, counts.GetProperty("available").GetInt32());
        Assert.Equal(0, counts.GetProperty("confirmed").GetInt32());
        Assert.All(SeatStatuses(show).Values, s => Assert.Equal("available", s));
        Assert.Equal(0, await DbAsync($"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND (user_id IS NOT NULL OR reservation_id IS NOT NULL)"));
        Assert.Equal(1, await DbAsync($"SELECT count(*) FROM reservations WHERE id = '{reservationId}' AND status = 'cancelled' AND cancelled_at IS NOT NULL"));
    }

    [Fact]
    public async Task Freed_seats_can_be_reserved_by_another_user()
    {
        var (showId, reservationId, _) = await AliceReservesAsync();
        await CancelAsync(reservationId, _alice);

        var bobs = await ReserveAsync(api.Client, showId, _bob, ["A2", "A3"], "key-1");

        Assert.Equal(HttpStatusCode.Created, bobs.Status);
        Assert.Equal(2, await DbAsync($"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND user_id = '{_bob}' AND reservation_id = '{bobs.ReservationId}'"));
    }

    [Fact]
    public async Task Non_owner_cancel_is_403_and_changes_nothing()
    {
        var (showId, reservationId, _) = await AliceReservesAsync();
        var before = api.Metrics.Cancelled;

        AssertProblem(await CancelAsync(reservationId, _bob), HttpStatusCode.Forbidden, "not_owner");

        Assert.Equal(before, api.Metrics.Cancelled);
        Assert.Equal(2, (await ShowAsync(showId)).GetProperty("counts").GetProperty("confirmed").GetInt32());
        Assert.Equal(2, await DbAsync($"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND user_id = '{_alice}' AND reservation_id = '{reservationId}'"));
        Assert.Equal(1, await DbAsync($"SELECT count(*) FROM reservations WHERE id = '{reservationId}' AND status = 'confirmed' AND cancelled_at IS NULL"));
    }

    [Fact]
    public async Task Unknown_or_malformed_id_is_404()
    {
        AssertProblem(await CancelAsync(Guid.NewGuid(), _alice), HttpStatusCode.NotFound, "reservation_not_found");
        AssertProblem(await CancelAsync("not-a-guid", _alice), HttpStatusCode.NotFound, "reservation_not_found");
    }

    [Fact]
    public async Task Double_cancel_is_200_with_the_same_body_and_counts_once()
    {
        var (showId, reservationId, _) = await AliceReservesAsync();
        var before = api.Metrics.Cancelled;

        var first = await CancelAsync(reservationId, _alice);
        var second = await CancelAsync(reservationId, _alice);

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(first.Raw, second.Raw);   // same cancelled_at: the second cancel changed nothing
        Assert.Equal(before + 1, api.Metrics.Cancelled);
        Assert.Equal(4, (await ShowAsync(showId)).GetProperty("counts").GetProperty("available").GetInt32());
    }

    [Fact]
    public async Task Cancel_without_a_token_is_401_and_changes_nothing()
    {
        var (_, reservationId, _) = await AliceReservesAsync();

        AssertProblem(await CancelAsync(reservationId, userId: null), HttpStatusCode.Unauthorized, "unauthorized");

        Assert.Equal(1, await DbAsync($"SELECT count(*) FROM reservations WHERE id = '{reservationId}' AND status = 'confirmed'"));
    }

    // ---- get ----

    [Fact]
    public async Task Owner_get_returns_the_same_body_as_the_201()
    {
        var (_, reservationId, createdBody) = await AliceReservesAsync();

        var got = await GetAsync(reservationId, _alice);

        Assert.Equal(HttpStatusCode.OK, got.Status);
        Assert.Equal(createdBody, got.Raw);
    }

    [Fact]
    public async Task Owner_get_after_cancel_returns_the_cancel_body()
    {
        var (_, reservationId, _) = await AliceReservesAsync();
        var cancelled = await CancelAsync(reservationId, _alice);

        var got = await GetAsync(reservationId, _alice);

        Assert.Equal(HttpStatusCode.OK, got.Status);
        Assert.Equal(cancelled.Raw, got.Raw);   // cancelled_at stamped at the precision Postgres keeps (D-096)
    }

    [Fact]
    public async Task Get_by_another_user_is_403_unknown_is_404_and_no_token_is_401()
    {
        var (_, reservationId, _) = await AliceReservesAsync();

        AssertProblem(await GetAsync(reservationId, _bob), HttpStatusCode.Forbidden, "not_owner");
        AssertProblem(await GetAsync(Guid.NewGuid(), _alice), HttpStatusCode.NotFound, "reservation_not_found");
        AssertProblem(await GetAsync("not-a-guid", _alice), HttpStatusCode.NotFound, "reservation_not_found");
        AssertProblem(await GetAsync(reservationId, userId: null), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Location_of_the_201_points_at_the_get()
    {
        var showId = await CreateShowAsync(api.Client, ["A1"]);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/shows/{showId}/reserve")
        {
            Content = JsonContent.Create(new { seats = new[] { "A1" }, idempotency_key = "key-1" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(_alice));
        using var created = await api.Client.SendAsync(request);

        var got = await SendAsync(HttpMethod.Get, created.Headers.Location!.OriginalString, _alice);

        Assert.Equal(HttpStatusCode.OK, got.Status);
        Assert.Equal(await created.Content.ReadAsStringAsync(), got.Raw);
    }
}
