# Seat Reservation at Scale

A seat-reservation service in C# / .NET 8 on PostgreSQL 16. Many users race for the same seats; the service sells each
seat at most once, keeps a per-user limit per show, makes retries safe with idempotency keys, and exposes Prometheus
metrics that reconcile with the API.

- **Live:** https://seatres-api.onrender.com (Render, region singapore, one instance)
- **Design write-up:** [WRITEUP.md](WRITEUP.md)

## Contents

- [Live service](#live-service)
- [Run locally](#run-locally)
- [Build and test](#build-and-test)
- [API](#api)
- [Reservation rules](#reservation-rules)
- [Error codes](#error-codes)
- [Health](#health)
- [Metrics guide](#metrics-guide)
- [Burst tool](#burst-tool)
- [Architecture](#architecture)
- [Load test results (free plan)](#load-test-results-free-plan)
- [Log evidence](#log-evidence)

## Live service

Base URL: **https://seatres-api.onrender.com**

The service runs on Render's free plan. After ~15 minutes without traffic it spins down, and the first request then
waits for a cold start (up to about a minute). The `keep-alive` workflow (`.github/workflows/keep-alive.yml`) pings
`/health/live` every 5 minutes to prevent that, but GitHub may delay scheduled runs, so hit `/health/ready` once
before testing:

```bash
curl -s https://seatres-api.onrender.com/health/ready
```

Every example in [API](#api) works against the live URL: set `BASE=https://seatres-api.onrender.com`.

## Run locally

### Dependencies

| What | Needed for |
|---|---|
| Docker with Compose | `docker compose up` (API + Postgres), the integration tests (Testcontainers), the burst tool without the SDK |
| .NET 8 SDK | `dotnet run`, `dotnet test`, the burst tool without Docker |
| bash, curl, jq | `burst.sh` and the curl examples below (Git Bash works on Windows) |
| make (optional) | the `Makefile` shortcuts |

### Option 1: everything in Docker

```bash
git clone https://github.com/surajmoolya/BookingSystem.git
cd BookingSystem
docker compose up --build
```

This starts Postgres 16 and the API on port 8080. The API applies its database migrations on startup;
`/health/ready` turns `200` once they are done:

```bash
curl -s http://localhost:8080/health/ready
```

On some Windows machines `localhost` resolves to IPv6 first and the published port doesn't answer there; use
`http://127.0.0.1:8080` instead.

`make up` / `make down` / `make logs` do the same in the background.

### Option 2: `dotnet run` against the compose database

```bash
docker compose up -d db
dotnet run --project src/SeatReservation.Api
```

The `http` launch profile listens on http://localhost:8080 with `ASPNETCORE_ENVIRONMENT=Development`, which supplies a
development signing key. The default connection string is
`Host=localhost;Port=5432;Database=seatres;Username=seatres;Password=seatres` (the compose database). To point it
elsewhere, set `ConnectionStrings__Postgres` (key/value form) or `DATABASE_URL` (`postgres://user:pass@host:port/db`,
which wins when both are set).

### Configuration

The defaults live in `src/SeatReservation.Api/appsettings.json`; any key can be overridden with an environment
variable (`Section__Key`). The ones you are most likely to touch:

| Key | Default | Meaning |
|---|---|---|
| `Auth__SigningKey` | dev key in Development only | HMAC key for the JWTs. Required outside Development (the service refuses to start without one, and refuses the committed dev key) |
| `Database__MaxPoolSize` | `40` | Connection pool size; also the number of requests allowed into the database at once (the rest queue) |
| `Admission__QueueLimit` | `50000` | Requests that may wait for a database slot before getting `503 overloaded` |
| `Reservations__DefaultPerUserLimit` | `4` | Per-user limit when a show is created without `per_user_limit` |
| `Reservations__MaxSeatsPerRequest` | `20` | Most seats in one reserve request |
| `Shows__RequireAuth` | `false` | When `true`, `POST /shows` needs a bearer token |
| `Shows__MaxSeats` | `10000` | Most seats in one show |

## Build and test

```bash
dotnet build SeatReservation.sln
dotnet test tests/SeatReservation.UnitTests
dotnet test tests/SeatReservation.IntegrationTests --filter "Category!=Load"
```

| Suite | What it covers | Needs |
|---|---|---|
| `SeatReservation.UnitTests` | The logic layer only (`SeatReservation.Application`): reservation and cancel rules, validation, idempotency hashing, outcomes. It references no database or web code. | nothing; runs in seconds |
| `SeatReservation.IntegrationTests` | Controllers through real HTTP (`WebApplicationFactory`) and repositories against a real Postgres 16 started by Testcontainers: status codes, problem bodies, locking under concurrency, migrations, health, metrics, admission queue. | Docker running |

`Category!=Load` is the CI filter: tests tagged `[Trait("Category","Load")]` are kept out of normal runs. There are none
at the moment; load is tested against the deployed service with the [burst tool](#burst-tool) instead.
`make test`, `make test-unit` and `make test-integration` run the same commands. CI (`.github/workflows/ci.yml`) builds,
runs both suites and builds the Docker image on every push to `main` and every pull request.

## API

All request and response bodies are JSON with snake_case names. Errors are `application/problem+json`
([error codes](#error-codes)). Every response carries an `X-Correlation-ID` header, which is also in the logs.

| Method and path | Auth | Purpose |
|---|---|---|
| `POST /auth/token` | none | Demo login: get a bearer token for a username |
| `POST /shows` | admin (see below) | Create a show with its seats, price and per-user limit |
| `GET /shows/{id}` | none | Show details, seat counts and every seat's status |
| `POST /shows/{id}/reserve` | bearer token | Reserve one or more seats (all-or-nothing, idempotent) |
| `GET /reservations/{id}` | bearer token, owner | Read your reservation |
| `POST /reservations/{id}/cancel` | bearer token, owner | Cancel your reservation and release its seats |
| `GET /health/live`, `GET /health/ready` | none | Liveness and readiness |
| `GET /metrics` | none | Prometheus metrics |

The examples use `curl` and `jq`:

```bash
BASE=http://localhost:8080        # or https://seatres-api.onrender.com
```

### 1. Get a token: `POST /auth/token`

This is a demo login: any username of 1–64 characters from `A-Z a-z 0-9 _ . -` gets a token, and the username becomes
the user id. There is no password; it stands in for a real identity provider.

```bash
TOKEN=$(curl -s -X POST "$BASE/auth/token" \
  -H 'Content-Type: application/json' \
  -d '{"username":"alice"}' | jq -r .access_token)
```

```json
{ "access_token": "eyJhbGciOi...", "token_type": "Bearer", "expires_in": 43200, "user_id": "alice" }
```

The token is an HS256 JWT valid for 12 hours. Send it as `Authorization: Bearer $TOKEN`. The user making a request is
always the token's subject; a `user_id` in a request body is ignored.

### 2. Create a show: `POST /shows`

```bash
SHOW=$(curl -s -X POST "$BASE/shows" \
  -H 'Content-Type: application/json' \
  -d '{"name":"friday-night","seats":["A1","A2","A3","A4","A5","A6"],"price_paise":25000,"per_user_limit":4}' \
  | jq -r .id)
```

| Field | Rules |
|---|---|
| `name` | required, 1–200 characters |
| `seats` | required, 1–10,000 labels, each 1–16 characters of `A-Z a-z 0-9 -`, no duplicates |
| `price_paise` | required, integer ≥ 0 (paise, so `25000` = ₹250); strings and fractions are rejected |
| `per_user_limit` | optional, 1–100, default 4: the most seats one user may hold in this show. `max_seats_per_user` is accepted as an alias |

Returns `201` with `Location: /shows/{id}` and the same body as `GET /shows/{id}`.

**Admin endpoint.** Creating shows is an admin operation. In this demo it is open by default so graders and the burst
tool can create shows without setup; set `Shows__RequireAuth=true` to require a bearer token. A real deployment would
check an admin role here.

### 3. Read a show: `GET /shows/{id}`

```bash
curl -s "$BASE/shows/$SHOW"
```

```json
{
  "id": "6f1c…", "name": "friday-night", "price_paise": 25000, "per_user_limit": 4,
  "counts": { "total": 6, "available": 6, "held": 0, "confirmed": 0 },
  "seats": [ { "label": "A1", "status": "available" }, … ],
  "as_of": "2026-10-03T10:00:00.123Z"
}
```

The counts and seat list come from one database read, so they are consistent with each other
(`available + held + confirmed == total`).

### 4. Reserve seats: `POST /shows/{id}/reserve`

The idempotency key can go in the **`Idempotency-Key` header** or the **`idempotency_key` body field**:

```bash
# key in the header
curl -s -i -X POST "$BASE/shows/$SHOW/reserve" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: alice-order-1' \
  -d '{"seats":["A1","A2"]}'

# key in the body
curl -s -i -X POST "$BASE/shows/$SHOW/reserve" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"seats":["A3"],"idempotency_key":"alice-order-2"}'
```

`201 Created`, with `Location: /reservations/{id}`:

```json
{
  "reservation_id": "0b9e…", "show_id": "6f1c…", "user_id": "alice",
  "seats": ["A1","A2"], "amount_paise": 50000, "status": "confirmed",
  "idempotency_key": "alice-order-1", "created_at": "2026-10-03T10:00:00.456Z"
}
```

- `seats`: 1–20 distinct labels from the show.
- The key: 1–128 printable ASCII characters without spaces. If both the header and the body field are sent they must
  be equal, otherwise it's a `400`. Sending neither is a `400`.
- Run the first command again and you get `200 OK` with the same body and the header `Idempotent-Replayed: true`.

### 5. Read a reservation: `GET /reservations/{id}`

```bash
RES=$(curl -s -X POST "$BASE/shows/$SHOW/reserve" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: alice-order-1' -d '{"seats":["A1","A2"]}' | jq -r .reservation_id)

curl -s "$BASE/reservations/$RES" -H "Authorization: Bearer $TOKEN"
```

Only the owner can read it: another user gets `403 not_owner`.

### 6. Cancel a reservation: `POST /reservations/{id}/cancel`

```bash
curl -s -X POST "$BASE/reservations/$RES/cancel" -H "Authorization: Bearer $TOKEN"
```

Returns `200` with the reservation, now `"status": "cancelled"` and a `cancelled_at`. Its seats are `available` again
and count against the user's limit no more. Cancelling again returns the same `200` (cancel is idempotent). Only the
owner can cancel: another user gets `403 not_owner`.

## Reservation rules

- **No double booking.** A seat is in at most one active reservation. Concurrent requests for the same seat lock its
  row in Postgres; exactly one wins (`201`) and the rest get `409 seat_taken`.
- **Multi-seat is all-or-nothing.** A request for several seats either reserves all of them or none. If any is taken,
  nothing is reserved and the `409 seat_taken` lists the `unavailable_seats`.
- **Per-user limit.** `per_user_limit` is set per show when it is created (default 4). A request that would take the
  user's active seats in that show above the limit is refused with `409 per_user_limit`, which reports `limit`, `held`
  (seats the user already has) and `requested`. Concurrent requests from the same user are serialised, so they can't
  slip past the limit together.
- **Reservations confirm immediately; release is an explicit cancel.** There is no hold-then-pay step and no hold
  timeout. A successful reserve goes straight to `confirmed`, and seats return to `available` only through
  `POST /reservations/{id}/cancel`. The `held` count and the `held` seat state exist in the API and metrics for a
  future hold step, but they are always `0`.
- **Idempotency.** A key belongs to the user who sent it. The same user, key and request (show and seat set)
  returns the original reservation as a `200` replay with `Idempotent-Replayed: true`, however many times it's
  retried, including concurrently and after the original request timed out on the client. The same key with a
  different request is `409 idempotency_key_conflict` and names the original `reservation_id`.

## Error codes

Every error is a problem document like:

```json
{
  "title": "One or more seats are not available.", "status": 409,
  "code": "seat_taken", "correlation_id": "086b35f2…", "unavailable_seats": ["A4"]
}
```

Clients should branch on `code`, not on `title`.

| Status | `code` | When | Extra fields |
|---|---|---|---|
| 400 | `validation` | Malformed JSON, or a field is missing or invalid; no idempotency key, or header and body keys differ | `errors` (field → messages) |
| 400 | `unknown_seat` | A seat label isn't in the show | `unknown_seats` |
| 400 | `bad_request` | The HTTP request itself can't be read (rejected by the server before reaching the API) | |
| 401 | `unauthorized` | Missing, expired or invalid bearer token | `WWW-Authenticate: Bearer` header |
| 403 | `not_owner` | The reservation belongs to another user | |
| 404 | `show_not_found` | No such show (or the id isn't a GUID) | |
| 404 | `reservation_not_found` | No such reservation (or the id isn't a GUID) | |
| 409 | `seat_taken` | One or more seats are already reserved; nothing was reserved | `unavailable_seats` |
| 409 | `per_user_limit` | The request would put the user over the show's limit | `limit`, `held`, `requested` |
| 409 | `idempotency_key_conflict` | The key was already used for a different request | `reservation_id` |
| 413 | `payload_too_large` | Body over 64 KB (256 KB for `POST /shows`) | |
| 500 | `internal_error` | A bug; logged with the correlation id | |
| 503 | `overloaded` | The admission queue is full | `Retry-After` header |
| 503 | `dependency_unavailable` | The database is unreachable after retries | `Retry-After` header |
| 503 | `not_ready` | The service is still starting (migrations not done) | `Retry-After` header |

A `503` means nothing was changed, so the request is safe to retry with the same idempotency key. In fact any retry
with the same key is safe.

## Health

| Endpoint | Meaning |
|---|---|
| `GET /health/live` | `200 {"status":"live"}` while the process runs. Never touches the database, so a database outage doesn't get the container restarted |
| `GET /health/ready` | `200` when the database answers and migrations are applied, otherwise `503`, with each check's status. Render routes traffic on this |
| `GET /metrics` | Prometheus text format |

None of these wait in the admission queue, so they answer even while a burst is running.

Their use is described in the [metrics guide](#metrics-guide).

## Metrics guide

`GET /metrics` is plain Prometheus text, so `curl -s $BASE/metrics | grep reservations_` already answers most
questions. With Prometheus scraping it, the queries below answer them over time.

### Metric catalog

Labels are low-cardinality only: there are never `user_id`, `reservation_id` or request-id labels (those are in the
logs). `endpoint` is the route template, e.g. `shows/{id}/reserve`.

| Metric | Type | Labels | What it shows |
|---|---|---|---|
| `http_requests_received_total` | counter | `code`, `method`, `endpoint` | Every HTTP response by status: throughput, status mix, 5xx |
| `http_request_duration_seconds` | histogram | `code`, `method`, `endpoint` | HTTP latency, including time spent in the admission queue (buckets 1 ms – 30 s) |
| `http_requests_in_progress` | gauge | `method`, `endpoint` | Requests in flight, including queued ones |
| `reservation_queue_length` | gauge | — | Requests waiting for a database slot in the admission queue |
| `reservations_confirmed_total` | counter | — | New reservations committed. A replay does **not** count here |
| `reservation_seats_confirmed_total` | counter | — | Seats in those new reservations |
| `reservations_declined_total` | counter | `reason` | Reserve attempts that didn't create a reservation: `seat_taken`, `per_user_limit`, `idempotent_replay`, `idempotency_key_conflict`, `unknown_seat`, `show_not_found`, `validation` |
| `reservations_cancelled_total` | counter | — | Cancels that released seats (a repeated cancel doesn't count) |
| `reservation_duration_seconds` | histogram | `outcome` | Time inside the reservation logic (`created`, `replayed`, `declined`, `error`), without HTTP or queueing |
| `show_seats` | gauge | `show_id`, `state` | Per-show seat counts, `state` = `available`, `held`, `confirmed` |
| `show_seats_total` | gauge | `show_id` | Seats per show |
| `seats_available`, `seats_held`, `seats_confirmed`, `seats_total` | gauge | — | The same, over all shows |
| `seats_gauge_stale` | gauge | — | `1` when the last seat-count query failed or timed out and the seat gauges show older values |
| `db_up` | gauge | — | `1` if the last database probe succeeded |
| `db_query_duration_seconds` | histogram | `operation` | Database time per operation (`reserve`, `cancel`, `fast_path`, `get_show`, `create_show`, `gauges`, `health`, …) |
| `db_errors_total` | counter | `operation`, `kind` | Database errors: `transient` (retried), `unavailable`, `bug` |
| `db_transaction_retries_total` | counter | `operation` | Transactions retried after a deadlock, serialization failure or dropped connection |
| `npgsql_db_client_connections_usage` | gauge | `pool_name`, `state` | Pool connections `used` / `idle` (pools `seatres-api` and `seatres-ops`) |
| `npgsql_db_client_connections_max`, `…_pending_requests` | gauge | `pool_name` | Pool size, and requests waiting for a connection |
| `dotnet_*`, `process_*` | various | — | Runtime: CPU, memory, GC, threads |

**Seat gauges come from the database.** `show_seats` and `seats_*` aren't counted in memory: each scrape reads them
from Postgres (one query, cached for 1 s), so they are correct after a restart and always agree with
`GET /shows/{id}`. `show_seats` is the only metric labelled by `show_id`, and it covers only the 200 most recently
created shows, so the number of series stays bounded.

**Single instance.** The counters (`*_total`) live in the process and start from 0 when it starts. The service runs as
one instance (`numInstances: 1` in `render.yaml`), so `/metrics` sees every request and reconciles exactly with the
API. With more instances, Prometheus would sum the counters across them; the database-backed seat gauges would be
correct either way.

### PromQL cheat-sheet

| Question | PromQL |
|---|---|
| How many requests per second? | `sum(rate(http_requests_received_total[30s]))` |
| … per endpoint? | `sum by (endpoint) (rate(http_requests_received_total[30s]))` |
| Reserve latency p50 / p95 / p99 / p99.9? | `histogram_quantile(0.99, sum by (le) (rate(http_request_duration_seconds_bucket{endpoint="shows/{id}/reserve"}[1m])))` (change `0.99`) |
| How many requests fail with 5xx? | `sum(rate(http_requests_received_total{code=~"5.."}[1m])) or vector(0)` |
| What do reserves return? | `sum by (code) (increase(http_requests_received_total{endpoint="shows/{id}/reserve"}[5m]))` |
| How many reservations succeed? | `sum(increase(reservations_confirmed_total[5m]))`, or `rate(...)` for per second |
| Why are reserves declined? | `sum by (reason) (increase(reservations_declined_total[5m]))` |
| How much is in flight / queued? | `sum(http_requests_in_progress)`, `reservation_queue_length` |
| How many seats are left in this show? | `show_seats{show_id="<id>"}` (equals the `counts` of `GET /shows/<id>`) |
| How many seats are left overall? | `seats_available` |
| Do the seat counts add up? | `sum by (show_id) (show_seats) - on (show_id) show_seats_total` (must be `0` for every show) |
| Is the database healthy? | `db_up`, `histogram_quantile(0.99, sum by (le, operation) (rate(db_query_duration_seconds_bucket[1m])))`, `sum by (operation, kind) (rate(db_errors_total[1m]))` |
| Is the pool saturated? | `npgsql_db_client_connections_usage{state="used"}` vs `npgsql_db_client_connections_max`, and `npgsql_db_client_connections_pending_requests` |

A series appears only once something has happened: with no 5xx yet, the 5xx query returns nothing, hence the
`or vector(0)`; `npgsql_db_client_connections_pending_requests` appears once a request has had to wait for a
connection. Without Prometheus, read the counters before and after a run and subtract.

### Reading a burst

The [burst tool](#burst-tool) prints one block per scenario, then totals. Taking
[docs/burst-report-final.txt](docs/burst-report-final.txt) as the example:

- **Per scenario:** the count of each status and `code` (`201`, `409 seat_taken`, …), `5xx`, `transport errors`
  (timeouts, resets: no HTTP answer at all), latency p50/p95/p99 and the throughput. `reconciliation` is
  `available+held+confirmed == total` for that scenario's show, and `FAIL:` lines say exactly which check failed.
- **`502 non_json`** means a 5xx whose body wasn't the API's JSON problem document: it came from a proxy in front of the
  service (Render's edge), not from the API. The server's own `http_requests_received_total{code=~"5.."}` doesn't see it.
- **`ownership`** (mixed storm) is the double-booking check: `0 seats in two reservations` and the most seats any user
  holds. `confirmed X != won Y` means the database confirmed more seats than the client saw succeed. That happens when
  a request commits after the client's timeout, and a retry with the same key would return it.
- **Final reconciliation** compares each show's `show_seats` gauges in `/metrics` with `GET /shows/{id}`: `7/7 shows
  match` means the metrics and the API agree.
- **`server metrics` / `server declines`** are the service's own counters. On a fresh instance they match the client's
  totals, give or take requests the client never saw answered: in the final report the server counted 838 new
  reservations against the 830 `201`s the client received. The 8 extra committed after their client had timed out
  (some were then seen through a `200` replay of a duplicate retry). Counters are since the process started, so on a
  long-running instance compare their change over the run.
- **`zero-5xx gate`** passes only with no 5xx and no transport errors; `OVERALL` passes only if everything above does.

### Local dashboards (optional)

```bash
docker compose --profile observability up --build
```

This also starts Prometheus on http://localhost:9090 (scraping the API every 5 s, `ops/prometheus.yml`) and Grafana on
http://localhost:3000 (anonymous, read-only), which opens on the provisioned "Seat Reservation" dashboard
(`ops/grafana/`). Its panels: DB up, 5xx, confirmed reservations, seats available, in flight, queue length, requests per
second by endpoint, reserve latency p50–p99.9, reserve status mix, declines by reason, confirmed and cancelled, seats,
DB pool connections, DB latency by operation, DB errors and retries, gauge staleness and the seat invariant. Run the
burst against `http://127.0.0.1:8080` and watch them move.

## Burst tool

`tools/Burst` fires concurrent traffic at a running service and checks the results. It has six scenarios:

| Scenario | What it does | Passes if |
|---|---|---|
| `hot` | 500 users race for one seat | exactly one `201`, the rest `409 seat_taken` |
| `idem` | 200 concurrent retries of one request with one key (key in header for half, body for half) | one `201`, the rest `200` replays of the same reservation |
| `conflict` | One key used concurrently for two different seats | one reservation; the other request gets `409 idempotency_key_conflict` |
| `limit` | One user requests 50 different seats at once on a limit-4 show, then on a limit-2 show | exactly 4, then 2, succeed |
| `mixed` | 20,000 requests from 5,000 users over 1,000 seats, with hot seats, multi-seat requests and retries | no seat sold twice, no user over the limit, confirmed seats match the successful responses |
| `cancel` | Reserve, cancel by a stranger (`403`), cancel by the owner, rebook by someone else | all as stated |

Afterwards it checks that every show's seat counts add up and that the `show_seats` gauges in `/metrics` match
`GET /shows/{id}`. The overall result also requires zero 5xx and zero transport errors (timeouts, resets) at a 60 s
client timeout. The exit code is `0` only if everything passes.

```bash
./burst.sh http://localhost:8080                       # all scenarios against a local service
./burst.sh https://seatres-api.onrender.com            # against the live service
./burst.sh <BASE_URL> --scenario hot,cancel            # some scenarios only
./burst.sh <BASE_URL> --json report.json               # also write a JSON report
./burst.sh --help                                      # every option and its default
```

`burst.sh` uses the .NET 8 SDK if it is installed, otherwise it builds and runs the tool in Docker
(`BURST_USE_DOCKER=1` forces Docker). Without bash: `dotnet run --project tools/Burst -c Release -- <BASE_URL>`, or
`make burst BASE_URL=<BASE_URL>`.

When the API runs in compose and the tool runs in Docker, reach the API over the compose network:

```bash
BURST_USE_DOCKER=1 BURST_DOCKER_NETWORK=bookingsystem_default ./burst.sh http://api:8080
```

## Architecture

```
HTTP ─▶ Controllers (SeatReservation.Api) ─▶ Services (SeatReservation.Application) ─▶ Repositories (SeatReservation.Infrastructure) ─▶ PostgreSQL
```

| Layer | Project | Does | Doesn't |
|---|---|---|---|
| Controller | `SeatReservation.Api` | Binds HTTP to commands, maps outcomes to status codes and problem bodies, auth, admission queue, metrics, logging | make business decisions |
| Logic | `SeatReservation.Application` | All business rules: validation, per-user limit, all-or-nothing, idempotency, cancel. Talks to storage through interfaces (ports) | reference ASP.NET Core, Npgsql, Dapper, Prometheus or Serilog |
| Repository | `SeatReservation.Infrastructure` | All SQL (Npgsql + Dapper), transactions with retries, migrations, health checks | make business decisions |

A reserve first does one lock-free read and turns away requests for seats that are clearly taken, which keeps the
hot-seat losers off the locks. Otherwise it runs in one transaction: lock the user (a transaction-scoped advisory lock), check the idempotency key, lock
the requested seat rows (`SELECT … FOR UPDATE`, in a fixed order so two requests can't deadlock), check that they are
all available and the user stays within the limit, then insert the reservation and mark the seats confirmed. Postgres
row locks make the database the single source of truth, so correctness doesn't depend on there being one instance.
[WRITEUP.md](WRITEUP.md) explains the choices.

Because the logic layer has no infrastructure dependencies, the unit tests cover it with in-memory fakes, and the
integration tests cover the two other layers with real HTTP and a real Postgres.

## Load test results (free plan)

The 20k-request burst (`./burst.sh https://seatres-api.onrender.com`) was run against the live service on
2026-10-03. Report: [docs/burst-report-2026-10-03.txt](docs/burst-report-2026-10-03.txt)
([JSON](docs/burst-report-2026-10-03.json)).
Final run after all M8 changes: [docs/burst-report-final.txt](docs/burst-report-final.txt) ([JSON](docs/burst-report-final.json)):
same picture, with 0×5xx in the 20k storm, 1 proxy `502` in the idempotency scenario and 14,708 client timeouts.

**Correctness holds under load:**
- The hot-seat storm (500 users, one seat) produced exactly one `201`.
- No seat is in two reservations, and no user holds more seats than `per_user_limit`.
- The per-show `show_seats` gauges match `GET /shows/{id}` for every show.
- The idempotency-replay, key-conflict, per-user-limit and cancel/rebook scenarios behave as specified.

**Capacity does not: the zero-5xx gate fails on the free plan.** The free instance has about 0.1 CPU and answers about
70 reserve requests per second. The 20k mixed storm needs about 330 req/s to finish inside the client's 60 s timeout.
- **15,432 requests timed out.** The admission queue kept them waiting instead of failing them; the API returned no 5xx of its own.
- **26 requests got `502`.** These came from Render's edge proxy (non-JSON body) while the instance was saturated.
- **Some timed-out requests still committed.** The client timed out before the answer arrived, but the reservation went
  through: 928 seats were confirmed against 915 seen by the client. Retrying with the same `Idempotency-Key` returns those
  reservations.

This is a choice to stay on the free plan, not a design limit. The service keeps one instance (the gauges are
per-process), so it scales up, not out: a Standard instance (1 CPU) is the next step for the zero-5xx gate.

## Log evidence

[Render live log captured during a burst](https://docs.google.com/document/d/1SExnH3vJzOQWY8asZlmzT9hh9cItXMWaGR2h1TSZs00/edit?usp=sharing)
(2026-10-03, `./burst.sh https://seatres-api.onrender.com --scenario hot,cancel`). The document shows:
- one JSON `http.request` line per request, with `CorrelationId`, trace id, route-template `Endpoint`, status and `Outcome`;
- the domain events `reservation.confirmed`, `reservation.cancelled` and `show.created`;
- the single hot-seat winner and the `409 seat_taken` declines;
- the cancel/rebook flow (`not_owner` → `cancelled` → rebooked → `already_cancelled`).

Render live logs are visible only to members of the Render workspace, so the evaluator can't open them directly.
This document stands in for that access.
