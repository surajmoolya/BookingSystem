# Seat Reservation at Scale: technical write-up

## Capacity on the deployed plan

The live service runs on Render's **free** plan: one web instance with about 0.1 CPU and 512 MB, plus free Postgres 16,
in singapore. That was a cost decision (D-091, D-102), and it decides the load-test result.

**Result of the 20k burst on 2026-10-03** ([report](docs/burst-report-2026-10-03.txt)):

| Check | Result |
|---|---|
| Hot seat: 500 users race for one seat | exactly 1×201, 499×409 `seat_taken` |
| Double booking in the 20k mixed storm | none (0 seats in two reservations) |
| Per-user limit | held (max 4 seats per user, limit 4) |
| Idempotent replay / key conflict / cancel and rebook | as specified |
| `/metrics` per-show gauges vs `GET /shows/{id}` | 7 of 7 shows match |
| Zero 5xx and zero transport errors in the 20k storm | **FAIL**: 15,432 client timeouts at 60 s, 26×502 from Render's proxy |

**Why it fails.** The instance answers about 70 reserve requests per second. The same ceiling showed up in every free-plan run
(T-3.15a/b, T-8.4), with or without the admission queue and runtime tuning, so the tenth of a CPU is the likely limit.
CPU wasn't measured: the free plan has no metrics tab. 20,000 requests
inside a 60 s client timeout need about 330 req/s. The admission queue (T-6.5) bounds the database work and keeps
excess requests waiting instead of failing them. So no 5xx in the report came from the API, but most requests waited past
the client's timeout. Render's edge proxy returned a non-JSON `502` for 26 connections while the instance was saturated.

**What a timeout means for the client.** A timed-out request may still have committed: the storm confirmed 928 seats
against 915 seen by the client. This is why every reserve carries an idempotency key. Retrying with the same key returns
the committed reservation (`200`, `Idempotent-Replayed: true`) instead of booking twice.

**How to pass the gate.** Scale up, not out. The per-show gauges and counters are per-process, so the service runs one
instance (D-086). A Standard instance (1 CPU, 2 GB) is the recommended evaluation size (D-088). Tuning pool and permit
sizes, or batching the locked path's queries, cannot make up the roughly 5× gap on a tenth of a CPU (T-8.5).
