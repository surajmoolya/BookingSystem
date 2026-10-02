namespace Burst;

public sealed record ScenarioResult(string Name, string Title, Outcome[] Outcomes, List<string> Failures, TimeSpan Elapsed)
{
    public bool Pass => Failures.Count == 0;
}

/// <summary>Console report in the lld §12 layout.</summary>
public static class Report
{
    public static void Scenario(ScenarioResult result, IEnumerable<string> extraLines)
    {
        Console.WriteLine($"== {result.Title} ".PadRight(72, '='));
        foreach (var (bucket, count) in Buckets(result.Outcomes))
        {
            Line(bucket, count.ToString());
        }

        Line("5xx", result.Outcomes.Count(o => o.Is5xx).ToString());
        Line("transport errors", result.Outcomes.Count(o => o.IsTransportError).ToString());
        Line("latency p50/p95/p99", Latency(result.Outcomes));
        Line("wall time", $"{result.Elapsed.TotalSeconds:0.0}s ({result.Outcomes.Length / Math.Max(result.Elapsed.TotalSeconds, 0.001):0} req/s)");
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
        Console.WriteLine("== TOTALS ".PadRight(72, '='));
        Console.WriteLine(
            $"confirmed: {Count(all, o => o.Status == 201)}   seat_taken: {Count(all, o => o.Code == "seat_taken")}   " +
            $"per_user_limit: {Count(all, o => o.Code == "per_user_limit")}   idempotent_replay: {Count(all, o => o.Status == 200 && o.Replayed)}");
        Console.WriteLine(
            $"idempotency_key_conflict: {Count(all, o => o.Code == "idempotency_key_conflict")}   " +
            $"other_4xx: {Count(all, o => o.Status is >= 400 and < 500 && o.Code is not ("seat_taken" or "per_user_limit" or "idempotency_key_conflict"))}   " +
            $"5xx: {Count(all, o => o.Is5xx)}   transport_errors: {Count(all, o => o.IsTransportError)}");
        Console.WriteLine($"latency p50/p95/p99 (all requests): {Latency(all)}");
        Console.WriteLine($"OVERALL: {(results.All(r => r.Pass) ? "PASS" : "FAIL")}");
    }

    public static IEnumerable<(string Bucket, int Count)> Buckets(IEnumerable<Outcome> outcomes) =>
        outcomes.GroupBy(o => o.Bucket).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Count()));

    /// <summary>Over every response that came back, declines included: that's what a client experiences.</summary>
    public static string Latency(IReadOnlyCollection<Outcome> outcomes)
    {
        var sorted = outcomes.Where(o => !o.IsTransportError).Select(o => o.LatencyMs).Order().ToArray();
        return sorted.Length == 0
            ? "n/a"
            : $"{Percentile(sorted, 0.50):0}ms / {Percentile(sorted, 0.95):0}ms / {Percentile(sorted, 0.99):0}ms";
    }

    public static double Percentile(double[] sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static int Count(IEnumerable<Outcome> outcomes, Func<Outcome, bool> predicate) => outcomes.Count(predicate);

    private static void Line(string label, string value) => Console.WriteLine($"  {(label + " ").PadRight(22, '.')} {value}");
}
