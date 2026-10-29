# Option 1: one ASP.NET Core app, two roles, background analytics, Docker locally

Status of the repo: it is empty apart from tooling, the HLD diagram (`architecture-hld.svg`) and `.foreman/target.yaml`. Nothing is built yet, so every claim below about code is a design claim, not a measurement. The machine has .NET SDK 10.0.403 (checked). I could not check Docker (the command was not approved in this session), so that is an unknown.

## Verdict

Feasible and small. It meets every constraint, and the 100 ms redirect budget is generous for a Redis-then-Postgres lookup. The real risks are not performance. They are:

1. Gaps in the HLD that the option inherits (no login endpoint, no deactivate endpoint, two list endpoints).
2. Protected migrations, which block the first schema until it is approved.
3. Tests that need Docker to run under a fixed `dotnet test` command.
4. A few correctness traps in the cache and in the analytics batch writer.

I would pick it, subject to the questions at the end.

## Build outline

Target `net10.0`. Pin package versions when the project is created.

```
src/
  compose.yaml                 (see "Where files can live")
  UrlShortener.Api/
    UrlShortener.Api.csproj
    Dockerfile
    Program.cs                 wiring only
    Urls/       Url entity, UrlEndpoints, ShortCodeGenerator, UrlService
    Redirect/   RedirectEndpoint, RedirectCache
    Users/      User entity, AuthEndpoints (register/login/logout), UserUrlsEndpoint
    Analytics/  RequestEvent, AnalyticsChannel, TimingMiddleware, AnalyticsWriter (BackgroundService)
    Data/       AppDbContext, Migrations/        <- protected
    Infrastructure/  Roles, RateLimiting, Telemetry, Health
tests/UrlShortener.Tests/
docs/           schema.md (for approval), runbook.md
```

### Data model (needs approval before any migration is generated)

| Table | Columns | Notes |
|---|---|---|
| `users` | id, email (unique), password_hash, created_at | Hash with `PasswordHasher<T>` from `Microsoft.Extensions.Identity.Core`. Do not add full ASP.NET Identity: it brings its own tables and migrations. |
| `urls` | id (from sequence), short_code (unique), target_url, user_id, is_active, created_at | Index `(user_id, created_at desc, id desc)` for the paged list. |
| `click_events` | url_id, occurred_at, duration_ms, referrer, user_agent | **No foreign key** to `urls` (see traps). Index `(url_id, occurred_at)`. |
| `url_stats` | url_id (PK), click_count, last_clicked_at | Updated by upsert. |

Deactivate = `is_active = false`, row stays. Delete = row removed. I would cascade-delete `url_stats` and leave orphan `click_events` or delete them with it; that is a question below.

### Short codes

`SELECT nextval('urls_id_seq')`, then `Sqids` encode (min length about 6), then insert the row with that id and the code in one statement. The code is stored in `short_code` with a unique index. Do not decode at read time: changing the Sqids alphabet or min length later would break existing links. Because a sequence never repeats, a deleted code is never reissued to a different URL, and there are no collisions to retry. Gaps in the sequence are harmless.

Sqids is obfuscation, not secrecy. A determined person can still enumerate codes.

Add reserved words to the Sqids blocklist (`health`, `v1`, and similar) so a generated code cannot equal a literal route. Literal routes already win over `/{shortcode}` in endpoint routing, but a code that shadows `/health` would be unreachable.

### Redirect path (`GET /{shortcode}`)

- Route constraint on the shortcode (alphanumeric, bounded length) so junk paths like `/favicon.ico` skip the lookup.
- Look up Redis, falling back to Postgres. Cache value: target URL plus active flag. Select only those two columns, no tracking.
- Respond **302** (or 307), not 301. A 301 is cached by browsers, so repeat clicks would never reach us (analytics undercounts) and a deactivated link would keep working for anyone who had it cached.
- Unknown, deleted and deactivated all return 404 (410 for deactivated is an option; see questions).
- Push the event with `TryWrite` and move on. Never `await` the channel on this path.

Cache details that matter:

- **Invalidate on deactivate and delete.** This is the one place where eventual consistency would break a stated rule ("a deactivated code must stop redirecting"). Delete or overwrite the key in the same request that changes Postgres. Also give every entry a short TTL (this is a cache TTL, unrelated to the out-of-scope URL expiry) as a backstop if the invalidation call fails.
- **Redis timeouts.** StackExchange.Redis defaults are in seconds. A hung Redis would blow the 100 ms budget. Set the async timeout to tens of milliseconds and fall back to Postgres on any Redis error.
- **Write-through on create** so a probed-then-created code is not stuck behind a cached "not found". Negative caching of unknown codes with a short TTL is optional.
- **Library choice.** `HybridCache` (`Microsoft.Extensions.Caching.Hybrid`) adds stampede protection, but its in-process L1 cache is per instance and a delete does not clear other instances' L1. With two roles that means a deactivated link could keep redirecting for the L1 lifetime. Either set `HybridCacheEntryFlags.DisableLocalCache` (Redis only) or use `IDistributedCache`/StackExchange.Redis directly. I lean to direct Redis: fewer moving parts, and a Redis round trip is about a millisecond locally. Needs a quick check of the HybridCache flag behaviour if chosen.
- **Cold start.** The first EF query compiles for a while and the Npgsql pool is empty. Warm up on startup (one dummy query and Redis ping) or the first redirects after a deploy can exceed 100 ms. A compiled query or plain Npgsql for this one query avoids most of it.

### Analytics

One bounded `Channel<RequestEvent>` (capacity around 10k, `FullMode = DropWrite`, single reader). A dropped-events counter is exported as a metric.

- `TimingMiddleware` is first in the pipeline. It starts a `Stopwatch` and records at response completion (`Response.OnCompleted` or after `next`). It measures server processing time, not what the client sees.
- `AnalyticsWriter` (BackgroundService) waits for an item, drains up to N (for example 500) or 1 second, then in **one transaction**: bulk-inserts `click_events` (Npgsql binary COPY is faster than EF `AddRange`; EF is fine to start) and upserts `url_stats` with per-batch pre-aggregated counts (`ON CONFLICT (url_id) DO UPDATE SET click_count = url_stats.click_count + EXCLUDED.click_count`).
- Increments commute, so several instances each running their own writer is safe.
- **Shutdown.** On SIGTERM complete the channel and drain it. Raise `HostOptions.ShutdownTimeout` and set a matching `stop_grace_period` in Compose, otherwise Docker's default 10 s kill can cut the drain short. A hard crash still loses whatever is in the channel plus the batch in flight, as the option states.
- **Failure.** Retry a failed batch with backoff, then drop it and log. Never let a bad batch block the loop or let memory grow.

Traps:

- **Foreign key on `click_events` would break batches.** If a URL is deleted between the click and the flush, one bad row fails the whole batch insert. Leave the FK off, or filter with an `INSERT ... SELECT` against `urls`.
- **"Every request's processing time" is wider than clicks.** `click_events` only covers redirects. For the other endpoints, the cheapest option is the OpenTelemetry `http.server.request.duration` histogram that ASP.NET Core already emits. Alternatively write all requests to the same table with a nullable `url_id`, which grows fast. Needs a decision (question 4).
- `click_events` grows without bound and the HLD says nothing about retention. Roughly 100 bytes per row is about 100 MB per million clicks. Fine for a demo; flag for later (partitioning or pruning).

### Auth

Built-in cookie authentication, 1-day expiry, `SlidingExpiration = false`, `HttpOnly`, `SameSite=Lax`.

- Override `OnRedirectToLogin` and `OnRedirectToAccessDenied` to return 401 and 403. The default redirects to `/Account/Login`, which is wrong for an API.
- The cookie is self-contained, so no session store is needed. The cost is that logout only clears the browser's cookie; a stolen cookie works until it expires. That matches "cookie session with 1 day expiry".
- **Data Protection keys.** In a container the key ring is lost on restart, which logs everyone out, and two instances would not be able to read each other's cookies. Persist the keys to Redis (`Microsoft.AspNetCore.DataProtection.StackExchangeRedis`) or a Compose volume. Do not use the EF Core key store: it needs a table, hence a protected migration. Redis is fine locally; a Redis flush logs everyone out.
- **CSRF.** Cookies plus POST/DELETE means CSRF is in play. `SameSite=Lax` and no permissive CORS policy cover the usual cases. Say so in the docs; do not claim more.
- Owner checks on `GET`/`DELETE /v1/urls/{shortcode}`: return 404 for someone else's code, so codes cannot be probed.
- Validate submitted URLs: absolute `http`/`https` only (no `javascript:` or `data:`), length cap, and reject the shortener's own host to avoid loops.

### Rate limiting

`Microsoft.AspNetCore.RateLimiting` is in-memory and per process. With two instances the effective limit is doubled, and it resets on restart. Redis is not used for it in this option. That is fine locally and for a demo. The HLD puts rate limiting at the API gateway, so a production deployment would do it there. Apply it to login and the write endpoints (partition by user or IP); give the redirect path a generous limit or none. Behind a proxy, `ForwardedHeaders` must be configured or every client looks like one IP.

### Write/Read role flag

`App:Role = All | Write | Read`. `Program.cs` maps endpoint groups by role:

- Write: register/login/logout, `POST` and `DELETE` urls, deactivate.
- Read: `GET /{shortcode}`, `GET /v1/urls/...`, `GET /v1/users/urls`.
- `All` is the default locally.

Consequences to know before promising "deploy it twice later":

- Both roles need the same Data Protection keys and cookie settings (Read endpoints are authenticated).
- The Read role still needs the **primary** database for the analytics writer. It cannot be limited to a read replica. So there are two connection strings: one for the analytics writer (primary) and one for reads (replica or the same database).
- With a real replica, `POST` then immediate `GET` can 404 (replica lag). The HLD accepts eventual consistency, but the API consumer will notice.
- Do not build a second DbContext or replica routing now. Ship the flag mapping and, optionally, a Compose profile that runs the same image twice with different roles. That is cheap, and it proves the split works.

### Pagination

`GET /v1/users/urls?sort=date&count=&page=`: page-based as required. `ORDER BY created_at DESC, id DESC` with offset/limit. Default count 20, clamp to 100, page starts at 1, `sort` accepts only `date` for now (reject others with 400). Join `url_stats` for click counts (eventually consistent). Offset cost grows with page depth; it is fine at this scale and cursors are explicitly out of scope.

### Observability and operations

- OpenTelemetry: ASP.NET Core, HttpClient, Npgsql (`Npgsql.OpenTelemetry`) and Redis instrumentation, runtime metrics. Custom metrics: channel depth, dropped events, flush duration and batch size, cache hit/miss. Console or OTLP exporter configured by environment variable; no collector is required in Compose (optional later).
- `/health/live` and `/health/ready` (Postgres and Redis checks).
- Schema at startup: `MigrateAsync()` only when `Database:MigrateOnStart` is true (local Compose). Never in a two-instance deployment, where both would race.

### Tests

xUnit. Two layers:

- **Unit (no Docker):** Sqids code generation, batch aggregation and writer logic against a fake sink, channel drop behaviour, URL validation, pagination clamping, cache fallback on Redis failure (fake).
- **Integration (Testcontainers Postgres + Redis, `WebApplicationFactory`):** create, redirect, deactivate stops redirect (cache invalidated), delete, ownership, cookie auth expiry, analytics eventually reaches `url_stats`.

Risk: `dotnet test tests/UrlShortener.Tests` is the fixed gate command. If Docker is not available where Foreman runs it, every Testcontainers test errors. Options: a `[DockerFact]` that skips when Docker is absent (then "tests pass" means "unit tests passed, integration tests skipped", and the report must say that), or move the command on purpose. Passing tests with fakes do not prove that Redis or Postgres behave as assumed; only the integration layer does.

## Where files can live

`target.yaml` allows code under `src/**`, tests under `tests/**`, docs under `docs/**`. A `Dockerfile` and `compose.yaml` at the repo root, and a `.sln` at the root, are outside those paths and may be rejected. Safe placement: `src/UrlShortener.Api/Dockerfile` and `src/compose.yaml` (build context `./UrlShortener.Api`, which works because there is only one project). Also `dotnet-ef` should be installed as a global tool, since a tool manifest would sit at `.config/dotnet-tools.json`, outside the allowed paths. Need confirmation.

## Assessment

| Axis | Assessment |
|---|---|
| Complexity | Low to moderate. One project, one process, five tables-worth of concepts. Estimate about 1,500 to 2,500 lines including tests. The hardest parts are the cache invalidation rules and the batch writer, not the framework. |
| Cost | Local: Postgres, Redis and one app container. Production: one small service (two deployments if split) plus managed Postgres and Redis. No queue or stream service to pay for. |
| Performance | Redirect hit: Kestrel plus one Redis GET, expected low single-digit milliseconds. Miss: plus one indexed Postgres read, a few milliseconds. 100 ms leaves large headroom. Threats are cold start, Redis timeouts and a flooded writer, all addressed above. These are estimates until measured. |
| Risk | Event loss on crash (accepted). Stale cache after failed invalidation (mitigated by delete-on-write plus short TTL). Per-instance rate limits. Cookies not revocable. Unbounded `click_events`. Docker dependency for the test gate. |
| Operability | Easy to run locally (`docker compose up`) and to read (feature folders). Metrics for channel depth and drops make the main failure mode visible. A crash loses analytics silently unless the drop and depth metrics are watched. |

## Unknowns, and what I would test first

1. **Redirect latency, measured.** Run a load test (k6 or bombardier) against hit, miss and Redis-down. Check p99 under 100 ms, including the first requests after start.
2. **Docker in the Foreman environment.** Does Testcontainers work there? This decides whether the `dotnet test` gate means anything.
3. **Cache invalidation across two instances.** Run two copies, deactivate on one, redirect on the other. Confirm it stops at once (this is what decides HybridCache versus direct Redis).
4. **Cookie survival across a container restart** with the Data Protection keys in Redis.
5. **Writer throughput and shutdown.** Flood redirects; confirm drops are counted, memory is flat, and a `docker stop` drains the channel.
6. **Sqids behaviour.** Confirm the blocklist and min-length options and that codes stay unique and case-sensitive through Postgres (default collation is case-sensitive).

## Questions for you (HLD gaps; I did not guess)

1. **Login.** The HLD requires cookie sessions but lists no register or login endpoint. Add `POST /v1/users` (register) and `POST /v1/users/login` and `/logout`? These go beyond the listed APIs.
2. **Deactivate.** Requirement 5 is deactivate, but the only listed write is `DELETE`. Add `PATCH /v1/urls/{shortcode}` with `{ "isActive": false }`? Also should it be reversible (reactivate)?
3. **Two list endpoints.** `GET /v1/urls/` and `GET /v1/users/urls` both exist. What is the difference? If they are the same (the signed-in user's URLs), one could be a thin alias.
4. **Timing for non-redirect requests.** Is the OpenTelemetry duration histogram enough, or must they be stored in Postgres?
5. **Deleting a URL.** Delete its click history too, or keep it? And 404 versus 410 for deactivated codes.
6. **Migrations.** The first schema, and every later change, needs your explicit approval. Do you want the table definitions above in `docs/schema.md` for review before any migration is generated?
7. **File locations** for Dockerfile, compose file and `.sln` (see "Where files can live").
