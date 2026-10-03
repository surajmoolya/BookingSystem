using System.Globalization;
using System.Text.RegularExpressions;

namespace Burst;

/// <summary>A parsed <c>GET /metrics</c> (Prometheus text exposition), enough for the final reconciliation (lld §12 #7).</summary>
public sealed partial class MetricsText
{
    private readonly List<(string Name, Dictionary<string, string> Labels, double Value)> _samples = [];

    public static MetricsText Parse(string text)
    {
        var metrics = new MetricsText();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#' || SampleLine().Match(line) is not { Success: true } match)
            {
                continue;
            }

            var labels = LabelPair().Matches(match.Groups["labels"].Value)
                .ToDictionary(m => m.Groups["k"].Value, m => m.Groups["v"].Value, StringComparer.Ordinal);
            if (double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                metrics._samples.Add((match.Groups["name"].Value, labels, value));
            }
        }

        return metrics;
    }

    /// <summary>The sum over series of <paramref name="name"/> matching these labels; null when there is no such series.</summary>
    public double? Value(string name, params (string Label, string Value)[] labels)
    {
        var matching = _samples.Where(s => s.Name == name && labels.All(l => s.Labels.TryGetValue(l.Label, out var v) && v == l.Value)).ToArray();
        return matching.Length == 0 ? null : matching.Sum(s => s.Value);
    }

    /// <summary>Every series of <paramref name="name"/> by the value of one label, e.g. declines by <c>reason</c>.</summary>
    public IReadOnlyDictionary<string, double> By(string name, string label) =>
        _samples.Where(s => s.Name == name && s.Labels.ContainsKey(label))
            .GroupBy(s => s.Labels[label], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Value), StringComparer.Ordinal);

    [GeneratedRegex("""^(?<name>[a-zA-Z_:][a-zA-Z0-9_:]*)(\{(?<labels>.*)\})?\s+(?<value>\S+)""")]
    private static partial Regex SampleLine();

    [GeneratedRegex("""(?<k>[a-zA-Z_][a-zA-Z0-9_]*)="(?<v>(?:[^"\\]|\\.)*)" """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LabelPair();
}
