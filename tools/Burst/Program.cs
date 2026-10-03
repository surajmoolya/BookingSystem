// Burst: concurrent load and correctness checker for the seat reservation service (lld §12).
// T-3.15 builds the minimum for the first remote check (hot seat + mixed storm); M7 adds the remaining scenarios.

using System.Text.Encodings.Web;
using System.Text.Json;
using Burst;

const string Usage = """
    Usage: burst <BASE_URL> [options]

    Fires concurrent reservation traffic at a running service and checks the results (lld §12).

    Options:
      --scenario <list>        comma-separated: hot,idem,conflict,limit,mixed,cancel (default: all built so far)
      --hot-users <n>          distinct users hitting one seat (default: 500)
      --idem-requests <n>      concurrent same-key requests (default: 200)
      --limit-requests <n>     one user, distinct seats (default: 50)
      --mixed-requests <n>     total mixed-storm requests (default: 20000)
      --mixed-users <n>        (default: 5000)
      --mixed-seats <n>        (default: 1000, at least 40)
      --max-connections <n>    SocketsHttpHandler.MaxConnectionsPerServer (default: 1000)
      --http2                  HTTP/2 multiplexing, fewer sockets (negotiated over https; plain http stays on 1.1)
      --timeout-seconds <n>    per request; the zero-5xx gate also requires 0 transport errors at this timeout (default: 60)
      --key-in <mode>          where the Idempotency-Key goes: header, body or mixed (alternate per request) (default: mixed)
      --json <file>            write a machine-readable report
      -h, --help               show this help

    Tokens are minted up front via POST /auth/token, at most 32 in flight, outside the timed window.
    Exit code: 0 if every scenario passes, 1 otherwise.
    """;

if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

if (!BurstOptions.TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine(error);
    return 1;
}

ThreadPool.SetMinThreads(256, 256);   // thousands of requests are released at once; don't let the pool ramp up slowly

using var client = new ServiceClient(options);
Console.WriteLine($"target {options.BaseUrl}  scenarios {string.Join(",", options.Scenarios)}  timeout {options.TimeoutSeconds}s  " +
                  $"max-connections {options.MaxConnections}  http2 {options.Http2}  key-in {options.KeyIn}");
await client.WaitUntilReadyAsync(TimeSpan.FromMinutes(5));
Console.WriteLine("service ready");
Console.WriteLine();

var scenarios = new Scenarios(client, options);
var results = new List<ScenarioResult>();
foreach (var name in options.Scenarios)
{
    var (result, lines) = name switch
    {
        "hot" => await scenarios.HotSeatAsync(),
        "idem" => await scenarios.IdempotentRetriesAsync(),
        "conflict" => await scenarios.KeyConflictAsync(),
        "mixed" => await scenarios.MixedStormAsync(),
        _ => throw new InvalidOperationException($"Scenario '{name}' is not wired."),
    };
    Report.Scenario(result, lines);
    results.Add(result);
}

Report.Totals(results);

if (options.JsonPath is { } path)
{
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Report.Json(options.BaseUrl, results), new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}

return results.All(r => r.Pass) ? 0 : 1;
