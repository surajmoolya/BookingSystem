using System.Diagnostics;

namespace Burst;

/// <summary>
/// The lld §12 scenarios. Each creates its own show, mints its
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
        var winner = outcomes.FirstOrDefault(o => o.Status == 201);
        Expect(failures, winner is null || (winner.ReservationId is not null && winner.Seats.SequenceEqual(["A1"])),
            $"the winning response should carry a reservation id for exactly [A1]; got [{string.Join(",", winner?.Seats ?? [])}]");
        var others = outcomes.Where(o => o.Status != 201).ToArray();
        Expect(failures, others.All(o => o is { Status: 409, Code: "seat_taken" }),
            $"every other response should be 409 seat_taken; got {Describe(others.Where(o => o.Code != "seat_taken"))}");
        GateZero5xx(failures, outcomes);

        var show = await client.GetShowAsync(showId);
        Expect(failures, show.Reconciles, $"show doesn't reconcile: {show.Available}+{show.Held}+{show.Confirmed} != {show.Total}");
        Expect(failures, show.Confirmed == 1 && show.SeatStatus.GetValueOrDefault("A1") == "confirmed",
            $"show state should have only A1 confirmed; confirmed={show.Confirmed}, A1={show.SeatStatus.GetValueOrDefault("A1")}");

        var lines = new List<string> { minted, $"winner: {winner?.UserId ?? "none"} ({winner?.ReservationId})", $"reconciliation: {show.Available}+{show.Held}+{show.Confirmed} == {show.Total}", $"show {showId}" };
        return (new ScenarioResult("hot", $"hot-seat storm: {users.Length} users -> A1", outcomes, failures, stopwatch.Elapsed), lines);
    }

    /// <summary>
    /// lld §12 #2: one user sends the same key and seat <c>idem-requests</c> times at once, the key alternating between the
    /// header and the body (D-084) whatever <c>--key-in</c> says. Then a header/body mismatch probe must be a 400.
    /// </summary>
    public async Task<(ScenarioResult Result, List<string> Lines)> IdempotentRetriesAsync()
    {
        var showId = await client.CreateShowAsync($"burst-idem-{_run}", ["X1", "X2", "X3"], PerUserLimit);
        var user = $"b{_run}-idem";
        var (tokens, minted) = await MintAsync([user]);
        var key = $"idem-{_run}";

        var stopwatch = Stopwatch.StartNew();
        var outcomes = await StartGate.RunAsync(options.IdemRequests, i =>
            client.ReserveAsync(showId, user, tokens[0], ["X1"], key, i % 2 == 0 ? KeyPlacement.Header : KeyPlacement.Body));
        stopwatch.Stop();

        var failures = new List<string>();
        GateZero5xx(failures, outcomes);
        var created = outcomes.Count(o => o.Status == 201);
        Expect(failures, created == 1, $"expected exactly 1×201, got {created}");
        var notReplays = outcomes.Where(o => o.Status != 201 && o is not { Status: 200, Replayed: true }).ToArray();
        Expect(failures, notReplays.Length == 0, $"every other response should be a 200 replay; got {Describe(notReplays)}");
        var ids = outcomes.Where(o => o.Status is 200 or 201).Select(o => o.ReservationId).Distinct().ToArray();
        Expect(failures, ids.Length == 1 && ids[0] is not null, $"2xx responses should share one reservation_id; got {ids.Length} distinct");
        Expect(failures, outcomes.Where(o => o.Status is 200 or 201).All(o => o.Seats.SequenceEqual(["X1"])), "a 2xx response named seats other than [X1]");

        // The same key in the header and a different one in the body: neither may be picked, it's a 400 (D-084).
        var probe = await client.ReserveAsync(showId, user, tokens[0], ["X2"], headerKey: key, bodyKey: key + "-other");
        Expect(failures, probe is { Status: 400, Code: "validation" }, $"header/body key mismatch should be 400 validation; got {probe.Bucket}");

        var show = await client.GetShowAsync(showId);
        Expect(failures, show.Reconciles, $"show doesn't reconcile: {show.Available}+{show.Held}+{show.Confirmed} != {show.Total}");
        Expect(failures, show.Confirmed == 1 && show.SeatStatus.GetValueOrDefault("X1") == "confirmed",
            $"the user should hold exactly X1; confirmed={show.Confirmed}, X1={show.SeatStatus.GetValueOrDefault("X1")}");

        var lines = new List<string>
        {
            minted,
            $"key in header/body: {(options.IdemRequests + 1) / 2}/{options.IdemRequests / 2}   reservation: {ids.FirstOrDefault()}",
            $"header/body mismatch probe: {probe.Bucket}",
            $"reconciliation: {show.Available}+{show.Held}+{show.Confirmed} == {show.Total}",
            $"show {showId}",
        };
        return (new ScenarioResult("idem", $"idempotent retries: {options.IdemRequests} × one key -> X1", outcomes, failures, stopwatch.Elapsed), lines);
    }

    /// <summary>
    /// lld §12 #3: one user, one key, half the requests for X and half for Y, all at once. Exactly one reservation wins;
    /// the requests for the other seat get 409 <c>idempotency_key_conflict</c>. Then reusing the key for new seats is a 409 too.
    /// </summary>
    public async Task<(ScenarioResult Result, List<string> Lines)> KeyConflictAsync()
    {
        var showId = await client.CreateShowAsync($"burst-conflict-{_run}", ["X", "Y", "Z"], PerUserLimit);
        var user = $"b{_run}-conflict";
        var (tokens, minted) = await MintAsync([user]);
        var key = $"conflict-{_run}";
        static string SeatFor(int i) => i % 2 == 0 ? "X" : "Y";

        var stopwatch = Stopwatch.StartNew();
        var outcomes = await StartGate.RunAsync(options.IdemRequests, i =>
            client.ReserveAsync(showId, user, tokens[0], [SeatFor(i)], key, options.KeyPlacementFor(i)));
        stopwatch.Stop();

        var failures = new List<string>();
        GateZero5xx(failures, outcomes);
        var created = outcomes.Where(o => o.Status == 201).ToArray();
        Expect(failures, created.Length == 1, $"expected exactly 1×201, got {created.Length}");
        var won = created.FirstOrDefault()?.Seats.FirstOrDefault();
        var ids = outcomes.Where(o => o.Status is 200 or 201).Select(o => o.ReservationId).Distinct().ToArray();
        Expect(failures, ids.Length == 1, $"2xx responses should share one reservation_id; got {ids.Length} distinct");

        // Same seat as the winner → created or replayed; the other seat → key conflict. Nothing else.
        var wrong = outcomes
            .Where((o, i) => SeatFor(i) == won
                ? o is not ({ Status: 201 } or { Status: 200, Replayed: true })
                : o is not { Status: 409, Code: "idempotency_key_conflict" })
            .ToArray();
        Expect(failures, won is not null && wrong.Length == 0,
            $"requests for {won ?? "the winning seat"} should be 201/200 replays and the rest 409 idempotency_key_conflict; got {Describe(wrong)}");

        var reuse = await client.ReserveAsync(showId, user, tokens[0], ["Z"], key, KeyPlacement.Header);
        Expect(failures, reuse is { Status: 409, Code: "idempotency_key_conflict" },
            $"sequential reuse with new seats should be 409 idempotency_key_conflict; got {reuse.Bucket}");

        var show = await client.GetShowAsync(showId);
        Expect(failures, show.Reconciles, $"show doesn't reconcile: {show.Available}+{show.Held}+{show.Confirmed} != {show.Total}");
        Expect(failures, show.Confirmed == 1 && won is not null && show.SeatStatus.GetValueOrDefault(won) == "confirmed",
            $"exactly the winning seat should be confirmed; confirmed={show.Confirmed}");

        var lines = new List<string>
        {
            minted,
            $"winning seat: {won ?? "none"}   reservation: {ids.FirstOrDefault()}",
            $"sequential reuse with [Z]: {reuse.Bucket}",
            $"reconciliation: {show.Available}+{show.Held}+{show.Confirmed} == {show.Total}",
            $"show {showId}",
        };
        return (new ScenarioResult("conflict", $"same key, different request: {options.IdemRequests} × one key -> X | Y", outcomes, failures, stopwatch.Elapsed), lines);
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
        string.Join(", ", Stats.Buckets(outcomes).Select(b => $"{b.Bucket}×{b.Count}"));
}
