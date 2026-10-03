# Seat Reservation at Scale

C# / .NET 8 + PostgreSQL 16 seat-reservation service.

## Live service

**https://seatres-api.onrender.com** (Render, region singapore, one instance)

| Endpoint | Meaning |
|---|---|
| `GET /health/live` | Process is up (`{"status":"live"}`); never checks the database |
| `GET /health/ready` | `200 Healthy` when the database answers and migrations are applied, otherwise `503` |

The service runs on Render's free tier: after ~15 minutes without traffic it spins down, and the first request
afterwards waits for a cold start. The `keep-alive` workflow (`.github/workflows/keep-alive.yml`) pings
`/health/live` every 5 minutes to prevent that; GitHub may delay scheduled runs, so still hit `/health/ready` once
before testing.

## Run locally

```bash
docker compose up --build
```

The API listens on http://localhost:8080 and applies its migrations on startup; `/health/ready` turns `200` once they are done.

## Build and test

```bash
dotnet build SeatReservation.sln
dotnet test tests/SeatReservation.UnitTests
dotnet test tests/SeatReservation.IntegrationTests --filter "Category!=Load"
```

Integration tests start a real Postgres with Testcontainers, so Docker must be running.

## Load test results (free plan)

The 20k-request burst (`./burst.sh https://seatres-api.onrender.com`, lld §12) was run against the live service on
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
