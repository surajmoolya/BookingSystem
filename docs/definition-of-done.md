# Definition of Done audit

Audited on 2026-10-03 at commit `76c4c06`. Each item links to its evidence. Test names refer to
`tests/SeatReservation.IntegrationTests` (classes under `Http/`, `Concurrency/`, `Persistence/`) unless noted.

**Summary: 18 of 20 met; 2 not met because of the hosting plan.** The service is correct under concurrency on the live
deployment, but the Render free plan can't serve the 20k burst inside the 60 s client timeout. Both gaps are capacity,
not correctness, and are explained in [WRITEUP §13](../WRITEUP.md#13-capacity-on-the-deployed-plan).

## Fresh-checkout check

A clean clone of `76c4c06`, run on the author's Windows machine:

- `docker compose up --build`: database healthy, API `/health/ready` → `200` within seconds, on an empty database.
- `./burst.sh http://127.0.0.1:8080` with every default (6 scenarios, 21,005 requests): **OVERALL PASS**.
  Hot seat 1×201; mixed storm 866 reservations, 0 seats in two reservations, max 4 seats per user, confirmed 1000 ==
  won 1000; 7/7 per-show gauges match the API; **0×5xx, 0 transport errors**. Latency p50/p99 30.2 s / 57.2 s, close
  to the 60 s timeout on a laptop.
- Unit tests 214/214 and integration tests 343/343 green on the same tree.

## Checklist

| # | Item | Status | Evidence |
|---|---|---|---|
| 1 | API works (auth, create/get show, reserve, cancel, get reservation) | ✅ | Every [README](../README.md#api) curl example run against the service; `AuthApiTests`, `ShowsApiTests`, `ReserveApiTests`, `CancellationApiTests` |
| 2 | Publicly deployed on Render, URL in README, exactly one instance on the evaluation plan, deployed since M1b | ⚠️ partly | Live at https://seatres-api.onrender.com since 2026-10-02, one instance (`numInstances: 1` in [render.yaml](../render.yaml)). **Not on the evaluation plan:** it runs on the free plan by the owner's choice (cost). It sleeps when idle; a `keep-alive` workflow pings it, but the audit's first request still took 13.5 s |
| 3 | Docker works from a clean checkout | ✅ | [Fresh-checkout check](#fresh-checkout-check) above; CI builds the image on every push |
| 4 | Health endpoints work; readiness verifies DB and migrations | ✅ | `HealthApiTests.Ready_200_with_db`, `…Ready_503_when_db_unreachable_and_live_stays_200`, `…Ready_503_when_db_up_but_migrations_not_done`, `…Ready_turns_503_when_db_goes_away_and_200_when_it_returns_while_live_stays_200`; live `/health/ready` → `200` |
| 5 | Metrics exposed, covering every addendum question | ✅ | `MetricsApiTests.Every_required_metric_is_exported` and 12 more; [README metrics guide](../README.md#metrics-guide) maps each question to a query, all checked against a real Prometheus |
| 6 | Per-show `show_seats` equals `GET /shows/{id}` for every burst show | ✅ | Burst final reconciliation 7/7 on the live service ([report](burst-report-final.txt)) and on the fresh checkout; `SeatGaugeTests.Per_show_gauges_equal_the_show_counts_after_reserves_and_a_cancel` |
| 7 | Structured logs with correlation ids, recording linked | ✅ | JSON logs, one line per request: `RequestLoggingTests`, `CorrelationIdTests`, `LogRedactionTests`. The live log evidence is a document of verbatim Render log lines captured during a burst, linked from the [README](../README.md#log-evidence), in place of a screen recording |
| 8 | Concurrency correct; hot seat gives exactly one winner | ✅ | `HotSeatTests.Two_hundred_users_racing_for_one_seat_produce_exactly_one_winner`; live burst hot seat 1×201 + 499×409; 0 seats in two reservations in every mixed storm |
| 9 | Per-user limits hold under concurrency, including a non-default `per_user_limit` | ✅ | `PerUserLimitTests.Fifty_parallel_single_seat_requests_from_one_user_get_exactly_the_limit` (limits 4 and 2); `ShowsApiTests.Custom_per_user_limit_is_stored_and_returned`; burst `limit` scenario (4, then 2) PASS live |
| 10 | Idempotency correct; same key, different request → 409 | ✅ | `IdempotencyConcurrencyTests` (100 parallel retries → one reservation; two payloads → key bound to one); `IdempotencyApiTests.The_same_key_with_different_seats_is_a_409_conflict_naming_the_original`; burst `conflict` PASS live |
| 11 | Key from header and body; mismatch → 400 | ✅ | `ReserveApiTests.Key_in_the_header_only_is_accepted`, `…Key_in_the_body_only_is_accepted`, `…Key_in_both_places_with_different_values_is_a_400_and_reserves_nothing`; burst `idem` sends half each way plus a mismatch probe |
| 12 | Multi-seat all-or-nothing, defined and correct | ✅ | Defined in [README](../README.md#reservation-rules) and [WRITEUP §3](../WRITEUP.md#3-multi-seat-requests); `MultiSeatTests.A_request_with_one_taken_seat_reserves_none_of_them`, `…Three_hundred_overlapping_multi_seat_requests_are_exclusive_and_deadlock_free` |
| 13 | Cancellation correct; a stale cancel never frees a rebooked seat | ✅ | `CancelRebookTests.A_stale_cancel_after_a_rebook_leaves_the_seat_with_the_new_owner`, `…A_cancel_racing_a_hundred_reserves_for_its_seat_frees_it_for_at_most_one_of_them`; `CancellationApiTests`; burst `cancel` PASS live |
| 14 | Seat counts reconcile during and after the burst | ✅ | `ReconciliationTests.Every_snapshot_taken_during_a_reserve_burst_reconciles`; every burst scenario's `available+held+confirmed == total` |
| 15 | Identity only from authentication | ✅ | `ReserveApiTests.A_user_id_in_the_body_is_ignored_in_favour_of_the_token`; `CancellationApiTests.Non_owner_cancel_is_403_and_changes_nothing`; bad, tampered and expired tokens → 401 |
| 16 | Automated tests exist and pass in CI | ✅ | [ci.yml](../.github/workflows/ci.yml) runs both suites on every push and pull request; green on every recent run. Locally 214 unit + 343 integration |
| 17 | Burst passes against the public URL with 0×5xx and 0 transport errors | ❌ | **Fails on the free plan** ([final report](burst-report-final.txt)): every correctness scenario passes, but the 20k mixed storm had 14,708 client timeouts and 1 proxy `502`. The same burst passes against a local deployment (above). Needs a larger instance; see [WRITEUP §13](../WRITEUP.md#13-capacity-on-the-deployed-plan) |
| 18 | README complete | ✅ | [README](../README.md): run locally, tests, API with curl, rules (multi-seat, release), errors, health, metrics guide, burst, architecture, deployed URL |
| 19 | WRITEUP complete, honest AI usage | ✅ | [WRITEUP](../WRITEUP.md): the 12 topics plus CAP and alerting; [§15 AI usage disclosure](../WRITEUP.md#15-ai-usage-disclosure) |
| 20 | Meaningful, unsquashed commit history | ✅ | 89 Conventional Commits (`feat`, `test`, `docs`, `perf`, `fix`, `ci`, `chore`), one per task |
