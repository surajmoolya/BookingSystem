namespace Burst;

/// <summary>How the totals block (lld §12) counts a response. Every outcome falls into exactly one class.</summary>
public enum OutcomeClass
{
    Confirmed,
    Replayed,
    SeatTaken,
    PerUserLimit,
    KeyConflict,
    Other2xx,
    Other4xx,
    ServerError,
    TransportError,
}

/// <summary>Latency over every response that came back, declines included: that's what a client experiences.</summary>
public sealed record LatencySummary(int Count, double P50, double P95, double P99, double Max)
{
    public override string ToString() => Count == 0 ? "n/a" : $"{P50:0}ms / {P95:0}ms / {P99:0}ms (max {Max:0}ms)";
}

public static class Stats
{
    public static OutcomeClass Classify(Outcome o) => o switch
    {
        { IsTransportError: true } => OutcomeClass.TransportError,
        { Is5xx: true } => OutcomeClass.ServerError,
        { Status: 201 } => OutcomeClass.Confirmed,
        { Status: 200, Replayed: true } => OutcomeClass.Replayed,
        { Status: >= 200 and < 300 } => OutcomeClass.Other2xx,
        { Status: 409, Code: "seat_taken" } => OutcomeClass.SeatTaken,
        { Status: 409, Code: "per_user_limit" } => OutcomeClass.PerUserLimit,
        { Status: 409, Code: "idempotency_key_conflict" } => OutcomeClass.KeyConflict,
        _ => OutcomeClass.Other4xx,   // 4xx, plus anything odd (1xx/3xx) that a client would still have to handle
    };

    /// <summary>A count for every class, zeros included, so reports always show the same keys.</summary>
    public static IReadOnlyDictionary<OutcomeClass, int> Count(IEnumerable<Outcome> outcomes)
    {
        var counts = Enum.GetValues<OutcomeClass>().ToDictionary(c => c, _ => 0);
        foreach (var outcome in outcomes)
        {
            counts[Classify(outcome)]++;
        }

        return counts;
    }

    public static LatencySummary Latency(IEnumerable<Outcome> outcomes)
    {
        var sorted = outcomes.Where(o => !o.IsTransportError).Select(o => o.LatencyMs).Order().ToArray();
        return sorted.Length == 0
            ? new LatencySummary(0, 0, 0, 0, 0)
            : new LatencySummary(sorted.Length, Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99), sorted[^1]);
    }

    /// <summary>Nearest-rank percentile of an ascending array: the smallest value with at least p of the sample at or below it.</summary>
    public static double Percentile(double[] sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    /// <summary>"201", "409 seat_taken", "transport timeout": the finer per-scenario breakdown.</summary>
    public static IEnumerable<(string Bucket, int Count)> Buckets(IEnumerable<Outcome> outcomes) =>
        outcomes.GroupBy(o => o.Bucket).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Count()));

    /// <summary>snake_case name used in the console totals and the JSON report.</summary>
    public static string Name(OutcomeClass c) => c switch
    {
        OutcomeClass.Confirmed => "confirmed",
        OutcomeClass.Replayed => "idempotent_replay",
        OutcomeClass.SeatTaken => "seat_taken",
        OutcomeClass.PerUserLimit => "per_user_limit",
        OutcomeClass.KeyConflict => "idempotency_key_conflict",
        OutcomeClass.Other2xx => "other_2xx",
        OutcomeClass.Other4xx => "other_4xx",
        OutcomeClass.ServerError => "5xx",
        OutcomeClass.TransportError => "transport_errors",
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, null),
    };
}
