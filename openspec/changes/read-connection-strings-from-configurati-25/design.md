# Design

## Context

`src/UrlShortener.Api` is a FastEndpoints app (`net10.0`, FastEndpoints 8.3.0). `Program.cs` only calls `AddFastEndpoints()` / `UseFastEndpoints()` and ends with `public partial class Program { }`, which the tests use through `WebApplicationFactory<Program>`. `HealthEndpoint` (`src/UrlShortener.Api/Health/HealthEndpoint.cs`) always returns `200 { "status": "Healthy" }`. No connection strings or data packages exist yet. See proposal.md for motivation, and `specs/service-health/spec.md` for the required behaviour.

Constraints that shape the approach:
- Tests override configuration with `WebApplicationFactory.WithWebHostBuilder`. Values read from `builder.Configuration` in `Program.cs` before `Build()` do not reliably see those overrides, so connection strings must be read from `IConfiguration` at check time.
- The gates run `dotnet build src/UrlShortener.Api` and `dotnet test tests/UrlShortener.Tests`. The target declares `requires: [docker]`, so tests may start containers.

## Goals / Non-Goals

**Goals:**
- One small abstraction (`IDependencyCheck`) so the endpoint only aggregates results, and each check is a few lines.
- Every check is lazy (nothing connects at startup), bounded in time, and swallows all detail of a failure.

**Non-Goals:**
- No tables, migrations, repositories or shared, long-lived connection pools for application use. Those come with the first business endpoint.
- No ASP.NET Core `HealthChecks` middleware or `MapHealthChecks` route; `/health` stays a FastEndpoints endpoint.
- No new configuration keys beyond the three connection strings; timeouts are constants in code.
- No separate readiness/liveness split.

## Decisions

1. **Own `IDependencyCheck` interface instead of `Microsoft.Extensions.Diagnostics.HealthChecks`.** Three checks and one aggregating endpoint are simpler to read than registering `IHealthCheck`s, calling `HealthCheckService` and mapping its report back to the required body. Alternative considered: the built-in health-check service — rejected because its report format and status mapping would need translating anyway and adds indirection for no gain here.

2. **Connection strings read at check time via `IConfiguration.GetConnectionString(name)`.** This makes environment-variable overrides and test overrides work, and a missing value becomes a failed check instead of a startup exception. Alternative: bind once in `Program.cs` into options or an `NpgsqlDataSource` singleton — rejected because test overrides would not apply and a missing value would fail at startup or at DI resolution.

3. **Open and dispose a connection per check.** Postgres: `new NpgsqlConnection(...)`, `OpenAsync`, `SELECT 1`. Redis: `ConnectionMultiplexer.ConnectAsync(...)`, `PingAsync`, dispose. This is a connectivity probe, not application data access. Alternative: shared singleton `NpgsqlDataSource` / `IConnectionMultiplexer` — deferred until a business feature needs them; a singleton multiplexer created at startup would also risk eager connection.

4. **Short timeouts, applied in code.** Postgres: copy the configured string into `NpgsqlConnectionStringBuilder` and set `Timeout = 2` and `CommandTimeout = 2` (seconds). Redis: `ConfigurationOptions.Parse(...)` then `ConnectTimeout = 2000`, `AsyncTimeout = 2000`, `SyncTimeout = 2000`, `ConnectRetry = 1`, `AbortOnConnectFail = true`. Each check also runs under a `CancellationTokenSource` linked to the request token with `CancelAfter(TimeSpan.FromSeconds(3))`. The three checks run in parallel (`Task.WhenAll`), so the request finishes in about 3 seconds at worst, inside the 5-second limit.

5. **All failures collapse to `false`.** Each check wraps everything — including parsing the connection string — in `try/catch (Exception)` and returns `false`. Exceptions are never logged with their message or passed to the logger as the `exception` argument (Npgsql exception messages include host and port). The endpoint logs one Warning per failing dependency with only its name.

6. **Fixed dependency names and order.** `postgres-write`, `postgres-read`, `redis`, registered in that order. `failing` is built in registration order, so the body is deterministic.

7. **Tests use Testcontainers for real Postgres and Redis.** A shared xUnit fixture starts one Postgres container and one Redis container. Both `Write` and `Read` point at the same Postgres container (allowed by the requirement; they are still checked separately). Failing cases override one connection string with an unreachable address (`Host=127.0.0.1;Port=1;...` or `127.0.0.1:1`). Tests that need no dependency (routing, wrong method, all-unreachable, missing config) do not use the containers.

8. **Settings files.** `appsettings.json` gets the three keys with empty string values, so a non-Development deployment without overrides is reported unhealthy rather than silently pointing at localhost. `appsettings.Development.json` gets local placeholders:
   - `Write`: `Host=localhost;Port=5432;Database=urlshortener;Username=urlshortener;Password=urlshortener`
   - `Read`: same as `Write`
   - `Redis`: `localhost:6379`

## API contract

### HTTP: `GET /health`

- Anonymous. No headers, query string or body required. Any are ignored.
- Response content type: `application/json` in every case.
- JSON property names are camelCase (FastEndpoints default).

| Situation | Status | Body |
|---|---|---|
| Write, Read and Redis all reachable | `200 OK` | `{ "status": "Healthy", "failing": [] }` |
| One or more unreachable, unconfigured or unparseable | `503 Service Unavailable` | `{ "status": "Unhealthy", "failing": [ <names> ] }` |

- `status` is exactly `"Healthy"` or `"Unhealthy"`.
- `failing` is always present; it is an array of strings drawn from `"postgres-write"`, `"postgres-read"`, `"redis"`, in that order, containing each failing dependency once and no reachable one.
- No other properties. No exception text, host, port, user name or password.
- The response is sent within 5 seconds.
- `POST`/`PUT`/`DELETE /health` and any other route are unchanged (405/404 from routing).

Example failure: `503` `{ "status": "Unhealthy", "failing": ["postgres-read"] }`.

### Configuration

| Key | Env var | Used by | Missing/empty |
|---|---|---|---|
| `ConnectionStrings:Write` | `ConnectionStrings__Write` | `postgres-write` check (Npgsql format) | check fails |
| `ConnectionStrings:Read` | `ConnectionStrings__Read` | `postgres-read` check (Npgsql format) | check fails |
| `ConnectionStrings:Redis` | `ConnectionStrings__Redis` | `redis` check (StackExchange.Redis format) | check fails |

### C# types (all in `src/UrlShortener.Api/Health/`, namespace `UrlShortener.Api.Health`)

```csharp
public interface IDependencyCheck
{
    string Name { get; }                                   // "postgres-write" | "postgres-read" | "redis"
    Task<bool> IsHealthyAsync(CancellationToken ct);       // never throws; false on any failure
}

public sealed class PostgresDependencyCheck(string name, string connectionStringName, IConfiguration configuration) : IDependencyCheck;
// Reads configuration.GetConnectionString(connectionStringName) at call time.
// Empty/null -> false. Applies Timeout=2, CommandTimeout=2; opens, runs SELECT 1, disposes.

public sealed class RedisDependencyCheck(IConfiguration configuration) : IDependencyCheck;
// Name = "redis". Reads GetConnectionString("Redis") at call time. Empty/null -> false.
// ConfigurationOptions.Parse, ConnectTimeout/AsyncTimeout/SyncTimeout=2000, ConnectRetry=1,
// AbortOnConnectFail=true; ConnectAsync, PingAsync, dispose.

public sealed record HealthResponse(string Status, IReadOnlyList<string> Failing);

public sealed class HealthEndpoint : EndpointWithoutRequest<HealthResponse>;
// Constructor-injects IEnumerable<IDependencyCheck>.
// Runs all checks in parallel, each under a linked CTS with CancelAfter(3s).
// For each false: Logger.LogWarning("Health check failed: {Dependency}", name).
// All true  -> 200 HealthResponse("Healthy", []), log Information "Health check: Healthy".
// Any false -> 503 HealthResponse("Unhealthy", failing) via Send.ResponseAsync(response, 503, ct).
```

### Registration in `Program.cs` (before `Build()`, in this order)

```csharp
builder.Services.AddSingleton<IDependencyCheck>(sp => new PostgresDependencyCheck("postgres-write", "Write", sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IDependencyCheck>(sp => new PostgresDependencyCheck("postgres-read", "Read", sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IDependencyCheck, RedisDependencyCheck>();
```

Nothing in `Program.cs` reads a connection string or opens a connection. `public partial class Program { }` stays.

### Packages

- `src/UrlShortener.Api/UrlShortener.Api.csproj`: `Npgsql`, `StackExchange.Redis` (latest stable versions that support `net10.0`).
- `tests/UrlShortener.Tests/UrlShortener.Tests.csproj`: `Testcontainers.PostgreSql`, `Testcontainers.Redis` (same major version as each other).

## Risks / Trade-offs

- [Orchestrators using `/health` as a liveness probe will restart pods during a database or Redis outage] → Accepted by the requirement; documented in `docs/api/health.md` so operators can point liveness probes elsewhere or tolerate it.
- [A new Redis multiplexer per request is relatively expensive] → Acceptable for an infrequently polled health route; revisit when a shared multiplexer is introduced for application use.
- [Npgsql pools connections per connection string, so a passing check may reuse an idle pooled connection to a server that has just gone away] → `SELECT 1` runs on it and fails, so the check still reports correctly.
- [Docker unavailable where tests run] → Container-backed tests fail rather than silently pass. The target declares `requires: [docker]`. If Testcontainers is not approved at the dependency gate, the fallback is to replace `IDependencyCheck` registrations with fakes in tests; those tests then prove aggregation and the response shape only, not real connectivity, and must say so.
- [Unreachable host that drops packets instead of refusing] → Bounded by the 2-second driver timeouts and the 3-second linked cancellation.

## Migration Plan

Deploy with `ConnectionStrings__Write`, `ConnectionStrings__Read` and `ConnectionStrings__Redis` set. Without them, `/health` returns `503` by design. Rollback is redeploying the previous build; no data or schema is involved.
