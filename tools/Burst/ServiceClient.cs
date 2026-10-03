using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Burst;

/// <summary>
/// One reserve call as the tool records it. <see cref="Status"/> is 0 for a transport error (timeout, reset, DNS),
/// which never reached an HTTP response and counts against the zero-5xx gate (D-088).
/// </summary>
public sealed record Outcome(
    int Status,
    string? Code,
    Guid? ReservationId,
    string[] Seats,
    bool Replayed,
    double LatencyMs,
    string? TransportError,
    string UserId)
{
    public bool IsTransportError => Status == 0;

    public bool Is5xx => Status >= 500;

    /// <summary>"201", "409 seat_taken", "transport timeout": the bucket used for counting.</summary>
    public string Bucket => IsTransportError ? $"transport {TransportError}" : Code is null ? $"{Status}" : $"{Status} {Code}";
}

public sealed record ShowState(int Total, int Available, int Held, int Confirmed, IReadOnlyDictionary<string, string> SeatStatus)
{
    public bool Reconciles => Available + Held + Confirmed == Total && SeatStatus.Count == Total;
}

/// <summary>Thin HTTP client for the service. Setup calls throw on failure; reserve calls never throw, they record.</summary>
public sealed class ServiceClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly BurstOptions _options;

    public ServiceClient(BurstOptions options)
    {
        _options = options;
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = options.MaxConnections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = options.Timeout,
            EnableMultipleHttp2Connections = true,
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = options.BaseUrl,
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,   // per request, below, so a timeout is recorded rather than thrown
            DefaultRequestVersion = options.Http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    /// <summary>A free Render instance may be asleep; the first request waits for its cold start.</summary>
    public async Task WaitUntilReadyAsync(TimeSpan max)
    {
        var deadline = DateTime.UtcNow + max;
        while (true)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = await _http.GetAsync("/health/ready", cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // still starting
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"{_options.BaseUrl} did not become ready within {max.TotalSeconds:0}s.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Tokens come from <c>POST /auth/token</c> (the signing key is a server secret), minted before the timed window with
    /// at most <see cref="BurstOptions.MintParallelism"/> calls in flight, so minting never looks like part of the burst.
    /// A failed mint is retried with backoff; after five attempts the run stops, since a missing token would skew the scenario.
    /// </summary>
    public async Task<string[]> MintTokensAsync(IReadOnlyList<string> users, int parallelism = BurstOptions.MintParallelism)
    {
        var tokens = new string[users.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, users.Count), new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (i, ct) =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var response = await _http.PostAsJsonAsync("/auth/token", new { username = users[i] }, ct);
                    response.EnsureSuccessStatusCode();
                    tokens[i] = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;
                    return;
                }
                catch (Exception) when (attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
                }
            }
        });
        return tokens;
    }

    public async Task<Guid> CreateShowAsync(string name, IReadOnlyList<string> seats, int perUserLimit)
    {
        using var response = await _http.PostAsJsonAsync("/shows", new { name, seats, price_paise = 25_000, per_user_limit = perUserLimit });
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException($"POST /shows returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public async Task<ShowState> GetShowAsync(Guid showId)
    {
        using var cts = new CancellationTokenSource(_options.Timeout);
        var body = await _http.GetFromJsonAsync<JsonElement>($"/shows/{showId}", cts.Token);
        var counts = body.GetProperty("counts");
        var seats = body.GetProperty("seats").EnumerateArray()
            .ToDictionary(s => s.GetProperty("label").GetString()!, s => s.GetProperty("status").GetString()!, StringComparer.Ordinal);
        return new ShowState(
            counts.GetProperty("total").GetInt32(),
            counts.GetProperty("available").GetInt32(),
            counts.GetProperty("held").GetInt32(),
            counts.GetProperty("confirmed").GetInt32(),
            seats);
    }

    /// <summary>Prepares nothing lazily: the caller builds every request before the start gate opens.</summary>
    public async Task<Outcome> ReserveAsync(Guid showId, string userId, string token, string[] seats, string key, KeyPlacement placement)
    {
        object body = placement == KeyPlacement.Body ? new { seats, idempotency_key = key } : new { seats };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/shows/{showId}/reserve")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (placement == KeyPlacement.Header)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        var started = Stopwatch.GetTimestamp();
        using var cts = new CancellationTokenSource(_options.Timeout);
        try
        {
            using var response = await _http.SendAsync(request, cts.Token);
            var text = await response.Content.ReadAsStringAsync(cts.Token);
            var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return Parse((int)response.StatusCode, text, response.Headers, latency, userId);
        }
        catch (OperationCanceledException)
        {
            return new Outcome(0, null, null, [], false, Stopwatch.GetElapsedTime(started).TotalMilliseconds, "timeout", userId);
        }
        catch (HttpRequestException ex)
        {
            // Unknown hides the cause (refused, reset, proxy closed): name the innermost exception instead.
            var inner = ex.GetBaseException();
            var kind = ex.HttpRequestError != HttpRequestError.Unknown ? ex.HttpRequestError.ToString()
                : inner == ex ? ex.GetType().Name
                : $"{inner.GetType().Name}: {inner.Message}";
            return new Outcome(0, null, null, [], false, Stopwatch.GetElapsedTime(started).TotalMilliseconds, kind, userId);
        }
    }

    private static Outcome Parse(int status, string text, HttpResponseHeaders headers, double latency, string userId)
    {
        string? code = null;
        Guid? id = null;
        string[] seats = [];
        try
        {
            var json = JsonDocument.Parse(text).RootElement;
            if (json.ValueKind == JsonValueKind.Object)
            {
                code = json.TryGetProperty("code", out var c) ? c.GetString() : null;
                id = json.TryGetProperty("reservation_id", out var r) && r.ValueKind == JsonValueKind.String ? r.GetGuid() : null;
                seats = json.TryGetProperty("seats", out var s) && s.ValueKind == JsonValueKind.Array
                    ? s.EnumerateArray().Select(e => e.GetString()!).ToArray()
                    : [];
            }
        }
        catch (JsonException)
        {
            code = "non_json";   // e.g. an HTML 502 from the platform's edge
        }

        var replayed = headers.TryGetValues("Idempotent-Replayed", out var values) && values.FirstOrDefault() == "true";
        return new Outcome(status, code, id, seats, replayed, latency, null, userId);
    }

    public void Dispose() => _http.Dispose();
}
