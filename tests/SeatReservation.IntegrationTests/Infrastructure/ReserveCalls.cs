using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>One reserve response, reduced to what concurrency tests count: status, problem <c>code</c>, the body, and whether it was a replay.</summary>
public sealed record ReserveResult(HttpStatusCode Status, string? Code, JsonElement Body, bool Replayed = false)
{
    public bool Is(HttpStatusCode status, string? code = null) => Status == status && (code is null || Code == code);

    public Guid ReservationId => Body.GetProperty("reservation_id").GetGuid();

    public string[] Seats => Body.GetProperty("seats").EnumerateArray().Select(s => s.GetString()!).ToArray();

    public override string ToString() => Code is null ? $"{(int)Status}" : $"{(int)Status} {Code}";
}

/// <summary>HTTP and database plumbing shared by the reserve concurrency tests.</summary>
public static class ReserveCalls
{
    /// <summary>Creates a show over HTTP (anonymous, D-021) and returns its id.</summary>
    public static async Task<Guid> CreateShowAsync(HttpClient client, IReadOnlyList<string> seats, int perUserLimit = 4, long pricePaise = 25_000)
    {
        using var response = await client.PostAsJsonAsync("/shows", new { name = "concurrency", seats, price_paise = pricePaise, per_user_limit = perUserLimit });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Labels S1..Sn.</summary>
    public static string[] Labels(int count) => Enumerable.Range(1, count).Select(i => $"S{i}").ToArray();

    /// <summary>One reserve as <paramref name="userId"/>; the token travels per request, so one client can serve many users.</summary>
    public static async Task<ReserveResult> ReserveAsync(HttpClient client, Guid showId, string userId, IReadOnlyList<string> seats, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/shows/{showId}/reserve")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { seats, idempotency_key = key }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(userId));

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var code = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var c) ? c.GetString() : null;
        var replayed = response.Headers.TryGetValues("Idempotent-Replayed", out var values) && values.Single() == "true";
        return new ReserveResult(response.StatusCode, code, body, replayed);
    }

    /// <summary>"201×1, 409 seat_taken×199": printed in assertion messages so a failure shows the whole outcome mix.</summary>
    public static string Histogram(IEnumerable<ReserveResult> results) =>
        string.Join(", ", results.GroupBy(r => r.ToString()).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}×{g.Count()}"));

    public static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
