namespace Burst;

public enum KeyPlacement
{
    Header,
    Body,
}

/// <summary>Command-line options (lld §12). Scenarios not built yet are rejected by name rather than silently skipped.</summary>
public sealed record BurstOptions(
    Uri BaseUrl,
    IReadOnlyList<string> Scenarios,
    int HotUsers,
    int IdemRequests,
    int LimitRequests,
    int MixedRequests,
    int MixedUsers,
    int MixedSeats,
    int MaxConnections,
    bool Http2,
    int TimeoutSeconds,
    string KeyIn,
    string? JsonPath)
{
    public static readonly string[] Implemented = ["hot", "idem", "conflict", "limit", "mixed"];

    /// <summary>The rest of lld §12's scenarios arrive with M7.</summary>
    public static readonly string[] Planned = ["cancel"];

    /// <summary>Concurrent <c>POST /auth/token</c> calls while minting, before any timed window.</summary>
    public const int MintParallelism = 32;

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary><c>--key-in mixed</c> alternates per request, so both key sources (D-084) see traffic.</summary>
    public KeyPlacement KeyPlacementFor(int index) => KeyIn switch
    {
        "header" => KeyPlacement.Header,
        "body" => KeyPlacement.Body,
        _ => index % 2 == 0 ? KeyPlacement.Header : KeyPlacement.Body,
    };

    public static bool TryParse(string[] args, out BurstOptions options, out string error)
    {
        options = null!;
        error = "";
        if (args.Length == 0 || !Uri.TryCreate(args[0], UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("http" or "https"))
        {
            error = "The first argument must be the service's base URL, e.g. https://seatres-api.onrender.com";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var http2 = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--http2")
            {
                http2 = true;
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                values[args[i][2..]] = args[++i];
            }
            else
            {
                error = $"Unexpected argument '{args[i]}'.";
                return false;
            }
        }

        var known = new HashSet<string>(["scenario", "hot-users", "idem-requests", "limit-requests", "mixed-requests", "mixed-users",
            "mixed-seats", "max-connections", "timeout-seconds", "key-in", "json"], StringComparer.Ordinal);
        if (values.Keys.FirstOrDefault(k => !known.Contains(k)) is { } unknown)
        {
            error = $"Unknown option --{unknown}.";
            return false;
        }

        var scenarios = values.GetValueOrDefault("scenario", "all") is "all"
            ? Implemented
            : values["scenario"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (scenarios.FirstOrDefault(s => !Implemented.Contains(s)) is { } missing)
        {
            error = Planned.Contains(missing)
                ? $"Scenario '{missing}' is not implemented yet (M7). Available: {string.Join(", ", Implemented)}."
                : $"Unknown scenario '{missing}'.";
            return false;
        }

        var keyIn = values.GetValueOrDefault("key-in", "mixed");
        if (keyIn is not ("header" or "body" or "mixed"))
        {
            error = "--key-in must be header, body or mixed.";
            return false;
        }

        try
        {
            options = new BurstOptions(
                baseUrl,
                scenarios,
                Int(values, "hot-users", 500),
                Int(values, "idem-requests", 200),
                Int(values, "limit-requests", 50),
                Int(values, "mixed-requests", 20_000),
                Int(values, "mixed-users", 5_000),
                Int(values, "mixed-seats", 1_000),
                Int(values, "max-connections", 1_000),
                http2,
                Int(values, "timeout-seconds", 60),
                keyIn,
                values.GetValueOrDefault("json"));
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        if (options.IdemRequests < 2 || options.LimitRequests < 5)
        {
            error = "--idem-requests must be at least 2 and --limit-requests at least 5 (more than the per-user limit of 4).";
            return false;
        }

        if (options.MixedSeats < 40)
        {
            error = "--mixed-seats must be at least 40 (20 hot seats plus the rest).";
            return false;
        }

        return true;
    }

    private static int Int(Dictionary<string, string> values, string name, int fallback)
    {
        if (!values.TryGetValue(name, out var text))
        {
            return fallback;
        }

        return int.TryParse(text, out var value) && value > 0
            ? value
            : throw new FormatException($"--{name} must be a positive integer, not '{text}'.");
    }
}
