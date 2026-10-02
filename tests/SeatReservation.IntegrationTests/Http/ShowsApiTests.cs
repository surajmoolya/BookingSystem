using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using SeatReservation.IntegrationTests.Infrastructure;

namespace SeatReservation.IntegrationTests.Http;

/// <summary><c>POST /shows</c> and <c>GET /shows/{id}</c> over HTTP against real Postgres (lld §5.2–5.3, D-085).</summary>
[Collection(PostgresCollection.Name)]
public class ShowsApiTests(ApiFixture api, PostgresFixture postgres) : IClassFixture<ApiFixture>
{
    private HttpClient Client => api.Client;

    private Task<HttpResponseMessage> PostJsonAsync(string json) =>
        Client.PostAsync("/shows", new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<JsonElement> CreateAsync(object body)
    {
        using var response = await Client.PostAsJsonAsync("/shows", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    private static async Task<JsonElement> ReadValidationAsync(HttpResponseMessage response, params string[] fields)
    {
        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        var errors = problem.GetProperty("errors");
        foreach (var field in fields)
        {
            Assert.True(errors.TryGetProperty(field, out _), $"expected errors.{field} in {errors}");
        }

        return errors;
    }

    // ---- create ----

    [Fact]
    public async Task Create_returns_201_with_Location_and_every_seat_available()
    {
        using var response = await Client.PostAsJsonAsync("/shows", new { name = "friday-night", seats = new[] { "A1", "A2", "A3" }, price_paise = 25_000, per_user_limit = 4 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/shows/{id}", response.Headers.Location!.OriginalString);
        Assert.Equal("friday-night", body.GetProperty("name").GetString());
        Assert.Equal(25_000, body.GetProperty("price_paise").GetInt64());
        Assert.Equal(4, body.GetProperty("per_user_limit").GetInt32());
        AssertCounts(body, total: 3, available: 3, held: 0, confirmed: 0);
        Assert.Equal(["A1", "A2", "A3"], body.GetProperty("seats").EnumerateArray().Select(s => s.GetProperty("label").GetString()));
        Assert.All(body.GetProperty("seats").EnumerateArray(), s => Assert.Equal("available", s.GetProperty("status").GetString()));
        Assert.EndsWith("Z", body.GetProperty("as_of").GetString());
    }

    [Fact]
    public async Task Get_returns_the_same_state_as_create()
    {
        var created = await CreateAsync(new { name = "gala", seats = new[] { "B2", "A10", "a1" }, price_paise = 100 });

        using var response = await Client.GetAsync($"/shows/{created.GetProperty("id").GetGuid()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var field in new[] { "id", "name", "price_paise", "per_user_limit", "counts", "seats" })
        {
            Assert.Equal(created.GetProperty(field).ToString(), body.GetProperty(field).ToString());
        }

        Assert.Equal(["B2", "A10", "a1"], body.GetProperty("seats").EnumerateArray().Select(s => s.GetProperty("label").GetString()));   // creation order
    }

    [Fact]
    public async Task Get_counts_match_the_seat_rows()
    {
        var created = await CreateAsync(new { name = "gala", seats = new[] { "A1", "A2", "A3", "A4" }, price_paise = 100 });
        var id = created.GetProperty("id").GetGuid();
        var reservation = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO reservations (id, show_id, user_id, idempotency_key, request_hash, seats, amount_paise, status)
            VALUES ('{reservation}', '{id}', 'alice', 'k1', '\x00'::bytea, ARRAY['A2','A4'], 200, 'confirmed');
            UPDATE seats SET status = 'confirmed', user_id = 'alice', reservation_id = '{reservation}'
            WHERE show_id = '{id}' AND label IN ('A2', 'A4');
            """);

        var body = await Client.GetFromJsonAsync<JsonElement>($"/shows/{id}");

        AssertCounts(body, total: 4, available: 2, held: 0, confirmed: 2);
        Assert.Equal(["available", "confirmed", "available", "confirmed"], body.GetProperty("seats").EnumerateArray().Select(s => s.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Unknown_show_is_404_show_not_found()
    {
        using var response = await Client.GetAsync($"/shows/{Guid.NewGuid()}");

        await ReadProblemAsync(response, HttpStatusCode.NotFound, "show_not_found");
    }

    [Fact]
    public async Task Malformed_show_id_is_the_same_404_problem()
    {
        using var response = await Client.GetAsync("/shows/not-a-guid");

        await ReadProblemAsync(response, HttpStatusCode.NotFound, "show_not_found");
    }

    [Fact]
    public async Task Ten_thousand_seat_show_is_accepted()
    {
        var seats = Enumerable.Range(1, 10_000).Select(i => $"ROW-{i:D5}-SEAT-X").ToArray();   // 16-char labels: the largest body

        var body = await CreateAsync(new { name = "stadium", seats, price_paise = 50_000 });

        AssertCounts(body, total: 10_000, available: 10_000, held: 0, confirmed: 0);
    }

    // ---- validation ----

    [Fact]
    public async Task Invalid_fields_are_400_validation_with_snake_case_errors()
    {
        using var response = await Client.PostAsJsonAsync("/shows", new { name = "", seats = new[] { "A1", "A1", "bad label" }, price_paise = -1, per_user_limit = 0 });

        var errors = await ReadValidationAsync(response, "name", "seats", "price_paise", "per_user_limit");
        Assert.Equal(4, errors.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("""{"name":"x","price_paise":1}""", "seats")]
    [InlineData("""{"name":"x","seats":[],"price_paise":1}""", "seats")]
    [InlineData("""{"seats":["A1"],"price_paise":1}""", "name")]
    [InlineData("""{"name":"x","seats":["A1"]}""", "price_paise")]
    [InlineData("""{"name":"x","seats":["A1"],"price_paise":1,"per_user_limit":101}""", "per_user_limit")]
    [InlineData("""{"name":"x","seats":["A1",7],"price_paise":1}""", "seats[1]")]
    public async Task Missing_or_out_of_range_fields_are_400(string json, string field)
    {
        using var response = await PostJsonAsync(json);

        await ReadValidationAsync(response, field);
    }

    [Fact]
    public async Task More_seats_than_Shows_MaxSeats_is_400()
    {
        var seats = Enumerable.Range(1, 10_001).Select(i => $"S{i}").ToArray();

        using var response = await Client.PostAsJsonAsync("/shows", new { name = "too-big", seats, price_paise = 1 });

        await ReadValidationAsync(response, "seats");
    }

    // ---- price_paise stays an integer (D-003) ----

    [Theory]
    [InlineData("250.5")]
    [InlineData("\"25000\"")]
    [InlineData("1e3")]
    [InlineData("9223372036854775808")]   // int64 max + 1
    public async Task Non_integer_price_is_400_on_price_paise(string price)
    {
        using var response = await PostJsonAsync($$"""{"name":"x","seats":["A1"],"price_paise":{{price}}}""");

        var errors = await ReadValidationAsync(response, "price_paise");
        var message = Assert.Single(errors.GetProperty("price_paise").EnumerateArray()).GetString()!;
        Assert.DoesNotContain("System.", message);   // no .NET type names or parser positions leak
        Assert.DoesNotContain("Position", message);
    }

    [Fact]
    public async Task Large_integer_price_round_trips_exactly_as_a_json_integer()
    {
        const long price = 9_007_199_254_740_993;   // 2^53 + 1: a double would round it

        using var response = await PostJsonAsync($$"""{"name":"x","seats":["A1"],"price_paise":{{price}}}""");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"price_paise\":{price}", raw);
        var id = JsonDocument.Parse(raw).RootElement.GetProperty("id").GetGuid();
        Assert.Contains($"\"price_paise\":{price}", await Client.GetStringAsync($"/shows/{id}"));
    }

    // ---- per_user_limit (D-085) ----

    [Fact]
    public async Task Omitted_per_user_limit_defaults_to_4()
    {
        var body = await CreateAsync(new { name = "x", seats = new[] { "A1" }, price_paise = 1 });

        Assert.Equal(4, body.GetProperty("per_user_limit").GetInt32());
    }

    [Fact]
    public async Task Custom_per_user_limit_is_stored_and_returned()
    {
        var body = await CreateAsync(new { name = "x", seats = new[] { "A1" }, price_paise = 1, per_user_limit = 2 });

        Assert.Equal(2, body.GetProperty("per_user_limit").GetInt32());
        var fetched = await Client.GetFromJsonAsync<JsonElement>($"/shows/{body.GetProperty("id").GetGuid()}");
        Assert.Equal(2, fetched.GetProperty("per_user_limit").GetInt32());
    }

    [Fact]
    public async Task Alias_max_seats_per_user_accepted_and_returned_as_per_user_limit()
    {
        var body = await CreateAsync(new { name = "x", seats = new[] { "A1" }, price_paise = 1, max_seats_per_user = 3 });

        Assert.Equal(3, body.GetProperty("per_user_limit").GetInt32());
        Assert.False(body.TryGetProperty("max_seats_per_user", out _));
    }

    [Fact]
    public async Task Alias_and_field_equal_is_accepted()
    {
        var body = await CreateAsync(new { name = "x", seats = new[] { "A1" }, price_paise = 1, per_user_limit = 5, max_seats_per_user = 5 });

        Assert.Equal(5, body.GetProperty("per_user_limit").GetInt32());
    }

    [Fact]
    public async Task Alias_and_field_differ_400()
    {
        using var response = await Client.PostAsJsonAsync("/shows", new { name = "x", seats = new[] { "A1" }, price_paise = 1, per_user_limit = 2, max_seats_per_user = 4 });

        await ReadValidationAsync(response, "per_user_limit");
    }

    [Fact]
    public async Task Alias_out_of_range_is_400_on_per_user_limit()
    {
        using var response = await Client.PostAsJsonAsync("/shows", new { name = "x", seats = new[] { "A1" }, price_paise = 1, max_seats_per_user = 0 });

        await ReadValidationAsync(response, "per_user_limit");
    }

    [Fact]
    public async Task Unknown_body_fields_are_ignored()
    {
        var body = await CreateAsync(new { name = "x", seats = new[] { "A1" }, price_paise = 1, user_id = "mallory", id = Guid.Empty, total_seats = 99 });

        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("total").GetInt32());
    }

    // ---- Shows:RequireAuth (D-021) ----

    [Fact]
    public async Task Create_is_anonymous_by_default()
    {
        using var response = await Client.PostAsJsonAsync("/shows", new { name = "x", seats = new[] { "A1" }, price_paise = 1 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task With_RequireAuth_create_needs_a_token_but_get_stays_public()
    {
        await using var factory = await ApiFactory.StartAsync(postgres, new Dictionary<string, string?> { ["Shows:RequireAuth"] = "true" });
        using var anonymous = factory.CreateClient();
        using var authenticated = factory.CreateClient().AsUser("admin");
        var body = new { name = "x", seats = new[] { "A1" }, price_paise = 1 };

        using var rejected = await anonymous.PostAsJsonAsync("/shows", body);
        await ReadProblemAsync(rejected, HttpStatusCode.Unauthorized, "unauthorized");

        using var created = await authenticated.PostAsJsonAsync("/shows", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var fetched = await anonymous.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    // ---- helpers ----

    private static void AssertCounts(JsonElement body, int total, int available, int held, int confirmed)
    {
        var counts = body.GetProperty("counts");
        Assert.Equal(total, counts.GetProperty("total").GetInt32());
        Assert.Equal(available, counts.GetProperty("available").GetInt32());
        Assert.Equal(held, counts.GetProperty("held").GetInt32());
        Assert.Equal(confirmed, counts.GetProperty("confirmed").GetInt32());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(api.Factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
