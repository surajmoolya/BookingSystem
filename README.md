# Seat Reservation at Scale

C# / .NET 8 + PostgreSQL 16 seat-reservation service.

## Live service

**https://seatres-api.onrender.com** (Render, region singapore, one instance)

| Endpoint | Meaning |
|---|---|
| `GET /health/live` | Process is up (`{"status":"live"}`); never checks the database |
| `GET /health/ready` | `200 Healthy` when the database answers and migrations are applied, otherwise `503` |

The service runs on Render's free tier: after ~15 minutes without traffic it spins down, and the first request
afterwards waits for a cold start. Hit `/health/ready` once before testing.

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
