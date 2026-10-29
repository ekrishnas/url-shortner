# Analysis: components, packages and low-level shape for the URL shortener

Change id: explore-components-packages-and-low-leve-1 (issue #1)

All three explorer notes are desk studies. Nobody built or ran anything, and the repo has no code yet. Every latency, cost and durability statement below is reasoning or recollection, not measurement. The tests that would settle the main claims are listed under Open questions.

## Context

The repo holds the setup for a URL shortener and nothing else: an Excalidraw high-level design (`architecture-hld.svg`), Foreman/OpenSpec scaffolding, and no source code or specs. `.foreman/target.yaml` commits the project to .NET, with one build target (`src/UrlShortener.Api`) and one test project (`tests/UrlShortener.Tests`). `*.csproj` files and `**/Migrations/**` are protected.

A logged-in user can create, list, get, deactivate and delete their own short URLs and see click counts. Anyone can follow a short URL. Redirects must take under 100 ms, processing time must be captured for analytics, and eventual consistency is acceptable. Auth is a cookie session with a 1-day expiry.

The diagram draws an API Gateway, a load balancer, a Write Service, a Read Service in an Auto Scaling Group, Redis, and Postgres with read replicas. It has no queue and no analytics store.

The decision is the components, packages and low-level shape before any code exists. Four points are open: where clicks and timings are recorded, what Redis is for, AWS or local Docker, and one service or two.

Things all three explorers found that the framing did not state:
- **There is no login endpoint.** With OIDC out of scope, nothing can create a session unless the app has its own register/login/logout. Every option needs this.
- **Short codes can collide with literal routes** (`v1`, `health`), because `GET /{shortcode}` sits at the root.
- **Data Protection keys** default to a container-local folder. In Docker, a restart or a second instance logs everyone out unless the keys are persisted.
- **A crashed `BackgroundService` stops the host** by default in .NET 6+, so every background loop needs a try/catch.
- **Testcontainers needs a Docker daemon** wherever `dotnet test` runs. Without one, the integration tests fail rather than skip.
- **Redirects must be 302, not 301.** Browsers cache 301s, which would hide clicks and make deactivation ineffective.

## Assumptions

No assumptions were given and the four clarifying questions are unanswered. This analysis makes the following working assumptions. Each should be confirmed before building.

- **Local Docker is the only target for the first change.** The constraint is that it must run locally. AWS stays possible but nothing is built for it yet.
- **One instance is the expected deployment for now.** Nothing in the requirements asks for independent scaling.
- **The app has its own username/password login**, using the framework's `PasswordHasher<User>` and a stateless auth cookie (1-day expiry, sliding).
- **Clicks may be lost in rare failure cases** (process crash, full buffer). Eventual consistency is read as allowing this, provided the loss is bounded and counted.
- **"Processing time" means server-side time for redirects**, from request entry to response start. Write API timings are not required.
- **API gaps are resolved the same way in every option:** one list endpoint for the caller's own URLs, an explicit deactivate endpoint, 404 for another user's URL, offset paging.

## Options

### Option 1: One ASP.NET Core service with in-process background analytics

One project, `UrlShortener.Api`, with feature folders (`Auth`, `Urls`, `Redirect`, `Analytics`, `Data`).

- **Data:** EF Core with Npgsql. Tables `users`, `urls` (soft delete via `deleted_at`, with a global query filter) and `clicks` (`url_id`, `clicked_at`, `elapsed_ms`).
- **Short codes:** reserve the id with `nextval`, encode it in base62 with a minimum length, insert once. No collisions and no retry loop. Codes are enumerable from the id, which is acceptable because redirects are public.
- **Redirect:** `HybridCache` (memory first, then Redis through `IDistributedCache`), with a short-lived "gone" marker for missing codes. 302, then `TryWrite` to a bounded `Channel` that drops when full.
- **Clicks:** a `BackgroundService` writes batches (500 events or 1 s) to `clicks`. Counts come from `count(*)` on an indexed column.
- **Invalidation:** commit to Postgres, then `RemoveAsync`. On one instance this is immediate. With several instances, other nodes serve the old redirect until their in-memory entry expires, so the in-memory TTL (30–60 s) is the staleness bound.
- **Other:** built-in rate limiter (per instance), Data Protection keys in Postgres, Compose with three containers (api, postgres, redis).

Explorer verdict: the smallest design that meets every requirement. The main cost is click loss on a crash, plus stale redirects across instances if more than one runs.

### Option 2: Separate Write and Read/Redirect services with a queue-backed analytics worker

Four projects (`Core`, `Api`, `Redirect`, `Worker`), plus a local gateway. The redirect service reads Redis, falls back to a read replica, then the primary, and publishes clicks through an in-process buffer to Redis Streams (SQS later). The worker writes clicks idempotently by event id.

The explorer found real costs:
- **A replica-lag race.** Deleting the cache entry on deactivate lets a redirect refill it from a lagging replica. The fix is tombstones plus `SET NX`, which works only if deactivation is one-way.
- **The build command stops covering the code.** `dotnet build src/UrlShortener.Api` would not compile `Redirect` or `Worker`. `target.yaml` would need a solution build, which is outside this spike's writable paths.
- **About nine containers locally**, including a Postgres replica that is fiddly to set up.
- **Auth at the gateway does not fit cookies.** API Gateway authorizers expect bearer tokens, so the Write API would validate the cookie itself, which departs from the diagram.
- **Durability is only partial.** Clicks still sit in an in-process buffer before the queue.

Explorer verdict: buildable, but 2–3 times the work of a single service. Choose it only if independent scaling and a durable pipeline are goals in themselves.

### Option 3: Redis-centred hot path with periodic flush to Postgres

One service, as in Option 1, but Redis holds the redirect mapping (no in-memory layer), click counters (`HINCRBY`), timing samples (a stream), and shared rate-limit counters. A `BackgroundService` flushes to Postgres using rename-and-flush with an idempotency table, and stream consumer groups with `XACK`.

The explorer found:
- **The strongest invalidation story.** One Redis update is seen by every instance at once, and an app crash loses no clicks.
- **No latency gain over Option 1.** An in-memory hit is faster than any Redis hop. Both are far under 100 ms.
- **The stated session design is unnecessary.** Stateless cookies need no Redis, and Data Protection keys should stay in Postgres. With that change, a Redis outage does not log users out.
- **Many small, subtle parts:**
  - a Lua rate limiter
  - a flush guard so `RENAME` never overwrites an unflushed batch
  - fail-safe ordering for deactivate (update Redis first, fail the call if Redis is unreachable)
  - a circuit breaker and low timeouts on Redis calls
  - two Redis roles, or careful eviction settings, so the cache does not evict counters
- **AOF may not be available on ElastiCache** (unverified). Durable managed Redis may mean MemoryDB at higher cost.

Explorer verdict: more machinery than one container needs. The best answer if several instances are a near-term need.

## Trade-off matrix

| Option | Complexity | Cost | Performance | Risk | Operability |
|---|---|---|---|---|---|
| 1. One service, in-process analytics | **Low.** One project, one deploy, three containers. Subtle parts: channel lifecycle, cache invalidation order. | **Lowest.** App + Postgres + Redis. Maps to one container service on AWS if ever wanted. | Cache-first redirect with no synchronous write. In-memory hit is the fastest path of any option. 100 ms looks comfortable but is unmeasured. | **Low–medium.** Clicks lost on crash or full queue (bounded, counted). Stale redirects across instances up to the in-memory TTL, which is zero risk on one instance. | One thing to deploy and debug. Needs metrics for queue depth and dropped clicks. Writer shares CPU with redirects. Warm cache survives a Postgres outage. |
| 2. Separate services + queue worker | **High.** Four deployables plus gateway, two queue interfaces, a replica, a migration job, a changed build target. Roughly 2–3× Option 1. | **Highest.** About nine containers locally. On AWS roughly $150–250/month minimum (unverified), about 3× a single service. | Best isolation of the redirect path. Gateway hop dominates, and API Gateway may add tens of ms (unverified). No faster per request than Option 1. | **Medium–high.** Replica-lag race (mitigated by tombstones, which rules out reactivation). Redis used as a queue needs its own instance. Ordered deploys for shared schema and events. Durability still only partial. | Four health checks and many metrics (replica lag, queue age, DLQ, hit ratio). Request ids needed to trace across services. Scales parts independently. |
| 3. Redis-centred hot path, periodic flush | **Medium–high.** One service, but a Lua limiter, rename-and-flush with idempotency, stream consumer groups, fail-safe ordering, circuit breaker, eviction tuning. Each needs its own failure test. | **Low–medium.** Same three containers as Option 1 (or four with two Redis roles). Redis memory is the cost driver. Durable managed Redis on AWS may cost more. | One awaited Redis read per redirect. Uniform across instances, but not faster than Option 1. A Redis outage falls back to Postgres per request, slower and unproven. | **Medium.** Counters live only in Redis until flushed. Eviction or OOM can silently drop clicks. Deactivate correctness depends on getting the ordering right. AOF may not exist on the managed target. | Two stateful systems to size and watch (memory, stream length, flush age). No extra process. App restarts lose no clicks. Immediate invalidation across instances. |

## Recommendation

**Build Option 1.**

It meets every stated requirement with the least code and the fewest moving parts, and it is the only option that fits the existing `target.yaml` without changes. The things Options 2 and 3 do better are independent scaling, crash-proof clicks and immediate invalidation across instances. None of those is a requirement today. Eventual consistency covers bounded click loss, and with one instance there is no staleness gap.

Neither alternative wins on the requirement that sets a number. All three are cache-first and keep writes off the redirect path. Option 1's in-memory hit is the fastest of the three.

Shape it so the later moves stay cheap:
- **Keep two narrow interfaces:** `IClickSink`, for where clicks go, and the cache lookup behind one small class. Moving to an external queue (towards Option 2) or to Redis counters (towards Option 3) then changes one folder.
- **Set a short in-memory TTL (30–60 s)** and write it into the spec as the staleness bound if a second instance ever runs. If immediate cross-instance invalidation becomes a requirement, switch the redirect lookup to Redis only, as in Option 3. That costs under a millisecond per redirect.
- **Take the cross-cutting fixes all explorers agreed on:**
  - app-owned login
  - Data Protection keys in Postgres
  - 302 with no caching
  - a regex route constraint plus a reserved-word skip list for codes
  - try/catch around the background loop
  - commit-then-evict ordering
  - soft delete
- **Use base62 rather than Sqids.** It is about 15 lines, has no dependency and is easy to test. Nothing in the requirements needs Sqids' blocklist.
- **Resolve the API gaps:**
  - `GET /v1/urls` is the caller's list, with `GET /v1/users/urls` as an alias of the same handler so the diagram stays honoured.
  - Deactivate is `PATCH /v1/urls/{shortcode}` with `{ "isActive": false }`, which also covers reactivation. Option 2 preferred a one-way `POST .../deactivate` only because its tombstones cannot be undone, and that reason does not apply here.

Do not adopt Option 1 without first running the redirect load test (Open questions, item 1). The 100 ms claim is the one hard number, and it is unmeasured.

## Open questions

Decisions for the user:
1. **Local login:** is app-owned username/password login acceptable, given OIDC is out of scope? Nothing can create a session without it.
2. **Target:** is local Docker the only target for the first change, with AWS deferred?
3. **Instances:** is a 30–60 s staleness window after deactivate acceptable if more than one instance ever runs, or is immediate invalidation needed? A "needed" answer moves the redirect lookup towards Option 3.
4. **List endpoint:** keep `GET /v1/urls` with `GET /v1/users/urls` as an alias, or drop the alias?
5. **Deactivate shape:** `PATCH` with `isActive`, or a one-way `POST .../deactivate`?
6. **Gone URLs:** 404 for both inactive and deleted, or 410 for deleted?
7. **Timing scope:** redirects only, or Write API calls too?
8. **Migrations:** who approves the first migration under the protected `Migrations/` path, and the `dotnet-ef` local tool manifest (`.config/dotnet-tools.json`, outside `src/**`)?
9. **Test environment:** does the Foreman test runner have Docker? If not, integration tests need a trait so they can be skipped there, and the remaining unit tests prove much less.

Unknowns that only testing settles:
1. **Redirect p99** against Compose (k6 or bombardier), with the cache warm, cold, and Redis stopped. This is the 100 ms question.
2. **How `HybridCache` behaves across two instances**, deactivating on A and redirecting on B. The explorer believes it does not broadcast in-memory invalidation, but did not verify.
3. **Real click loss** when the process is killed mid-burst, and whether the writer survives a Postgres outage without stopping the host.
4. **Exact defaults** the explorers flagged as "verify": host shutdown timeout, StackExchange.Redis timeouts.

## Draft user story

### Shorten, manage and redirect URLs in a single service with background click analytics

As a logged-in user, I want to create, list, view, deactivate and delete my own short URLs and see how often each is clicked, so that I can share short links and know how they are used. Anyone following a short link should be redirected quickly.

#### Acceptance criteria

- One ASP.NET Core project at `src/UrlShortener.Api` builds with `dotnet build src/UrlShortener.Api`, and `docker compose up` starts the api, Postgres and Redis locally.
- `POST /v1/auth/register`, `POST /v1/auth/login`, `POST /v1/auth/logout` and `GET /v1/auth/me` exist. Login sets an HttpOnly cookie that expires after 1 day (sliding). Unauthenticated calls to `/v1/urls` return 401, not a redirect.
- Sessions survive an api container restart, because Data Protection keys are stored in Postgres.
- `POST /v1/urls` accepts only absolute `http`/`https` URLs within a length cap, rejects the app's own host, and returns a short code of at least 6 base62 characters derived from the row id. No code equals a reserved route name.
- `GET /v1/urls` returns only the caller's URLs, newest first, with `page` and `count` parameters. `GET /v1/users/urls` returns the same result.
- `GET /v1/urls/{shortcode}` returns the URL's details and click count. `PATCH /v1/urls/{shortcode}` with `{ "isActive": false }` deactivates it. `DELETE /v1/urls/{shortcode}` soft-deletes it. Another user's code returns 404 for all three.
- `GET /{shortcode}` returns a 302 to the target URL for an active code, and a 404 for an unknown, inactive or deleted code. It is never a 301.
- After a deactivate or delete call returns, the next redirect for that code on the same instance returns 404, verified by an integration test against real Postgres and Redis.
- Each redirect records a click with a timestamp and server-side elapsed time. The click appears in the `clicks` table within a few seconds, with `elapsed_ms` recorded, verified by an integration test.
- The click buffer is bounded. When it is full, the redirect still succeeds without waiting, and a dropped-click counter increases.
- If Postgres is unavailable, the background writer logs and retries without stopping the host, and redirects for cached codes keep working.
- Redirects, URL creation and login are rate-limited, and return 429 with `Retry-After` when over the limit.
- A load test against the local Compose stack reports redirect p99 under 100 ms with the cache warm, and records the cold-cache result. The numbers are committed with the change.
- The initial EF migration is generated and reviewed as a separate, approved step.
