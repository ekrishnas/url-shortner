# Proposal

## Why

The API will soon depend on a Postgres write database, a Postgres read database and Redis, but today it has no way to know where they are and `GET /health` reports healthy even when none of them can be reached. Reading the three connection strings from configuration, and making `/health` check that each one is reachable, lets operators and deployments see straight away when the service cannot reach what it needs.

## What Changes

- The API reads `ConnectionStrings:Write`, `ConnectionStrings:Read` and `ConnectionStrings:Redis` from configuration, so environment variables such as `ConnectionStrings__Write` override them. No connection string is written as a literal in C#.
- `appsettings.json` gains a `ConnectionStrings` section with empty values; `appsettings.Development.json` holds local-development placeholder values (no real credentials).
- **BREAKING**: `GET /health` stops being a liveness-only check. It returns `200` only when the Write Postgres database, the Read Postgres database and Redis are all reachable, and `503` otherwise, with a JSON body naming every failing dependency (`postgres-write`, `postgres-read`, `redis`).
- **BREAKING**: the healthy body becomes `{ "status": "Healthy", "failing": [] }` (it gains an always-present `failing` array).
- A missing or empty connection string makes that dependency fail the health check; it never stops the host from starting.
- Checks are connectivity-only (`SELECT 1`, `PING`), have short timeouts so `/health` answers within 5 seconds, and never expose hostnames, ports, credentials or exception messages in the response or logs.
- Adds the `Npgsql` and `StackExchange.Redis` packages to the API project, and `Testcontainers.PostgreSql` and `Testcontainers.Redis` to the test project.
- Health tests, the `service-health` spec and `docs/api/health.md` are updated to match.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `service-health`: `GET /health` becomes a dependency check of the Write database, the Read database and Redis. "Liveness is independent of external dependencies" is removed; "Liveness endpoint" and "Anonymous access to liveness" are modified for the new status codes and body; new requirements cover configuration-sourced connection strings, the failure response, missing configuration, bounded response time, startup independence and not leaking connection details.

## Impact

- Code: `src/UrlShortener.Api/Program.cs`, `src/UrlShortener.Api/Health/` (endpoint plus new dependency-check classes), `src/UrlShortener.Api/appsettings.json`, `src/UrlShortener.Api/appsettings.Development.json`.
- Dependencies: `src/UrlShortener.Api/UrlShortener.Api.csproj` (`Npgsql`, `StackExchange.Redis`); `tests/UrlShortener.Tests/UrlShortener.Tests.csproj` (`Testcontainers.PostgreSql`, `Testcontainers.Redis`). These need approval at the dependency gate.
- Tests: `tests/UrlShortener.Tests/Health/` — the healthy-case and per-dependency failing-case tests need Docker (the target already declares `requires: [docker]`).
- Operations: orchestrators that probe `/health` for liveness will now see `503` during a database or Redis outage. There is no separate liveness route, by design.
- Docs: `docs/api/health.md` is rewritten.
