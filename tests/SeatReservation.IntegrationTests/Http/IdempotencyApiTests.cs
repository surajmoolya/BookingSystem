using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SeatReservation.IntegrationTests.Infrastructure;
using static SeatReservation.IntegrationTests.Infrastructure.ReserveCalls;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>Idempotent replay and key conflicts over HTTP, one request at a time (T-3.11, D-031–D-033, D-084).</summary>
[Collection(PostgresCollection.Name)]
public class IdempotencyApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    // Keys are per user across every show and the class shares one host, so each test (a new instance) gets its own user.
    private readonly string _alice = $"alice-{Guid.NewGuid():N}"[..14];

    private sealed record Response(HttpStatusCode Status, bool Replayed, string Raw)
    {
        public JsonElement Json => JsonDocument.Parse(Raw).RootElement;

        public Guid ReservationId => Json.GetProperty("reservation_id").GetGuid();
    }

    /// <summary>Sends the key in the body, the header, or both, and keeps the raw body so replays can be compared byte for byte.</summary>
    private async Task<Response> ReserveAsync(Guid showId, string[] seats, string? bodyKey = null, string? headerKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/shows/{showId}/reserve")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { seats, idempotency_key = bodyKey }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(_alice));
        if (headerKey is not null)
        {
            request.Headers.Add("Idempotency-Key", headerKey);
        }

        using var response = await api.Client.SendAsync(request);
        var replayed = response.Headers.TryGetValues("Idempotent-Replayed", out var values) && values.Single() == "true";
        return new Response(response.StatusCode, replayed, await response.Content.ReadAsStringAsync());
    }

    private Task<Guid> NewShowAsync() => CreateShowAsync(api.Client, ["A1", "A2", "A3", "A4", "A5", "A6"]);

    private async Task<Response> CreatedAsync(Guid showId, string[] seats, string key)
    {
        var created = await ReserveAsync(showId, seats, bodyKey: key);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.False(created.Replayed);
        return created;
    }

    private static void AssertReplayOf(Response original, Response replay)
    {
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.True(replay.Replayed);
        Assert.Equal(original.Raw, replay.Raw);   // the original body, byte for byte
    }

    private static void AssertConflictWith(Response original, Response conflict)
    {
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal("idempotency_key_conflict", conflict.Json.GetProperty("code").GetString());
        Assert.Equal(original.ReservationId, conflict.Json.GetProperty("reservation_id").GetGuid());
        Assert.False(conflict.Replayed);
    }

    private async Task<long> ReservationsAsync() =>
        await ScalarAsync(api.Factory.ConnectionString, $"SELECT count(*) FROM reservations WHERE user_id = '{_alice}'");

    [Fact]
    public async Task A_retry_replays_with_200_the_same_id_and_the_header_and_writes_nothing()
    {
        var showId = await NewShowAsync();
        var original = await CreatedAsync(showId, ["A1", "A2"], "key-1");

        var first = await ReserveAsync(showId, ["A1", "A2"], bodyKey: "key-1");
        var second = await ReserveAsync(showId, ["A1", "A2"], bodyKey: "key-1");

        AssertReplayOf(original, first);
        AssertReplayOf(original, second);
        Assert.Equal(1, await ReservationsAsync());
        Assert.Equal(2, await ScalarAsync(api.Factory.ConnectionString, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND status = 'confirmed'"));
    }

    [Fact]
    public async Task Key_in_the_header_first_and_in_the_body_on_retry_is_a_replay()
    {
        var showId = await NewShowAsync();
        var original = await ReserveAsync(showId, ["A1"], headerKey: "key-1");
        Assert.Equal(HttpStatusCode.Created, original.Status);

        AssertReplayOf(original, await ReserveAsync(showId, ["A1"], bodyKey: "key-1"));
        AssertReplayOf(original, await ReserveAsync(showId, ["A1"], bodyKey: "key-1", headerKey: "key-1"));
    }

    [Fact]
    public async Task A_seat_order_permutation_is_a_replay()
    {
        var showId = await NewShowAsync();
        var original = await CreatedAsync(showId, ["A3", "A1", "A2"], "key-1");

        AssertReplayOf(original, await ReserveAsync(showId, ["A2", "A3", "A1"], bodyKey: "key-1"));
    }

    [Fact]
    public async Task The_same_key_with_different_seats_is_a_409_conflict_naming_the_original()
    {
        var showId = await NewShowAsync();
        var original = await CreatedAsync(showId, ["A1"], "key-1");

        AssertConflictWith(original, await ReserveAsync(showId, ["A2"], bodyKey: "key-1"));     // different seat
        AssertConflictWith(original, await ReserveAsync(showId, ["A1", "A2"], bodyKey: "key-1")); // superset
        Assert.Equal(1, await ReservationsAsync());
    }

    [Fact]
    public async Task The_same_key_on_a_different_show_is_a_409_conflict()
    {
        var original = await CreatedAsync(await NewShowAsync(), ["A1"], "key-1");

        AssertConflictWith(original, await ReserveAsync(await NewShowAsync(), ["A1"], bodyKey: "key-1"));
    }

    [Fact]
    public async Task A_declined_attempt_does_not_bind_the_key()
    {
        var showId = await NewShowAsync();
        var bob = $"bob-{Guid.NewGuid():N}"[..12];
        Assert.True((await ReserveCalls.ReserveAsync(api.Client, showId, bob, ["A1"], "k")).Is(HttpStatusCode.Created));

        var declined = await ReserveAsync(showId, ["A1"], bodyKey: "key-1");
        Assert.Equal(HttpStatusCode.Conflict, declined.Status);
        Assert.Equal("seat_taken", declined.Json.GetProperty("code").GetString());

        // key-1 is still unbound, so it can be used for a different request, which then becomes its first use.
        var created = await CreatedAsync(showId, ["A2"], "key-1");
        AssertReplayOf(created, await ReserveAsync(showId, ["A2"], bodyKey: "key-1"));
    }

    [Fact]
    public async Task A_replay_still_replays_when_the_user_is_now_at_the_limit()
    {
        var showId = await NewShowAsync();   // default limit 4
        var original = await CreatedAsync(showId, ["A1", "A2", "A3", "A4"], "key-1");

        AssertReplayOf(original, await ReserveAsync(showId, ["A4", "A3", "A2", "A1"], bodyKey: "key-1"));   // not per_user_limit
    }

    [Fact]
    public async Task A_replay_after_cancellation_returns_the_cancelled_reservation()
    {
        var showId = await NewShowAsync();
        var original = await CreatedAsync(showId, ["A1", "A2"], "key-1");
        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"/reservations/{original.ReservationId}/cancel");
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(_alice));
        using var cancelled = await api.Client.SendAsync(cancel);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var replay = await ReserveAsync(showId, ["A2", "A1"], bodyKey: "key-1");

        // D-041: the key's attempt ended as this (now cancelled) reservation; a retry must not book the seats again.
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.True(replay.Replayed);
        Assert.Equal(original.ReservationId, replay.ReservationId);
        Assert.Equal("cancelled", replay.Json.GetProperty("status").GetString());
        Assert.Equal(await cancelled.Content.ReadAsStringAsync(), replay.Raw);
        Assert.Equal(1, await ReservationsAsync());
        Assert.Equal(0, await ScalarAsync(api.Factory.ConnectionString, $"SELECT count(*) FROM seats WHERE show_id = '{showId}' AND status <> 'available'"));
    }
}
