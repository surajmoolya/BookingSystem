using System.Globalization;
using System.Text.RegularExpressions;

namespace SeatReservation.IntegrationTests.Infrastructure;

/// <summary>One sample line of the Prometheus text exposition: <c>name{label="value",…} 12</c>.</summary>
public sealed record MetricSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value)
{
    public bool Has(string label, string value) => Labels.TryGetValue(label, out var v) && v == value;
}

/// <summary>A parsed <c>GET /metrics</c> response, with lookups for the tests.</summary>
public sealed partial class MetricsScrape(IReadOnlyList<MetricSample> samples, IReadOnlySet<string> declaredNames)
{
    public IReadOnlyList<MetricSample> Samples { get; } = samples;

    /// <summary>Names from <c># TYPE</c> lines, so a metric that has no sample yet (a labelled counter) still counts as present.</summary>
    public IReadOnlySet<string> DeclaredNames { get; } = declaredNames;

    public static async Task<MetricsScrape> FetchAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/metrics");
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync());
    }

    public static MetricsScrape Parse(string text)
    {
        var samples = new List<MetricSample>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# TYPE ", StringComparison.Ordinal))
            {
                declared.Add(line.Split(' ')[2]);
                continue;
            }

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var match = SampleLine().Match(line);
            if (!match.Success)
            {
                throw new FormatException($"Unparsable metrics line: {line}");
            }

            var labels = LabelPair().Matches(match.Groups["labels"].Value)
                .ToDictionary(m => m.Groups["k"].Value, m => m.Groups["v"].Value, StringComparer.Ordinal);
            samples.Add(new MetricSample(match.Groups["name"].Value, labels, double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)));
        }

        return new MetricsScrape(samples, declared);
    }

    public IEnumerable<MetricSample> Named(string name) => Samples.Where(s => s.Name == name);

    /// <summary>The value of the one series of <paramref name="name"/> with these labels, or 0 when it has not been created yet.</summary>
    public double Value(string name, params (string Label, string Value)[] labels) =>
        Named(name).Where(s => labels.All(l => s.Has(l.Label, l.Value))).Sum(s => s.Value);

    [GeneratedRegex("""^(?<name>[a-zA-Z_:][a-zA-Z0-9_:]*)(\{(?<labels>.*)\})?\s+(?<value>\S+)""")]
    private static partial Regex SampleLine();

    [GeneratedRegex("""(?<k>[a-zA-Z_][a-zA-Z0-9_]*)="(?<v>(?:[^"\\]|\\.)*)" """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LabelPair();
}
