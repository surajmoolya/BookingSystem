namespace Burst;

public sealed record ScenarioResult(string Name, string Title, Outcome[] Outcomes, List<string> Failures, TimeSpan Elapsed)
{
    public bool Pass => Failures.Count == 0;

    public double RequestsPerSecond => Outcomes.Length / Math.Max(Elapsed.TotalSeconds, 0.001);
}

/// <summary>Console and JSON report in the lld §12 layout; every number comes from <see cref="Stats"/>.</summary>
public static class Report
{
    public static void Scenario(ScenarioResult result, IEnumerable<string> extraLines)
    {
        var counts = Stats.Count(result.Outcomes);
        Console.WriteLine($"== {result.Title} ".PadRight(72, '='));
        foreach (var (bucket, count) in Stats.Buckets(result.Outcomes))
        {
            Line(bucket, count.ToString());
        }

        Line("5xx", counts[OutcomeClass.ServerError].ToString());
        Line("transport errors", counts[OutcomeClass.TransportError].ToString());
        Line("latency p50/p95/p99", Stats.Latency(result.Outcomes).ToString());
        Line("wall time", $"{result.Elapsed.TotalSeconds:0.0}s ({result.RequestsPerSecond:0} req/s)");
        foreach (var line in extraLines)
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
        Console.WriteLine(string.Join("   ", new[] { OutcomeClass.KeyConflict, OutcomeClass.Other2xx, OutcomeClass.Other4xx, OutcomeClass.ServerError, OutcomeClass.TransportError }.Select(Pair)));
        Console.WriteLine($"requests: {all.Length}   latency p50/p95/p99 (all requests): {Stats.Latency(all)}");
        Console.WriteLine($"OVERALL: {(results.All(r => r.Pass) ? "PASS" : "FAIL")}");
    }

    /// <summary>The <c>--json</c> report: the same numbers as the console, keyed by the same snake_case names.</summary>
    public static object Json(Uri target, IReadOnlyList<ScenarioResult> results)
    {
        var all = results.SelectMany(r => r.Outcomes).ToArray();
        return new
        {
            target = target.ToString(),
            at = DateTimeOffset.UtcNow,
            pass = results.All(r => r.Pass),
            totals = new { requests = all.Length, counts = Counts(all), latency_ms = Stats.Latency(all) },
            scenarios = results.Select(r => new
            {
                name = r.Name,
                title = r.Title,
                pass = r.Pass,
                failures = r.Failures,
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

    private static void Line(string label, string value) => Console.WriteLine($"  {(label + " ").PadRight(22, '.')} {value}");
}
