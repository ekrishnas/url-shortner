# Analysis: components, packages and low-level design for the URL shortener

Change id: explore-components-packages-and-low-leve-1 (issue #1)

## Context

So far the repo has the high-level design (`architecture-hld.svg`) and the Foreman target config, and nothing else. There is no code, no specs and no `src/`, `tests/` or `docs/` folders. The HLD describes an authenticated URL shortener:

- Logged-in users create short codes, list them with page-based paging, see click counts, deactivate them and delete them.
- Anyone with a code gets redirected.
- Redirects must take under 100 ms.
- Every request's processing time must be recorded.
- Eventual consistency is acceptable.

The HLD names an API Gateway, a Load Balancer, a Write Service, a Read Service in an Auto Scaling Group, Redis, and Postgres on RDS with read replicas. It does not say:

- where click and timing data goes,
- what Redis is for,
- whether AWS is the target now or later,
- whether Write and Read are separate codebases.

`.foreman/target.yaml` fixes the build to `dotnet build src/UrlShortener.Api` and the tests to `dotnet test tests/UrlShortener.Tests`. It also protects `**/Migrations/**`.

This document compares three ways to answer those open questions. The goal is to pick one before any code is written.

What was actually checked: the Option 1 explorer confirmed .NET SDK 10.0.403 on this machine. No explorer could check whether Docker is available. Everything said about performance below is an estimate. Nothing has been built or measured.

## Assumptions

The framing gave no assumptions. This analysis adds these, and each one should be confirmed or rejected:

1. **Local first.** The first deliverable runs on a developer machine with Postgres and Redis in Docker. Production deployment (AWS or anything else) comes later and is not part of the first feature.
2. **Moderate scale.** The aim is a correct, measurable service, not one already sized for billions of redirects. Nothing in the repo or HLD states a traffic number.
3. **The HLD's Write/Read split is logical.** It describes roles that can be scaled separately. It does not require separate codebases from day one.
4. **Fixed commands stay fixed** unless there is a good reason to change them, since every change to `target.yaml` is a deliberate decision.
5. **Login endpoints will be added.** Cookie sessions can't work without register, login and logout endpoints, even though the HLD doesn't list them. All three explorers raised this gap.

## Options

### Option 1: One ASP.NET Core app, two roles, in-process background analytics, Docker locally

- **Shape:** a single minimal-API project, `UrlShortener.Api`, with feature folders (Urls, Redirect, Users, Analytics, Data). A config flag `App:Role = All | Write | Read` controls which endpoint groups are mapped. The same image can later be deployed twice to match the HLD's split.
- **Redirect:** Redis lookup, then Postgres fallback, then a 302 response.
- **Analytics:** timing middleware and the redirect handler call `TryWrite` on a bounded `Channel`. A `BackgroundService` batch-writes `click_events` and upserts `url_stats` in one transaction.
- **Short codes:** Postgres sequence plus Sqids, with a blocklist for reserved routes.
- **Redis:** used only for the lookup cache and the Data Protection key ring.
- **Auth and rate limiting:** built-in cookie auth (1 day, no sliding expiry) and the built-in per-process rate limiter.
- **Fit with target.yaml:** works with the commands as they are. The Dockerfile and compose file can go under `src/`.
- **Explorer verdict:** feasible and small. The main risks are HLD gaps, protected migrations and the Docker dependency in tests, not performance.

### Option 2: Separate Write, Read, Analytics Worker and Gateway services sharing libraries, analytics through Redis Streams

- **Shape:** six projects:
  - Domain and Infrastructure (shared libraries),
  - Write,
  - Read (redirect only, plain Npgsql),
  - Analytics.Worker,
  - Gateway (YARP).

  That is four deployables plus Postgres and Redis.
- **Redis does three jobs:**
  - the cache, with tombstones on deactivate or delete,
  - a session ticket store and a shared rate-limit counter,
  - a Redis Stream (`XADD`) that the worker reads with a consumer group. The worker uses idempotent inserts, `XAUTOCLAIM` to recover pending entries, and a dead-letter stream.
- **Short codes:** each Write instance reserves a block of IDs from the sequence, scrambles the ID, then base62-encodes it.
- **Fit with target.yaml:** the build command must change to `dotnet build src/UrlShortener.sln`. A green `dotnet build src/UrlShortener.Api` would mean nothing.
- **Explorer verdict:** matches the HLD literally and scales the redirect path on its own. It costs several days more work. Redis carries three jobs that want conflicting eviction policies.

### Option 3: Designed for AWS managed services, with SQS and a separate analytics store

- **Hosting and data:** ECS Fargate, ALB and an HTTP API Gateway. ElastiCache Redis, plus RDS Postgres Multi-AZ with a read replica.
- **Analytics:** the redirect hands off to a channel. A publisher batches events to SQS. A .NET Lambda sums the clicks and writes DynamoDB counters, so there is no second Postgres and no extra migration.
- **Timings:** OpenTelemetry, sent through an ADOT sidecar to CloudWatch and X-Ray.
- **Short codes:** random 7-character base62 codes with retry on unique violation.
- **Infrastructure:** CDK for .NET (needs Node.js), with LocalStack for local development.
- **Fit with target.yaml:** the build command still works for the Api. Infra and the Lambda are outside the build and only compile through tests.
- **Explorer verdict:** the best failure isolation and the most realistic at scale. It is also the most to build and run, and the most expensive (about $150–300/month fixed, estimated from memory). Much of it can't be verified without an AWS account. It also brings a stale-replica race on deactivate that needs tombstones.

## Trade-off matrix

| Option | Complexity | Cost | Performance | Risk | Operability |
|---|---|---|---|---|---|
| 1. One app, two roles, in-process analytics | **Low–moderate.** 1 project, 1 process, about 1.5–2.5k lines including tests. Hardest parts are cache invalidation and the batch writer. | **Lowest.** Local: app + Postgres + Redis. Production: one service (or two copies) plus managed Postgres and Redis. No queue to pay for. | Redirect = 1 Redis GET (miss: + 1 indexed Postgres read). Estimated low single-digit ms, so wide headroom under 100 ms. Unmeasured. | Clicks still in the channel are lost on a crash (accepted). Stale cache if an invalidation fails (bounded by TTL). Per-instance rate limits. Cookies can't be revoked. Read role still needs the primary DB for analytics. | Easiest. `docker compose up`, one log stream, one health check. Channel depth and drop metrics show the main failure. |
| 2. Separate services + Redis Streams | **High.** 6 projects, 4 deployables. Consistency between gateway auth, cache and DB, plus stream-consumer correctness. Several days more work than Option 1. | **Moderate.** Free locally. Production has 4 service types. Redis needs persistence (AOF) and ideally separate instances for its three jobs. | Redirect = gateway hop + Redis GET. Estimated ~1–3 ms more than Option 1, still well under 100 ms. Read scales on its own. Unmeasured. | Redis holds three jobs with conflicting eviction policies (sessions or stream entries could be evicted). Cache/DB race on deactivate. Migrations race across services. Build command must change. | Hardest locally: 4 processes to run and watch, stream lag metrics, a dead-letter stream to handle. |
| 3. AWS managed services, SQS + DynamoDB | **Highest.** ~5 projects, 2 extra toolchains (CDK/Node, LocalStack), 4 AWS data services, IAM and networking. | **Highest.** Roughly $150–300/month fixed before traffic (unverified estimate). API Gateway on the redirect path adds about $1 per million redirects. | Redirect stays off SQS, so it should be well under 100 ms. If redirects pass through API Gateway, add roughly 10–30 ms (from memory). Unmeasured. | Replica lag can bring back a deactivated code unless tombstones are used. SQS duplicates mean approximate counts. Multi-region failover isn't designed. IAM and network mistakes only show on a real deploy. | Strongest in production (managed services, alarms, the DLQ isolates analytics failures). Weakest to verify from a Foreman run: only `cdk synth` and emulators. |

## Recommendation

**Choose Option 1.** Keep Option 2's service split and Option 3's AWS deployment as later steps that Option 1 does not block.

Reasons:

1. **It meets every hard constraint for the least work.**
   - The 100 ms redirect budget is met with no synchronous analytics write.
   - Every request is timed by middleware and by the built-in OpenTelemetry duration histogram.
   - Deactivate and delete stay distinct.
   - The endpoints and cookie auth match the HLD.
   - It works with the `target.yaml` commands as they stand.
2. **Its main weakness is accepted.** Losing in-flight click events on a crash is allowed by "eventual consistency is acceptable". Options 2 and 3 are also best-effort before their queue or stream, because both buffer in an in-process channel first. So the durability gain from the extra infrastructure is smaller than it looks.
3. **It keeps the HLD's shape.** The `App:Role` flag and the separate read and write connection strings let the same image run as "write" and "read" later. Moving to separate services (Option 2) or to AWS hosting (Option 3) changes deployment, not the domain code.
4. **It is the most verifiable.** One process with Testcontainers integration tests can prove the important behaviour end to end: deactivate stops redirects, clicks reach `url_stats`, and cookies expire. Option 3 can't be proven without AWS, and Option 2 needs much more test wiring to reach the same confidence.

Things to adopt from the other explorers' notes:

- Answer redirects with 302, never 301 (all three agree).
- **Deactivate and delete stop redirects at once:** delete or overwrite the Redis entry in the same request that updates Postgres. Also give every cache entry a short TTL as a backstop.
- Use direct StackExchange.Redis rather than HybridCache's per-instance L1, so a deactivate isn't missed by another instance.
- Set Redis timeouts to tens of milliseconds and fall back to Postgres on any Redis error.
- Store the Data Protection keys in Redis, not in an EF table, to avoid an extra migration.
- Put no foreign key on `click_events`.
- Run migrations only when `Database:MigrateOnStart` is true (local only).
- Integration tests that need Docker must skip visibly when Docker is absent, and reports must say so.

Two conditions apply before building:

- The schema (`users`, `urls`, `click_events`, `url_stats`) must be approved, because Migrations are protected.
- Docker availability in the Foreman environment must be confirmed (see Open questions).

## Open questions

1. **Docker in the Foreman environment.** Is Docker available where `dotnet test` runs? No explorer could check. If it isn't, the integration tests will skip and "tests pass" will only mean "unit tests pass".
2. **Login endpoints.** Is it OK to add `POST /v1/users` (register), `POST /v1/users/login` and `POST /v1/users/logout`? Should users be identified by email or username?
3. **Deactivate endpoint.** Is it OK to add `PATCH /v1/urls/{shortcode}` with `{ "isActive": false }`? Can a code be reactivated?
4. **Two list endpoints.** What is the difference between `GET /v1/urls/` and `GET /v1/users/urls?sort=date&count=&page=`? If there is none, one can be a thin alias.
5. **Timing storage.** For non-redirect requests, is the OpenTelemetry `http.server.request.duration` histogram enough? Or must every request be stored per row in Postgres?
6. **Delete semantics.** When a URL is deleted, should its click history be deleted too? For deactivated codes, return 404 or 410?
7. **Schema approval.** Should the table definitions go into `docs/schema.md` for review before any migration is generated?
8. **File locations.** Is it OK to put the Dockerfile at `src/UrlShortener.Api/Dockerfile` and the compose file at `src/compose.yaml`? Is a `.sln` needed, and if so, under `src/`? Root-level files fall outside the allowed paths.
9. **Production target.** Is AWS the eventual target, and is there a traffic figure? That decides when (or whether) the Option 2 or Option 3 steps are worth doing.
10. **Rate limiting.** Are per-process limits acceptable for now, with production limits at the gateway? Should the redirect path be rate limited at all?
11. **Measured latency.** Redirect p99 has not been measured for any option. The first build should include a load test covering cache hit, cache miss, Redis down and the first requests after a cold start.

## Draft user story

### Build the URL shortener as one ASP.NET Core app with background analytics

As a **signed-in user of the URL shortener**, I want **to create, list, deactivate and delete short links, and see their click counts, while anyone with a link is redirected quickly**, so that **I can share short URLs and see how they are used, without slow redirects**.

#### Acceptance criteria

- The solution is one project, `src/UrlShortener.Api` (net10.0, minimal APIs, feature folders). `dotnet build src/UrlShortener.Api` and `dotnet test tests/UrlShortener.Tests` both succeed with no change to `target.yaml`.
- The table definitions for `users`, `urls`, `click_events` and `url_stats` are written to `docs/schema.md` and approved before any file under `Migrations/` is created.
- Users can register, log in and log out (endpoints as agreed in the open questions). The session is a cookie with a 1-day fixed expiry, `HttpOnly` and `SameSite=Lax`. Unauthenticated API calls get 401, not a redirect to a login page.
- `POST /v1/urls/` accepts only absolute `http`/`https` URLs and returns a short code. Codes come from a Postgres sequence encoded with Sqids, are never reused, and never equal a reserved route (for example `v1` or `health`).
- `GET /v1/urls/`, `GET /v1/urls/{shortcode}` and `GET /v1/users/urls?sort=date&count=&page=` return only the caller's URLs, with click counts. Paging is page-based, newest first. `count` defaults to 20 and is capped at 100. Any `sort` value other than `date` returns 400. Another user's code returns 404.
- `GET /{shortcode}` returns 302 to the target URL for active codes, and 404 for unknown, deactivated or deleted codes. It reads Redis first and falls back to Postgres, including when Redis errors or times out.
- Deactivating a code keeps its row but stops redirects at once (the cache entry is removed or overwritten in the same request). Deleting removes the row and also stops redirects at once.
- Redirects never write to the database synchronously. Click and timing events go into a bounded in-process channel. A background service writes them in batches to `click_events` and `url_stats`. Dropped events and channel depth are exported as metrics.
- Every request's processing time is recorded through timing middleware and OpenTelemetry request-duration metrics.
- `App:Role` (`All` | `Write` | `Read`, default `All`) selects which endpoint groups are mapped.
- `docker compose up` (compose file under `src/`) starts the app, Postgres and Redis locally.
- Tests:
  - Unit tests cover code generation, URL validation, paging limits, batch aggregation and cache fallback.
  - Testcontainers integration tests cover create → redirect → deactivate → 404, delete, ownership, and clicks eventually showing in counts.
  - If Docker is unavailable, the integration tests are skipped and the test report says so.
- A load test shows redirect p99 under 100 ms for cache hit and cache miss, and the results are recorded in `docs/`.
