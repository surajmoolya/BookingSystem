using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Http;

/// <summary><c>POST /shows/{id}/reserve</c> over HTTP against real Postgres (lld §5.4, D-084).</summary>
[Collection(PostgresCollection.Name)]
public class ReserveApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string KeyHeader = "Idempotency-Key";

    // Keys are scoped per user across every show (D-031) and the host is shared by the class, so each test (a new instance)
    // gets its own users; otherwise "alice/key-1" from one test would be a key conflict in the next.
    private readonly string _alice = $"alice-{Guid.NewGuid():N}"[..14];
    private readonly string _bob = $"bob-{Guid.NewGuid():N}"[..12];

    private HttpClient As(string user) => api.Factory.CreateClient().AsUser(user);

    private async Task<Guid> NewShowAsync(long price = 25_000, int limit = 4, params string[] seats)
    {
        using var response = await api.Client.PostAsJsonAsync("/shows", new
        {
            name = "friday-night",
            seats = seats.Length == 0 ? ["A1", "A2", "A3", "A4", "A5", "A6"] : seats,
            price_paise = price,
            per_user_limit = limit,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> ReserveAsync(HttpClient client, Guid showId, object body, string? headerKey = null) =>
        await ReserveRawAsync(client, showId.ToString(), JsonSerializer.Serialize(body), headerKey);

    private static async Task<HttpResponseMessage> ReserveRawAsync(HttpClient client, string showId, string json, string? headerKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/shows/{showId}/reserve")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (headerKey is not null)
        {
            request.Headers.Add(KeyHeader, headerKey);
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }

    private static async Task AssertValidationAsync(HttpResponseMessage response, string field)
    {
        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"expected errors.{field} in {problem}");
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private async Task<JsonElement> ShowAsync(Guid showId) =>
        await ReadJsonAsync(await api.Client.GetAsync($"/shows/{showId}"), HttpStatusCode.OK);

    // ---- success ----

    [Fact]
    public async Task Reserve_returns_201_with_Location_and_every_field()
    {
        var showId = await NewShowAsync(price: 25_000);
        using var alice = As(_alice);

        using var response = await ReserveAsync(alice, showId, new { seats = new[] { "A2", "A1" }, idempotency_key = "key-1" });

        var body = await ReadJsonAsync(response, HttpStatusCode.Created);
        var id = body.GetProperty("reservation_id").GetGuid();
        Assert.Equal($"/reservations/{id}", response.Headers.Location!.OriginalString);
        Assert.Equal(showId, body.GetProperty("show_id").GetGuid());
        Assert.Equal(_alice, body.GetProperty("user_id").GetString());
        Assert.Equal(["A1", "A2"], Strings(body.GetProperty("seats")));
        Assert.Equal(50_000, body.GetProperty("amount_paise").GetInt64());
        Assert.Equal("confirmed", body.GetProperty("status").GetString());
        Assert.Equal("key-1", body.GetProperty("idempotency_key").GetString());
        Assert.EndsWith("Z", body.GetProperty("created_at").GetString());
        Assert.False(body.TryGetProperty("cancelled_at", out _));
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));

        var show = await ShowAsync(showId);
        Assert.Equal(2, show.GetProperty("counts").GetProperty("confirmed").GetInt32());
    }

    // ---- key source (D-084) ----

    [Fact]
    public async Task Key_in_the_header_only_is_accepted()
    {
        using var alice = As(_alice);
        using var response = await ReserveAsync(alice, await NewShowAsync(), new { seats = new[] { "A1" } }, headerKey: "hdr-1");

        Assert.Equal("hdr-1", (await ReadJsonAsync(response, HttpStatusCode.Created)).GetProperty("idempotency_key").GetString());
    }

    [Fact]
    public async Task Key_in_the_body_only_is_accepted()
    {
        using var alice = As(_alice);
        using var response = await ReserveAsync(alice, await NewShowAsync(), new { seats = new[] { "A1" }, idempotency_key = "body-1" });

        Assert.Equal("body-1", (await ReadJsonAsync(response, HttpStatusCode.Created)).GetProperty("idempotency_key").GetString());
    }

    [Fact]
    public async Task Key_in_both_places_with_the_same_value_is_accepted()
    {
        using var alice = As(_alice);
        using var response = await ReserveAsync(alice, await NewShowAsync(), new { seats = new[] { "A1" }, idempotency_key = "same" }, headerKey: "same");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Key_in_both_places_with_different_values_is_a_400_and_reserves_nothing()
    {
        var showId = await NewShowAsync();
        using var alice = As(_alice);

        using var response = await ReserveAsync(alice, showId, new { seats = new[] { "A1" }, idempotency_key = "body" }, headerKey: "header");

        await AssertValidationAsync(response, "idempotency_key");
        Assert.Equal(0, (await ShowAsync(showId)).GetProperty("counts").GetProperty("confirmed").GetInt32());
    }

    [Fact]
    public async Task No_key_anywhere_is_a_400()
    {
        using var alice = As(_alice);
        using var response = await ReserveAsync(alice, await NewShowAsync(), new { seats = new[] { "A1" } });

        await AssertValidationAsync(response, "idempotency_key");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("café")]
    public async Task A_key_with_bad_characters_is_a_400(string key)
    {
        using var alice = As(_alice);
        using var response = await ReserveAsync(alice, await NewShowAsync(), new { seats = new[] { "A1" }, idempotency_key = key });

        await AssertValidationAsync(response, "idempotency_key");
    }

    [Fact]
    public async Task A_key_longer_than_128_characters_is_a_400()
    {
        using var alice = As(_alice);
        using var response = await ReserveAsync(alice, await NewShowAsync(), new { seats = new[] { "A1" }, idempotency_key = new string('k', 129) });

        await AssertValidationAsync(response, "idempotency_key");
    }

    // ---- bad requests ----

    [Fact]
    public async Task Unknown_show_is_404()
    {
        using var alice = As(_alice);

        await ReadProblemAsync(await ReserveAsync(alice, Guid.NewGuid(), new { seats = new[] { "A1" }, idempotency_key = "k" }), HttpStatusCode.NotFound, "show_not_found");
        await ReadProblemAsync(await ReserveRawAsync(alice, "not-a-guid", """{"seats":["A1"],"idempotency_key":"k"}"""), HttpStatusCode.NotFound, "show_not_found");
    }

    [Fact]
    public async Task Unknown_seat_is_400_listing_every_unknown_label()
    {
        using var alice = As(_alice);

        using var response = await ReserveAsync(alice, await NewShowAsync(), new { seats = new[] { "Z9", "A1", "a1" }, idempotency_key = "k" });

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest, "unknown_seat");
        Assert.Equal(["Z9", "a1"], Strings(problem.GetProperty("unknown_seats")));
    }

    [Fact]
    public async Task Duplicate_or_missing_seats_are_400_validation()
    {
        var showId = await NewShowAsync();
        using var alice = As(_alice);

        await AssertValidationAsync(await ReserveAsync(alice, showId, new { seats = new[] { "A1", "A1" }, idempotency_key = "k" }), "seats");
        await AssertValidationAsync(await ReserveAsync(alice, showId, new { seats = Array.Empty<string>(), idempotency_key = "k" }), "seats");
        await AssertValidationAsync(await ReserveAsync(alice, showId, new { idempotency_key = "k" }), "seats");
        await AssertValidationAsync(await ReserveRawAsync(alice, showId.ToString(), """{"seats":["A1",null],"idempotency_key":"k"}"""), "seats");
    }

    [Fact]
    public async Task Malformed_json_is_400_validation()
    {
        using var alice = As(_alice);

        using var response = await ReserveRawAsync(alice, (await NewShowAsync()).ToString(), "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Without_a_token_it_is_401_problem_with_a_bearer_challenge()
    {
        var showId = await NewShowAsync();

        using var response = await ReserveAsync(api.Client, showId, new { seats = new[] { "A1" }, idempotency_key = "k" });

        await ReadProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.Equal(0, (await ShowAsync(showId)).GetProperty("counts").GetProperty("confirmed").GetInt32());
    }

    // ---- identity ----

    [Fact]
    public async Task A_user_id_in_the_body_is_ignored_in_favour_of_the_token()
    {
        using var alice = As(_alice);

        using var response = await ReserveAsync(alice, await NewShowAsync(), new { seats = new[] { "A1" }, idempotency_key = "k", user_id = "mallory" });

        Assert.Equal(_alice, (await ReadJsonAsync(response, HttpStatusCode.Created)).GetProperty("user_id").GetString());
    }

    // ---- declines ----

    [Fact]
    public async Task A_taken_seat_is_409_seat_taken_listing_the_unavailable_seats()
    {
        var showId = await NewShowAsync();
        using var alice = As(_alice);
        using var bob = As(_bob);
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(alice, showId, new { seats = new[] { "A2", "A3" }, idempotency_key = "a" })).StatusCode);

        using var response = await ReserveAsync(bob, showId, new { seats = new[] { "A1", "A2", "A3" }, idempotency_key = "b" });

        var problem = await ReadProblemAsync(response, HttpStatusCode.Conflict, "seat_taken");
        Assert.Equal(["A2", "A3"], Strings(problem.GetProperty("unavailable_seats")));
        var a1 = (await ShowAsync(showId)).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("label").GetString() == "A1");
        Assert.Equal("available", a1.GetProperty("status").GetString());   // all-or-nothing
    }

    [Fact]
    public async Task Going_over_the_limit_is_409_per_user_limit_with_limit_held_and_requested()
    {
        var showId = await NewShowAsync(limit: 2);
        using var alice = As(_alice);
        Assert.Equal(HttpStatusCode.Created, (await ReserveAsync(alice, showId, new { seats = new[] { "A1" }, idempotency_key = "a" })).StatusCode);

        using var response = await ReserveAsync(alice, showId, new { seats = new[] { "A2", "A3" }, idempotency_key = "b" });

        var problem = await ReadProblemAsync(response, HttpStatusCode.Conflict, "per_user_limit");
        Assert.Equal(2, problem.GetProperty("limit").GetInt32());
        Assert.Equal(1, problem.GetProperty("held").GetInt32());
        Assert.Equal(2, problem.GetProperty("requested").GetInt32());
    }

    // ---- idempotency ----

    [Fact]
    public async Task A_replay_is_200_with_the_original_body_and_the_replayed_header()
    {
        var showId = await NewShowAsync();
        using var alice = As(_alice);
        using var first = await ReserveAsync(alice, showId, new { seats = new[] { "A1", "A2" }, idempotency_key = "key-1" });
        var original = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var replay = await ReserveAsync(alice, showId, new { seats = new[] { "A2", "A1" } }, headerKey: "key-1");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());   // byte for byte, created_at included (D-096)
        Assert.Equal(2, (await ShowAsync(showId)).GetProperty("counts").GetProperty("confirmed").GetInt32());
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_is_409_with_the_original_reservation_id()
    {
        var showId = await NewShowAsync();
        using var alice = As(_alice);
        var originalId = (await ReadJsonAsync(await ReserveAsync(alice, showId, new { seats = new[] { "A1" }, idempotency_key = "key-1" }), HttpStatusCode.Created))
            .GetProperty("reservation_id").GetGuid();

        using var response = await ReserveAsync(alice, showId, new { seats = new[] { "A2" }, idempotency_key = "key-1" });

        var problem = await ReadProblemAsync(response, HttpStatusCode.Conflict, "idempotency_key_conflict");
        Assert.Equal(originalId, problem.GetProperty("reservation_id").GetGuid());
    }
}
