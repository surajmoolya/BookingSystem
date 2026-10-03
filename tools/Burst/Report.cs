namespace Burst;

public sealed record ScenarioResult(string Name, string Title, Outcome[] Outcomes, List<string> Failures, TimeSpan Elapsed)
{
    public bool Pass => Failures.Count == 0;

    public double RequestsPerSecond => Outcomes.Length / Math.Max(Elapsed.TotalSeconds, 0.001);

    /// <summary>The scenario's extra report lines (show ids, reconciliation, gauges), also written to the JSON report.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Console and JSON report in the lld §12 layout; every number comes from <see cref="Stats"/>.</summary>
public static class Report
{
    public static void Scenario(ScenarioResult result)
    {
        var counts = Stats.Count(result.Outcomes);
        Console.WriteLine($"== {result.Title} ".PadRight(72, '='));
        if (result.Outcomes.Length > 0)   // the final reconciliation sends no reservation traffic
        {
            foreach (var (bucket, count) in Stats.Buckets(result.Outcomes))
            {
                Line(bucket, count.ToString());
            }

            Line("5xx", counts[OutcomeClass.ServerError].ToString());
            Line("transport errors", counts[OutcomeClass.TransportError].ToString());
            Line("latency p50/p95/p99", Stats.Latency(result.Outcomes).ToString());
            Line("wall time", $"{result.Elapsed.TotalSeconds:0.0}s ({result.RequestsPerSecond:0} req/s)");
        }

        foreach (var line in result.Notes)
        {
            Console.WriteLine($"  {line}");
        }

        foreach (var failure in result.Failures)
        {
            Console.WriteLine($"  FAIL: {failure}");
        }

        Line("RESULT", result.Pass ? "PASS" : "FAIL");
        Console.WriteLine();
    }

    public static void Totals(IReadOnlyList<ScenarioResult> results)
    {
        var all = results.SelectMany(r => r.Outcomes).ToArray();
        var counts = Stats.Count(all);
        string Pair(OutcomeClass c) => $"{Stats.Name(c)}: {counts[c]}";

        Console.WriteLine("== TOTALS ".PadRight(72, '='));
        Console.WriteLine(string.Join("   ", new[] { OutcomeClass.Confirmed, OutcomeClass.SeatTaken, OutcomeClass.PerUserLimit, OutcomeClass.Replayed }.Select(Pair)));
        Console.WriteLine(string.Join("   ", new[] { OutcomeClass.KeyConflict, OutcomeClass.NotOwner, OutcomeClass.Cancelled, OutcomeClass.Other2xx, OutcomeClass.Other4xx }.Select(Pair)));
        Console.WriteLine(string.Join("   ", new[] { OutcomeClass.ServerError, OutcomeClass.TransportError }.Select(Pair)));
        Console.WriteLine($"requests: {all.Length}   latency p50/p95/p99 (all requests): {Stats.Latency(all)}");
        foreach (var line in results.Where(r => r.Name == "final").SelectMany(r => r.Notes.Where(n => !n.StartsWith("show_seats ", StringComparison.Ordinal))))
        {
            Console.WriteLine(line);
        }

        Console.WriteLine($"zero-5xx gate (D-088): {(ZeroFiveXxGate(all) ? "PASS" : "FAIL")}");
        Console.WriteLine($"OVERALL: {(Pass(results) ? "PASS" : "FAIL")}");
    }

    /// <summary>lld §12 #8: across every scenario, no 5xx and no transport error (timeout, reset) at <c>--timeout-seconds</c>.</summary>
    public static bool ZeroFiveXxGate(IEnumerable<Outcome> outcomes) => !outcomes.Any(o => o.Is5xx || o.IsTransportError);

    /// <summary>The run passes only if every scenario (and the final reconciliation) passed and the zero-5xx gate holds.</summary>
    public static bool Pass(IReadOnlyList<ScenarioResult> results) =>
        results.All(r => r.Pass) && ZeroFiveXxGate(results.SelectMany(r => r.Outcomes));

    /// <summary>The <c>--json</c> report: the same numbers as the console, keyed by the same snake_case names.</summary>
    public static object Json(Uri target, IReadOnlyList<ScenarioResult> results)
    {
        var all = results.SelectMany(r => r.Outcomes).ToArray();
        return new
        {
            target = target.ToString(),
            at = DateTimeOffset.UtcNow,
            pass = Pass(results),
            zero_5xx_gate = ZeroFiveXxGate(all),
            totals = new { requests = all.Length, counts = Counts(all), latency_ms = Stats.Latency(all) },
            scenarios = results.Select(r => new
            {
                name = r.Name,
                title = r.Title,
                pass = r.Pass,
                failures = r.Failures,
                notes = r.Notes,
                requests = r.Outcomes.Length,
                elapsed_seconds = r.Elapsed.TotalSeconds,
                requests_per_second = r.RequestsPerSecond,
                counts = Counts(r.Outcomes),
                buckets = Stats.Buckets(r.Outcomes).ToDictionary(b => b.Bucket, b => b.Count),
                latency_ms = Stats.Latency(r.Outcomes),
            }),
        };
    }

    private static Dictionary<string, int> Counts(IEnumerable<Outcome> outcomes) =>
        Stats.Count(outcomes).ToDictionary(kv => Stats.Name(kv.Key), kv => kv.Value);

    private static void Line(string label, string value) => Console.WriteLine($"  {(label + " ").PadRight(32, '.')} {value}");
}
