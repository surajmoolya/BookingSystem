using System.Diagnostics;

namespace Burst;

/// <summary>
/// The lld §12 scenarios built so far (T-3.15): the hot seat and the mixed storm. Each creates its own show, mints its
/// tokens before the timed window, and releases every request through <see cref="StartGate"/>.
/// </summary>
public sealed class Scenarios(ServiceClient client, BurstOptions options)
{
    private const int PerUserLimit = 4;
    private const int HotSeatCount = 20;

    // Usernames are unique per run: keys and limits are per user (D-031), so a rerun must not collide with the last one.
    private readonly string _run = Guid.NewGuid().ToString("N")[..6];

    public async Task<(ScenarioResult Result, List<string> Lines)> HotSeatAsync()
    {
        var showId = await client.CreateShowAsync($"burst-hot-{_run}", Enumerable.Range(1, 10).Select(i => $"A{i}").ToArray(), PerUserLimit);
        var users = Enumerable.Range(0, options.HotUsers).Select(i => $"b{_run}-h{i}").ToArray();
        var (tokens, minted) = await MintAsync(users);

        var stopwatch = Stopwatch.StartNew();
        var outcomes = await StartGate.RunAsync(users.Length, i =>
            client.ReserveAsync(showId, users[i], tokens[i], ["A1"], $"hot-{i}", options.KeyPlacementFor(i)));
        stopwatch.Stop();

        var failures = new List<string>();
        Expect(failures, outcomes.Count(o => o.Status == 201) == 1, $"expected exactly 1×201, got {outcomes.Count(o => o.Status == 201)}");
        var others = outcomes.Where(o => o.Status != 201).ToArray();
        Expect(failures, others.All(o => o is { Status: 409, Code: "seat_taken" }),
            $"every other response should be 409 seat_taken; got {Describe(others.Where(o => o.Code != "seat_taken"))}");
        GateZero5xx(failures, outcomes);

        var show = await client.GetShowAsync(showId);
        Expect(failures, show.Reconciles, $"show doesn't reconcile: {show.Available}+{show.Held}+{show.Confirmed} != {show.Total}");
        Expect(failures, show.Confirmed == 1 && show.SeatStatus.GetValueOrDefault("A1") == "confirmed",
            $"show state should have only A1 confirmed; confirmed={show.Confirmed}, A1={show.SeatStatus.GetValueOrDefault("A1")}");

        var lines = new List<string> { minted, $"reconciliation: {show.Available}+{show.Held}+{show.Confirmed} == {show.Total}", $"show {showId}" };
        return (new ScenarioResult("hot", $"hot-seat storm: {users.Length} users -> A1", outcomes, failures, stopwatch.Elapsed), lines);
    }

    public async Task<(ScenarioResult Result, List<string> Lines)> MixedStormAsync()
    {
        var random = new Random(Environment.TickCount);
        var labels = Enumerable.Range(1, options.MixedSeats).Select(i => $"S{i}").ToArray();
        var hot = labels[..HotSeatCount];
        var cold = labels[HotSeatCount..];
        var showId = await client.CreateShowAsync($"burst-mixed-{_run}", labels, PerUserLimit);
        var users = Enumerable.Range(0, options.MixedUsers).Select(i => $"b{_run}-m{i}").ToArray();
        var (tokens, minted) = await MintAsync(users);

        // 90% originals with their own key; 10% are retries of an original (same user, seats and key) racing it.
        var requests = new (int User, string[] Seats, string Key)[options.MixedRequests];
        var originals = 0;
        for (var i = 0; i < requests.Length; i++)
        {
            if (originals > 0 && random.NextDouble() < 0.10)
            {
                requests[i] = requests[random.Next(originals)];
                continue;
            }

            var size = random.NextDouble() < 0.70 ? 1 : random.Next(2, 4);
            var pool = random.NextDouble() < 0.30 ? hot : cold;
            requests[i] = (random.Next(users.Length), pool.OrderBy(_ => random.Next()).Take(size).ToArray(), $"mixed-{i}");
            (requests[originals], requests[i]) = (requests[i], requests[originals]);   // originals stay at the front for retries to copy
            originals++;
        }

        requests = requests.OrderBy(_ => random.Next()).ToArray();   // retries interleave with the rest

        var stopwatch = Stopwatch.StartNew();
        var outcomes = await StartGate.RunAsync(requests.Length, i =>
            client.ReserveAsync(showId, users[requests[i].User], tokens[requests[i].User], requests[i].Seats, requests[i].Key, options.KeyPlacementFor(i)));
        stopwatch.Stop();

        var failures = new List<string>();
        GateZero5xx(failures, outcomes);

        // Identical retries can't conflict, so a key conflict here is a bug like any other unexpected answer.
        var unexpected = outcomes.Where(o => !o.IsTransportError && !o.Is5xx && o is not ({ Status: 201 } or { Status: 200, Replayed: true }
            or { Status: 409, Code: "seat_taken" or "per_user_limit" })).ToArray();
        Expect(failures, unexpected.Length == 0, $"unexpected responses: {Describe(unexpected)}");

        // Each reservation: exactly one 201, its replays agree on the seats, and no seat belongs to two reservations.
        var reservations = outcomes.Where(o => o.Status is 200 or 201 && o.ReservationId is not null).GroupBy(o => o.ReservationId!.Value).ToArray();
        foreach (var r in reservations)
        {
            Expect(failures, r.Count(o => o.Status == 201) == 1, $"reservation {r.Key} has {r.Count(o => o.Status == 201)} creations");
            Expect(failures, r.Select(o => string.Join(',', o.Seats)).Distinct().Count() == 1, $"reservation {r.Key} came back with different seats");
            Expect(failures, r.Select(o => o.UserId).Distinct().Count() == 1, $"reservation {r.Key} came back for different users");
        }

        var wonSeats = reservations.SelectMany(r => r.First().Seats).ToArray();
        var doubled = wonSeats.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Expect(failures, doubled.Length == 0, $"seats in two reservations: {string.Join(", ", doubled.Take(10))}");

        var perUser = reservations.GroupBy(r => r.First().UserId).Select(g => (User: g.Key, Seats: g.Sum(r => r.First().Seats.Length))).ToArray();
        var overLimit = perUser.Where(u => u.Seats > PerUserLimit).ToArray();
        Expect(failures, overLimit.Length == 0, $"users over the limit of {PerUserLimit}: {string.Join(", ", overLimit.Take(10).Select(u => $"{u.User}={u.Seats}"))}");

        var show = await client.GetShowAsync(showId);
        var confirmedLabels = show.SeatStatus.Where(s => s.Value == "confirmed").Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        Expect(failures, show.Reconciles && show.Total == labels.Length, $"show doesn't reconcile: {show.Available}+{show.Held}+{show.Confirmed} != {show.Total}");
        // A client that timed out may still have won: the server finishes the transaction regardless (D-042). Then the
        // show has more confirmed seats than the responses account for. It still fails (the gate wants no timeouts), but
        // it isn't double-booking, so say which it is.
        var timeouts = outcomes.Count(o => o.IsTransportError);
        var unaccounted = confirmedLabels.Except(wonSeats).Count();
        var missing = wonSeats.Distinct().Count(s => !confirmedLabels.Contains(s));
        Expect(failures, confirmedLabels.SetEquals(wonSeats),
            $"confirmed seats ({confirmedLabels.Count}) differ from the seats in successful responses ({wonSeats.Distinct().Count()}): " +
            (missing == 0 && unaccounted > 0 && timeouts > 0
                ? $"{unaccounted} extra confirmed seats, consistent with commits by some of the {timeouts} timed-out requests"
                : $"{missing} won seats not confirmed, {unaccounted} confirmed seats not won"));

        var lines = new List<string>
        {
            minted,
            $"reservations: {reservations.Length}, seats confirmed: {wonSeats.Length} of {labels.Length}, users at the limit: {perUser.Count(u => u.Seats == PerUserLimit)}",
            $"reconciliation: {show.Available}+{show.Held}+{show.Confirmed} == {show.Total}",
            $"show {showId}",
        };
        return (new ScenarioResult("mixed", $"mixed storm: {requests.Length} requests, {users.Length} users, {labels.Length} seats", outcomes, failures, stopwatch.Elapsed), lines);
    }

    private async Task<(string[] Tokens, string Line)> MintAsync(IReadOnlyList<string> users)
    {
        var stopwatch = Stopwatch.StartNew();
        var tokens = await client.MintTokensAsync(users);
        return (tokens, $"tokens: {tokens.Length} minted in {stopwatch.Elapsed.TotalSeconds:0.0}s ({BurstOptions.MintParallelism} in flight, outside the timed window)");
    }

    /// <summary>D-088: any 5xx or transport error fails the run, whatever else happened.</summary>
    private static void GateZero5xx(List<string> failures, Outcome[] outcomes)
    {
        Expect(failures, !outcomes.Any(o => o.Is5xx), $"5xx responses: {Describe(outcomes.Where(o => o.Is5xx))}");
        Expect(failures, !outcomes.Any(o => o.IsTransportError), $"transport errors: {Describe(outcomes.Where(o => o.IsTransportError))}");
    }

    private static void Expect(List<string> failures, bool condition, string message)
    {
        if (!condition)
        {
            failures.Add(message);
        }
    }

    private static string Describe(IEnumerable<Outcome> outcomes) =>
        string.Join(", ", Report.Buckets(outcomes).Select(b => $"{b.Bucket}×{b.Count}"));
}
