# Seat Reservation at Scale: technical write-up

How the service stays correct when thousands of users reserve the same seats at once, how it behaves when things go
wrong, and how you'd run it. The [README](README.md) covers usage; this document covers the reasoning.

## Contents

1. [Preventing double-selling](#1-preventing-double-selling)
2. [Handling concurrent requests](#2-handling-concurrent-requests)
3. [Multi-seat requests](#3-multi-seat-requests)
4. [Per-user limit](#4-per-user-limit)
5. [Idempotency](#5-idempotency)
6. [Same key, different request](#6-same-key-different-request)
7. [Cancellation and release](#7-cancellation-and-release)
8. [Dependency outage](#8-dependency-outage)
9. [Consistency under a network partition (CAP)](#9-consistency-under-a-network-partition-cap)
10. [Metrics and logs](#10-metrics-and-logs)
11. [Operational monitoring: what pages at 2am](#11-operational-monitoring-what-pages-at-2am)
12. [Layering and test strategy](#12-layering-and-test-strategy)
13. [Capacity on the deployed plan](#13-capacity-on-the-deployed-plan)
14. [Future improvements](#14-future-improvements)

## 1. Preventing double-selling

**Postgres is the only source of truth and the only coordination point.** Each seat is a row in `seats`
(`show_id`, `label`, `status`, `user_id`, `reservation_id`). There is no in-memory seat map and no counter column
that could drift.

A reserve locks the requested seat rows with `SELECT … FOR UPDATE` and checks that they are `available` inside the
same transaction that marks them `confirmed`. A competing transaction blocks on the row lock. When the winner commits,
Postgres (READ COMMITTED) makes the waiter re-read the row; it sees `confirmed` and declines with `409 seat_taken`.
The hot-seat storm in the burst tool (500 users, one seat) and the integration test
`HotSeatTests.Two_hundred_users_racing_for_one_seat_produce_exactly_one_winner` both show exactly one `201`.

Defence in depth: the final `UPDATE seats … WHERE status = 'available'` must touch exactly the requested number of
rows or the transaction aborts, and CHECK constraints keep `status`, `user_id` and `reservation_id` consistent. If the
logic were ever wrong, the database would refuse the write rather than sell a seat twice.

**Rejected alternatives.**
- *SERIALIZABLE + retry:* under a 500-way race, serialization failures turn into a retry storm. Row locks give one
  deterministic winner.
- *Optimistic `UPDATE … WHERE status = 'available'` alone:* fine for one seat, but multi-seat all-or-nothing and the
  per-user limit still need coordination.
- *Redis or an in-memory seat map:* a second source of truth that breaks as soon as there are two instances.
- *`SKIP LOCKED` / `NOWAIT`:* they decline a seat whose lock holder may still roll back, so a hot seat could end with
  zero winners.

## 2. Handling concurrent requests

Every reserve takes its locks in one global order: **user lock → seat rows in sorted label order**. Cancel takes
**user lock → reservation row → that reservation's seats**. No transaction takes them in another order, so lock cycles
(deadlocks) can't form between them. A retry for deadlocks and serialization failures still exists as a safety net
(up to 3 attempts with jitter, counted in `db_transaction_retries_total`). `MultiSeatTests.Three_hundred_overlapping_multi_seat_requests_are_exclusive_and_deadlock_free`
checks this.

**Fast path for losers.** Before locking anything, a reserve does one lock-free read of the seats' current owners. If a
seat already belongs to another user, it returns `409 seat_taken` without opening a transaction. In a hot-seat storm
the losers mostly never wait on the row lock, which keeps the pool free for winners. This can only decline, never
grant: a stale read can at worst decline a request that was racing anyway.

**Admission queue.** Only as many requests as there are pool connections (40) run database work at once. The rest wait
in an in-memory FIFO queue (up to 50,000) instead of failing with pool timeouts. Under overload, latency rises before
errors do; the queue length is a metric (`reservation_queue_length`). Health and metrics endpoints bypass the queue, so
the platform's health checks still answer during a burst.

**A client disconnect doesn't abort a commit.** Database work runs on the application-shutdown token, not the request's
abort token, so a client that gives up mid-transaction leaves a cleanly committed or rolled-back transaction, never a
half-finished one. The client can retry with its key to find out which.

## 3. Multi-seat requests

**All or nothing.** The request's seats are locked together (sorted), and if any is unavailable the transaction rolls
back and the response is `409 seat_taken` listing every unavailable seat. The others stay `available`
(`MultiSeatTests.A_request_with_one_taken_seat_reserves_none_of_them`).

Partial fulfilment was rejected: a user asking for seats together usually wants them together, and a partial booking
forces the client to cancel the rest. All-or-nothing keeps the client's retry logic simple.

## 4. Per-user limit

`per_user_limit` is set per show when it's created (default 4, from 1 to 100). The check is "seats the user already
holds in this show + seats requested ≤ limit".

The problem is that two concurrent requests from the same user could both read "2 held" and both add 2. So every
reserve and cancel first takes a **per-user transaction-scoped advisory lock**
(`pg_advisory_xact_lock(1, hashtext(user_id))`). A user's own requests run one after another; different users still
run in parallel. The lock is released at commit or rollback, so it can't leak. A hash collision between two users only
makes them wait for each other; it never affects correctness.

`PerUserLimitTests.Fifty_parallel_single_seat_requests_from_one_user_get_exactly_the_limit` sends 50 parallel requests
and gets exactly 4 successes. Cancelling frees room again
(`Cancelling_one_seat_at_the_limit_frees_room_for_one_more_but_not_two`).

## 5. Idempotency

Every reserve needs a key, in the `Idempotency-Key` header or the `idempotency_key` body field (both are accepted
because clients differ; both with different values is a `400`). Keys belong to a user:
`UNIQUE (user_id, idempotency_key)`, so one user can't collide with or probe another's keys.

The key is stored on the reservation together with a **request hash**: SHA-256 of the show id and the sorted seat
labels. Inside the user's lock, the reserve first looks the key up:
- not found → reserve normally and store the key;
- found with the same hash → return the original reservation as `200` with `Idempotent-Replayed: true`, writing
  nothing.

Concurrent retries of the same request are serialized by the user lock, so the first creates and the rest replay. The
unique constraint is a backstop: if it ever fires, the service re-reads and replays. The burst tool's `idem` scenario
(200 concurrent copies) and `IdempotencyConcurrencyTests` get exactly one reservation.

Only a successful reservation binds a key. A declined attempt (`seat_taken`, `per_user_limit`) stores nothing, so
retrying it with the same key is evaluated afresh. A replay after a cancel returns the cancelled reservation; it does
not book again.

The metrics stay honest: a replay counts as `reservations_declined_total{reason="idempotent_replay"}`, never as a new
confirmation.

## 6. Same key, different request

If the key exists but the hash differs (different show or seats), the response is `409 idempotency_key_conflict` with
the original `reservation_id`. Seat order doesn't matter (the labels are sorted before hashing), so `[A2, A1]` is a
replay of `[A1, A2]`. When two different payloads race with one key, whichever commits first binds the key and every
request for the other payload gets the conflict
(`IdempotencyConcurrencyTests.One_hundred_parallel_requests_with_one_key_and_two_payloads_bind_the_key_to_exactly_one`).

Treating it as a conflict rather than a new reservation is deliberate: a reused key almost always means a client bug,
and silently booking would hide it.

## 7. Cancellation and release

There are no temporary holds: a reserve confirms immediately, and seats are released only by
`POST /reservations/{id}/cancel`. `held` is in the state model and the metrics, ready for a hold-then-pay step, but it
is always 0.

Cancel locks the user, then the reservation row. A non-owner gets `403 not_owner`; an already cancelled reservation
returns `200` with the same body, so a retried cancel is harmless. The seats are released with
`UPDATE seats … WHERE reservation_id = <id>`, keyed by reservation, not by seat label. If the seat has been cancelled
and rebooked by someone else meanwhile, it carries the new reservation's id, so an old or duplicate cancel can never
free it (`CancelRebookTests.A_stale_cancel_after_a_rebook_leaves_the_seat_with_the_new_owner`). Released seats are
immediately `available` and go through the normal reserve path.

## 8. Dependency outage

The only dependency is Postgres.

| Situation | Behaviour |
|---|---|
| Database down at startup | The process starts, `/health/live` is `200`, `/health/ready` is `503`. Migrations retry with backoff for up to 2 minutes. Render sends no traffic until readiness is green. Requests that arrive anyway get `503 not_ready`. |
| Database lost while running | Transient errors (dropped connection, lock timeout, deadlock) are retried up to 3 times. After that, the request gets `503 dependency_unavailable` with `Retry-After`, promptly, never a `500` and never a hang (`DatabaseOutageTests`). Readiness turns `503` and `db_up` drops to 0; liveness stays `200`, so the platform doesn't restart a healthy process for a database problem. |
| Pool exhausted | Can't normally happen because the admission queue caps concurrency at the pool size. If it does, it's a `503 dependency_unavailable`, not a raw driver timeout. |
| Crash mid-transaction | Postgres rolls back and releases every lock. The client retries with its key and gets the right answer. |

Nothing is ever faked: there's no cached "success" and no partial write, because every change is one transaction. A
`503` means nothing changed, so it's always safe to retry with the same key. Readiness and the metrics gauges use a
separate 3-connection pool, so a burst that fills the main pool can't make the health check time out.

On shutdown the service stops accepting new requests and drains in-flight ones for 5 seconds.

## 9. Consistency under a network partition (CAP)

**The service chooses consistency.** If the API can't reach Postgres, it doesn't guess: reserves, cancels and even
`GET /shows/{id}` return `503` until the database is back. The alternative, serving from a local cache or accepting
writes to reconcile later, would let two partitions each sell the same seat, which is the one thing this service
must never do.

What stays available during a partition: liveness, metrics (the seat gauges keep their last values and set
`seats_gauge_stale = 1`), and validation that needs no data (`400`s). Show metadata (name, price, limit, seat labels)
is immutable once created and cached in process, but the cache is only used to validate input, never to decide who
owns a seat.

With several API instances, all coordination is still in Postgres, so a partition between instances doesn't matter;
only the API–database link does. A replicated database would have to commit synchronously to keep this guarantee, at
the cost of availability when the replica is unreachable.

## 10. Metrics and logs

**Metrics** (`GET /metrics`, Prometheus; the [README's metrics guide](README.md#metrics-guide) has the catalog and
queries):
- HTTP: requests by `code`, `method`, `endpoint`; latency histograms; in flight.
- Domain: `reservations_confirmed_total`, `reservations_declined_total{reason}`, `reservations_cancelled_total`,
  `reservation_queue_length`.
- Seats: `show_seats{show_id,state}` per show and `seats_*` totals, read from the database at scrape time (cached 1 s),
  so they always match `GET /shows/{id}` and survive restarts.
- Database: `db_up`, query latency per operation, errors by kind, retries, pool usage.

Labels are low-cardinality: never user, reservation or request ids. The one exception is `show_id` on the seat gauges,
limited to the 200 most recent shows. Counters are per process, so the service runs as a single instance and
`/metrics` reconciles exactly with the API; the burst tool checks this after every run (7/7 shows matched on the live
service).

**Logs:** structured JSON on stdout, one object per line, through a non-blocking buffered sink so logging never slows
a request. Each request produces one `http.request` line with the correlation id, route, status, latency and the
domain `Outcome` (`created`, `replayed`, `seat_taken`, …), plus `ReservationId`, `UserId` and `ShowId`. Domain events
(`reservation.confirmed`, `reservation.cancelled`, `show.created`, `db.retry`, `db.unavailable`,
`invariant.violation`, `unhandled.exception`) carry the same correlation id, which is also returned in the
`X-Correlation-ID` header and in every error body. Tokens, the `Authorization` header and raw idempotency keys are
never logged (`LogRedactionTests`). An excerpt of the live service's logs during a burst is linked from the README.

## 11. Operational monitoring: what pages at 2am

Page only for things a human must act on now:

| Alert | Condition (sketch) | Why it matters |
|---|---|---|
| **5xx from the API** | `sum(rate(http_requests_received_total{code=~"5.."}[5m])) > 0` for 5 min | A lost race is a `409`, never a `5xx`; a `500` is a bug and a `503` means the database or capacity is failing. |
| **Database down** | `db_up == 0` for 1 min, or readiness failing | Every reserve is failing. |
| **Seat invariant broken** | any `invariant.violation` log, or `sum by (show_id) (show_seats) - on (show_id) show_seats_total != 0` | Correctness is at risk; stop and investigate. Should never fire. |
| **Reserve p99 latency** | p99 of `shows/{id}/reserve` > 2 s for 10 min | Users are waiting; usually the queue below. |
| **Queue / pool saturation** | `reservation_queue_length` rising for 5 min, or `npgsql_db_client_connections_pending_requests > 0` | Demand is above capacity; the next step is timeouts. Scale up. |

Ticket, don't page: database lock waits and retries (`db_transaction_retries_total`, `db_errors_total{kind="transient"}`)
climbing, connection count approaching Postgres `max_connections`, `seats_gauge_stale == 1`, and growth in
`reservations_declined_total{reason="per_user_limit"}` (possible abuse).

Not alerts at all: `seat_taken` spikes. During a sale most attempts lose; that is the system working.

## 12. Layering and test strategy

Three projects, one per layer, with dependencies enforced by project references:

| Layer | Project | Responsibility |
|---|---|---|
| Controller | `SeatReservation.Api` | HTTP only: binding, the user from the JWT, outcome → status code, auth, admission queue, metrics and logging adapters |
| Logic | `SeatReservation.Application` | Every business rule and the order of steps inside each transaction; talks to storage through interfaces |
| Repository | `SeatReservation.Infrastructure` | All SQL and locking statements, transactions and retries, error translation, migrations |

The logic layer references no ASP.NET Core, Npgsql, Dapper, Prometheus or Serilog; `ArchitectureTests` fails the
build if it ever does. The repositories make no business decisions: they return rows and the service decides. This
keeps each rule in one place and makes it easy to extend live.

**Tests follow the layers.**
- **Unit tests** (`SeatReservation.UnitTests`, logic layer only, in-memory fakes, no Docker, seconds): every decision
  and the exact call order inside a transaction (user lock → key → count → seat lock → insert → confirm), sorted
  labels, replay vs conflict, metrics recorded once even when a transaction is retried.
- **Integration tests** (`SeatReservation.IntegrationTests`, real Postgres 16 via Testcontainers, real HTTP via
  `WebApplicationFactory`): status codes and problem bodies, SQL and locking behaviour, and concurrency tests that race
  real requests: 200 users on one seat, 100 parallel retries of one key, 300 overlapping multi-seat requests, a cancel
  racing 100 reserves, snapshots reconciling during a burst. Also health, outage, migrations, metrics, log redaction,
  admission queue, graceful shutdown.
- **Load:** the burst tool (`tools/Burst`, `./burst.sh <url>`) runs six scenarios against a deployed service and
  checks invariants, metric reconciliation and the zero-5xx gate.

Unit tests can prove decisions and ordering but not database locking; that's what the concurrency integration tests
are for. CI runs both suites on every push to `main` and every pull request.

## 13. Capacity on the deployed plan

The live service runs on Render's **free** plan: one web instance with about 0.1 CPU and 512 MB, plus free Postgres 16,
in singapore. That was a cost decision, and it decides the load-test result.

**Result of the 20k burst on 2026-10-03** ([report](docs/burst-report-2026-10-03.txt); a final re-run after all
changes, [docs/burst-report-final.txt](docs/burst-report-final.txt), shows the same picture):

| Check | Result |
|---|---|
| Hot seat: 500 users race for one seat | exactly 1×201, 499×409 `seat_taken` |
| Double booking in the 20k mixed storm | none (0 seats in two reservations) |
| Per-user limit | held (max 4 seats per user, limit 4) |
| Idempotent replay / key conflict / cancel and rebook | as specified |
| `/metrics` per-show gauges vs `GET /shows/{id}` | 7 of 7 shows match |
| Zero 5xx and zero transport errors in the 20k storm | **FAIL**: 15,432 client timeouts at 60 s, 26×502 from Render's proxy |

**Why it fails.** The instance answers about 70 reserve requests per second. The same ceiling showed up in every
free-plan run, with or without the admission queue and runtime tuning, so the tenth of a CPU is the likely limit. CPU
wasn't measured: the free plan has no metrics tab. 20,000 requests inside a 60 s client timeout need about 330 req/s.
The admission queue bounds the database work and keeps excess requests waiting instead of failing them. So no 5xx in
the report came from the API, but most requests waited past the client's timeout. Render's edge proxy returned a
non-JSON `502` for 26 connections while the instance was saturated.

**What a timeout means for the client.** A timed-out request may still have committed: the storm confirmed 928 seats
against 915 seen by the client. This is why every reserve carries an idempotency key. Retrying with the same key returns
the committed reservation (`200`, `Idempotent-Replayed: true`) instead of booking twice.

**How to pass the gate.** Scale up, not out. The counters are per process, so the service runs one instance. A
Standard instance (1 CPU, 2 GB) is the recommended evaluation size. Tuning pool and permit sizes, or batching the
locked path's queries, can't make up the roughly 5× gap on a tenth of a CPU.

## 14. Future improvements

- **Capacity:** a Standard instance for the zero-5xx gate; then PgBouncer and several instances, with counters summed
  in Prometheus (the database-backed seat gauges already work with any number of instances).
- **Holds with expiry:** a `held` state with a TTL for a hold-then-pay flow, released by a background sweep; the state
  model and metrics already have the slot.
- **Real identity and admin role:** an OIDC provider instead of the demo login (the service only reads `sub`), and an
  admin role for `POST /shows`.
- **Per-user rate limiting** in front of the admission queue, so one client can't fill it.
- **Idempotency key retention:** keys live as long as their reservation; a TTL or archive would bound the table.
- **Tracing:** OpenTelemetry spans through the database calls, linked to the existing correlation ids.
- **Payments and events:** an outbox table for reservation events, so payment or notification services can consume
  them reliably.
- **Bigger venues:** partition `seats` by show, and serve `GET /shows/{id}` from a read replica.
