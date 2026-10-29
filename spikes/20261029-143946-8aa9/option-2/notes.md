# Option 2: separate Write and Read services, shared domain library, analytics through Redis Streams

## Starting point

- The repo has no application code. `src/`, `tests/` and `docs/` don't exist yet. `.foreman/target.yaml` points at `src/UrlShortener.Api` and `tests/UrlShortener.Tests`, and nothing creates them.
- `architecture-hld.svg` shows:
  - Client -> API Gateway (auth and rate limits) -> Write Service (generates the short URL, stores to the DB).
  - A load balancer in front of an auto scaling group of Read Services.
  - Redis.
  - Postgres on RDS, with write and read replicas.
- I could not check whether the .NET SDK or Docker is installed here, because those commands were blocked. Both are unknowns (see the end).
- I assumed "as Option 1" means the usual baseline: EF Core, `Npgsql.EntityFrameworkCore.PostgreSQL`, xUnit, `Microsoft.AspNetCore.Mvc.Testing`. I did not see Option 1.

## How it would be built

### Projects (all under `src/`)

| Project | Role | References |
|---|---|---|
| `UrlShortener.Domain` | Entities (`User`, `Url`), the short-code encoder, event contracts (`RequestEvent`), cache key and value format. No I/O. | none |
| `UrlShortener.Infrastructure` | EF Core `DbContext` and migrations, Redis helpers (cache, session ticket store, stream publisher), the shared request-timing middleware, auth scheme setup. | Domain |
| `UrlShortener.Write` | Register and login, `POST/GET /v1/urls`, `GET/DELETE /v1/urls/{code}`, deactivate, `GET /v1/users/urls`. | Domain, Infrastructure |
| `UrlShortener.Read` | Only `GET /{shortcode}` plus a health check. | Domain, a thin slice of Infrastructure |
| `UrlShortener.Analytics.Worker` | Generic Host `BackgroundService` consuming the stream. | Domain, Infrastructure |
| `UrlShortener.Gateway` | YARP host. The option doesn't name this project, but it is needed. | Redis and auth setup only |

- Add `src/UrlShortener.sln` and a single `tests/UrlShortener.Tests` project that references all of them. The test command then stays valid.
- Keep the `.sln` under `src/`. The constraints say code goes under `src/**`, and a root-level `.sln` falls outside the declared paths.
- Read should stay lean. If `Infrastructure` pulls in EF Core and every service references it, Read loads EF for nothing. Two ways to avoid that:
  - Split a small `UrlShortener.Infrastructure.Redis` out, or keep EF in a separate `Persistence` project.
  - Have Read query Postgres with plain `Npgsql` (`SELECT long_url, status FROM urls WHERE code=@c`). That's one query, so EF adds nothing.
  - I'd do the second one; it's the least extra structure.

### Request flows

**Create** (`POST /v1/urls`):

1. Gateway checks the cookie, rate limits, and routes to Write.
2. Write takes the next ID from its locally held block and encodes it to a short code.
3. Write inserts the row in Postgres.
4. After commit, Write does `SET url:{code} "A|{longUrl}"` in Redis (write-through, so the first redirect is already a hit).

**Redirect** (`GET /{code}`):

1. Gateway routes to Read. The route is `/{**catch-all}` with the lowest `Order`.
2. Read does `GET url:{code}` in Redis.
3. On a miss, Read runs one Postgres lookup and then `SET ... EX 300`. An unknown code gets a negative entry for about 30–60 s so scanners can't hammer Postgres.
4. Read returns a **302**, not a 301. A 301 is cached by browsers, which breaks both deactivation and click counting.
5. The timing middleware drops a `RequestEvent` into an in-memory bounded `Channel`. A background task batches these into `XADD analytics:events MAXLEN ~ 1000000 ...`.
   - The redirect never awaits Redis for analytics.
   - If the channel is full, the event is dropped and a counter is incremented. The redirect still works if Redis is down.

**Deactivate and delete:**

1. Write updates Postgres first.
2. Write then updates Redis.
   - Deactivate: set `url:{code}` to `"I"` (an inactive tombstone, with a TTL).
   - Delete: set the tombstone as well, or `DEL` the key.
3. Read treats `"I"` and a missing row as 404. Use 404 for both so the two cases look the same to callers.
4. Deactivate keeps the row. Delete removes the row. Code IDs are never reused, because they come from a monotonic counter.

**Analytics worker:**

1. `XREADGROUP GROUP analytics <hostname> COUNT 500 BLOCK 2000`.
2. Insert the batch into Postgres. `COPY` through Npgsql is fastest; EF `AddRange` is simpler.
3. Make inserts idempotent with a unique `stream_id` column and `ON CONFLICT DO NOTHING`.
4. In the same transaction, bump `Urls.ClickCount` (or a stats table), grouped by code.
5. `XACK` after commit.
6. On startup and periodically, run `XAUTOCLAIM` to recover entries a crashed consumer left pending.
7. After N failed deliveries, move the entry to a dead-letter stream and ack it.

**Short codes:**

- Use a Postgres sequence declared `INCREMENT BY 1000`.
- Each Write instance calls `nextval` once to get a block base, then mints `base..base+999` in memory with an `Interlocked` counter. Another `nextval` is taken when the block is used up.
- A restart wastes the unused tail of the block, which is harmless.
- Base62 encode with a minimum length of 6 characters. That keeps codes from colliding with real paths such as `v1` and `health`.
- Raw sequential IDs are enumerable. Run the ID through a cheap bijective scramble before encoding. A multiply modulo a prime or a small Feistel round does it, and it stays collision-free. The need for this is a judgement call; flag it for review.

**Auth:**

- The HLD gives no register or login endpoint, but cookie sessions need one. That is a gap: add `POST /v1/auth/register` and `/login` plus a logout, and confirm with the user.
- Passwords: `PasswordHasher<T>` from `Microsoft.Extensions.Identity.Core`. Full ASP.NET Identity is overkill.
- Use cookie auth with a custom `ITicketStore` backed by Redis.
  - The cookie carries only a session ID.
  - Expiry is 1 day, enforced both by cookie lifetime and by the Redis TTL.
  - Any instance can serve any request, and sessions can be revoked.
- If the cookie is instead the default encrypted ticket, the gateway and Write need a shared Data Protection key ring (`Microsoft.AspNetCore.DataProtection.StackExchangeRedis`).
- The gateway rejects unauthenticated calls early with a YARP `AuthorizationPolicy`. Write also has `[Authorize]` and validates the same ticket itself.
  - Write must not trust a forwarded user header, because anyone who can reach Write directly could forge it.
  - Both services use one shared `AddAppAuth()` extension from `Infrastructure`.
- Cookie auth with POST and DELETE needs CSRF protection. Use `SameSite=Lax` or `Strict` plus `HttpOnly` and `Secure`, and require a custom header on mutating calls. The HLD doesn't mention this, so it needs a decision.

**Rate limiting:**

- ASP.NET's built-in limiter is per process, so it doesn't meet "shared state".
- Options:
  - A small fixed-window limiter in the gateway: `INCR` plus `EXPIRE` in one Lua script, about 40 lines. Key it by user ID when logged in, otherwise by IP.
  - A community Redis limiter package. I haven't verified one, so treat that as unknown.
- Rate limiting the redirect route adds a Redis round trip and a gateway hop on the hot path. That is probably around 1–3 ms, which fits the 100 ms budget, but measure it.
- Decide whether `/{shortcode}` is rate limited at all. Leaving it out is simpler and faster.

**Timing capture:**

- A shared middleware in `Infrastructure` times each request with a `Stopwatch`. It runs on both Write and Read, so every request is covered.
- Record the route template (such as `/v1/urls/{shortcode}`), not the raw path, to keep cardinality low.
- Open question: does "request to response processing time" mean time at the service or end to end at the gateway? I'd capture at the services and leave the gateway out, then ask the user.

### Gateway routes (YARP config)

- `/v1/auth/**` -> Write, anonymous.
- `/v1/urls/**` and `/v1/users/**` -> Write, requires auth.
- `/{**catch-all}` -> Read, lowest priority, anonymous.

### Data model sketch (this needs approval, since migrations are protected)

- `users(id, email/username, password_hash, created_at)`.
- `urls(id bigint, code unique, long_url, user_id, status, created_at, click_count)`.
  - Index `(user_id, created_at desc)` for the paged list.
- `request_events(id, stream_id unique, service, route, method, status, duration_ms, short_code null, occurred_at)`.
- The id-block sequence.
- Do not add a foreign key from events to urls with a hard delete, or deleting a URL would cascade over its analytics. Decide whether delete should keep or drop analytics.
- Migrations belong in `Infrastructure/Migrations`, which is protected, so every schema change needs explicit approval.
- Only one process should apply migrations: a one-shot step or the Write service at startup. If three services all migrate on boot, they race.

### List endpoint

- `GET /v1/users/urls?sort=date&count=&page=` is page-based: `OFFSET (page-1)*count LIMIT count`.
- Cap `count` (say 100).
- `sort=date` is the only supported value; return 400 for others.
- Click counts come from `ClickCount`, which is eventually consistent.

### Rollout and local run

- Local: a `docker-compose` file with Postgres and Redis, plus `dotnet run` for each of the 4 processes. The compose file location is undecided: `src/` or `docs/` both fit the declared paths, a repo root file doesn't. .NET Aspire's AppHost would be tidier but adds a project and packages; I'd skip it to keep things simple.
- Production is described in the HLD only (ALB, auto scaling group for Read). No deployment work is in scope here.

### Packages (`.csproj`)

- `Yarp.ReverseProxy` (Gateway).
- `StackExchange.Redis` (Infrastructure and Read).
- `Microsoft.AspNetCore.DataProtection.StackExchangeRedis`, only if not using a Redis ticket store.
- `Npgsql` (Read), `Npgsql.EntityFrameworkCore.PostgreSQL` and `Microsoft.EntityFrameworkCore.Design` (Infrastructure).
- `Microsoft.Extensions.Identity.Core` (password hashing).
- Tests: `xunit`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql` and `Testcontainers.Redis`.
- I can't check versions offline. Pin to the latest stable matching the installed SDK.

## `target.yaml` impact

- `dotnet build src/UrlShortener.Api` has no sensible meaning here.
  - If `UrlShortener.Api` were the gateway, the build would only compile the gateway and anything it references. That would not build Write, Read or Worker, and a green build would be misleading.
  - Referencing all of them from Api just to satisfy the command would be a hack.
- Change the build command on purpose to `dotnet build src/UrlShortener.sln`.
- `dotnet test tests/UrlShortener.Tests` can stay as is.
- `paths` and `protected` need no change. `dependency_manifests: **/*.csproj` already covers the extra projects, but each project now declares its own packages, so more files change per feature.

## Assessment

| Axis | Assessment |
|---|---|
| Complexity | High compared with a single-service design. 6 projects, 4 deployables, plus Postgres and Redis. Hard parts: the cache and DB consistency, the auth flow across gateway and Write, and stream consumer correctness. Each is small alone, but together they take several days of work and many tests. |
| Cost | Local cost is zero. In production, 4 service types and one Redis for three jobs. Redis memory is small: URL cache plus sessions plus a capped stream. |
| Performance | Redirect = gateway hop + Redis `GET` (about 1 ms local). A cache miss adds one indexed Postgres lookup (single digit ms). Both fit well under 100 ms. This is only an estimate; I haven't measured it. Read scales independently, as the HLD intends. |
| Risk | See the list below. |
| Operability | Needs health checks per service, a lag/pending metric for the stream (`XINFO GROUPS`), and a drop counter for the analytics channel. More logs and processes to follow than one service. |

### Main risks

1. **Stale cache after deactivate or delete.**
   - If Postgres commits but the Redis update fails, the URL keeps redirecting until the TTL expires.
   - Also, a Read cache miss can repopulate the old value in the gap between the Postgres commit and Write's Redis update.
   - Mitigations: always set a TTL, so staleness is bounded (5 min is a guess). Write retries the Redis update. Write sets a tombstone rather than deleting the key.
   - Strict "stops immediately" needs an outbox or a version check, which is more work. This requirement needs a decision on how strict "stops redirecting" must be.
2. **One Redis with three jobs.**
   - A cache wants LRU eviction, but sessions and the stream must not be evicted.
   - `allkeys-lru` could log everyone out or drop click events.
   - Locally one instance is fine. In production use separate instances, or put TTLs on all cache keys and use `volatile-*` policies, which still risks evicting sessions.
   - Enable AOF persistence if sessions and clicks matter. If Redis dies, everyone is logged out and the unread part of the stream is lost.
3. **Click loss.**
   - Events in the in-memory channel are lost on a crash. Events past `MAXLEN` are trimmed if the worker is down long enough.
   - "Eventually consistent" does not cover permanently lost clicks. Say plainly that this design is at-least-once from the stream onward, but best-effort before it.
4. **The analytics table grows with every request**, including Write calls. It needs retention or partitioning, which I'd list as a later improvement.
5. **Auth has several moving parts:** the shared ticket store, the CSRF decision, and the missing login endpoint.
6. **Migration races** between services (see above).
7. **Tests that prove less than they seem.** Unit tests with fakes won't prove real Redis stream behaviour (consumer groups, `XAUTOCLAIM`, trimming). The only honest check is Testcontainers tests, which need Docker wherever `dotnet test` runs. Without Docker, those tests have to be skipped, and then nothing exercises the Redis and Postgres paths.

## Compared with a single-process design (briefly)

- Gains: the redirect scales on its own, and the layout matches the HLD.
- Costs: everything above that isn't pure business logic, which is mostly wiring.
- If the grader or user only needs the HLD to be matched in behaviour, much of this could be done inside one process with two route groups. That choice belongs to the comparison across options, not to this note.

## What I would test first

1. **Redirect latency.** Run a throwaway Read + Redis + Postgres with a load tool (`bombardier`, `k6` or `NBomber`). Check p99 with the cache hot, with a cold miss, and through the YARP hop. This is the one number the option must prove.
2. **Deactivate race.** Deactivate while redirects are running, and measure how long it takes for redirects to stop. Also test with the Redis update failing.
3. **Stream consumer.** Start two workers, kill one mid-batch, and confirm there are no duplicates and no losses (the `stream_id` unique key plus `XAUTOCLAIM`).
4. **Session across instances.** Log in through the gateway, hit Write on two instances, and confirm the cookie works on both and that logout revokes it.
5. **Redis down.** Confirm a redirect still works via Postgres, and that create and login fail cleanly.

## Unknowns and questions

- Is the .NET SDK installed, and which version? Is Docker available? Both are unverified here (commands were blocked).
- Login and register endpoints: confirm they are wanted, and the user model (email or username).
- "Delete": hard delete, with or without removing analytics rows?
- How strict is "deactivated must stop redirecting"? TTL-bounded staleness, or immediate?
- Where should "processing time" be measured: service or gateway?
- Should `/{shortcode}` be rate limited?
- Where does the compose file go (`src/` or `docs/`), and may `target.yaml` get the new build command?
- Does the schema above get approval, given the protected `Migrations` path?
