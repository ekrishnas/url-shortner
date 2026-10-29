# Option 3: AWS managed services, SQS queue, separate analytics store

## Starting point (evidence from the repo)

- There is no `src/`, `tests/` or `docs/` yet. The only product artefact is `architecture-hld.svg`, so everything below is a new build, not a change to existing code. I could not check the .NET version, so the package choices assume a current LTS (.NET 8 or later) and need confirming.
- `.foreman/target.yaml` fixes the commands: `dotnet build src/UrlShortener.Api` and `dotnet test tests/UrlShortener.Tests`. `**/Migrations/**` is protected.
- The HLD already looks like this option: gateway (auth and rate limits) -> load balancer -> Write Service and Read Service in an auto scaling group -> Redis and Postgres RDS with write and read replicas. The HLD has no queue and no separate analytics store, so those are the additions here.
- Gap that applies to every option: the HLD lists no login or register endpoint, but the cookie session needs one. Someone has to decide how users are created and signed in (username and password, or a stub). I have not designed that here.

## Components

| Piece | What it is |
|---|---|
| `src/UrlShortener.Api` | ASP.NET Core app. Endpoints exactly as the HLD lists them, cookie auth, EF Core (Npgsql), Redis, SQS publisher. |
| `src/UrlShortener.Contracts` | One record, `ClickEvent(eventId, shortCode, occurredAt, durationMs)`. Shared by the Api and the consumer. |
| `src/UrlShortener.ClickProcessor` | .NET Lambda, triggered by SQS. Sums clicks per short code across the batch and writes the counters. |
| `src/UrlShortener.Infra` | AWS CDK for .NET. Stacks: network, data, app (ECS, ALB, API Gateway), analytics (SQS, DLQ, Lambda, DynamoDB), alarms. |
| `tests/UrlShortener.Tests` | Unit tests against fakes. Integration tests are tagged and separate (see Testing). |

The Api exposes small interfaces: `IUrlRepository`, `IUrlCache`, `IClickPublisher`, `IClickReader`. Each AWS or Redis piece is an adapter behind one. With no queue configured the app uses a no-op publisher, so it runs on a laptop without AWS.

### Analytics store: recommendation

The option offers a time-partitioned Postgres table or DynamoDB counters. I would pick **DynamoDB counters**:

- Table `clicks`, key `shortCode` (partition) and `bucket` (sort: `TOTAL` or `D#2026-10-29`). The consumer does `ADD count :n`.
- On-demand billing, so there is no idle cost and no capacity planning.
- The Lambda needs no VPC, so no NAT gateway, no RDS Proxy, and no connection exhaustion from Lambda concurrency.
- It creates no EF migration, so the protected `Migrations/` folder is untouched. The table is defined in CDK.
- A hot short code could hit the per-item write limit (about 1,000 writes per second). Summing per code inside each SQS batch before the write avoids that.
- It cannot answer ad hoc time-series questions. If raw events are wanted later, partitioned Postgres is the alternative (below).

A "separate" Postgres only meets the "never touches the write primary" goal if it is a **separate RDS instance**. A second database on the same instance shares CPU, IO and connections with the primary. Partitioned Postgres therefore adds an instance (about $12+/month, double with Multi-AZ), a second DbContext and Migrations folder (both need approval), and a job to create partitions (pg_partman or a scheduled task).

## Data flow

**Redirect `GET /{shortcode}`**
1. A middleware starts a `Stopwatch`.
2. Redis `GET code:{shortcode}` with a tight timeout (about 50 ms, `AbortOnConnectFail=false`).
3. On a miss, query the read replica, then populate Redis. Cache a short-lived negative entry for unknown codes so scanners and `favicon.ico` do not hit the replica.
4. If the entry is active, return **302** (not 301; browsers cache 301 forever, which defeats deactivation and click counting). Otherwise return 404.
5. Write a `ClickEvent` to a bounded in-process `Channel<ClickEvent>` and return. A hosted service drains the channel and calls `SendMessageBatch` (10 messages per call). The redirect never awaits SQS. If the channel is full it drops the event and increments a "dropped clicks" metric.
6. Hot path cost is one Redis round trip (about 1 ms in-VPC), or Redis plus one replica query on a miss. The 100 ms budget is not at risk if SQS stays off the path. I have not measured this.

**Create `POST /v1/urls/`**
1. Cookie auth, then validate the URL.
2. Generate a 7-character base62 code with `RandomNumberGenerator` and rejection sampling (bytes of 248 or more are discarded, since 256 mod 62 is not 0, to avoid modulo bias).
3. Insert on the primary. On Npgsql `23505` (unique violation) retry with a new code, up to about 5 times.
4. Write the entry to Redis and return it. 62^7 is about 3.5e12, so at 1e9 stored URLs the chance a given insert collides is about 0.03%.

**Deactivate and delete**
- The two are different operations. Deactivate sets `is_active=false` and keeps the row. Delete removes the row (delete the click counter too, or leave it as harmless orphan data).
- **Stale-replica race:** if we only `DEL` the Redis key, a later cache miss can read the not-yet-updated replica and put the active entry back. Fix: on deactivate or delete, write a short-lived **tombstone** to Redis (`inactive`, TTL longer than worst-case replica lag, e.g. 5 minutes) instead of deleting. Cache fills from the replica must not overwrite a tombstone (`SET NX`).
- `DEPRECATED path:` the redirect must never fall back to the replica for a code that has a tombstone. This is the main correctness risk specific to this option, and it needs a test.

**Reads**
- `GET /v1/urls/{shortcode}`: the replica for the URL, plus a DynamoDB `GetItem` for the click count.
- `GET /v1/users/urls?sort=date&count=&page=`: the replica with `ORDER BY created_at DESC, id DESC LIMIT/OFFSET` and an index on `(user_id, created_at DESC, id)`. Cap `count` at 100. The click counts then come from one DynamoDB `BatchGetItem` (100 keys max), because the two stores cannot be joined in SQL.
- Replica lag means a user can create a URL and not see it in the next list call (read-your-writes). Options: accept it (the HLD says eventual consistency), or send a user's list reads to the primary for a few seconds after they write. I would accept it at first and say so in the spec.

## Auth and the gateway

- Cookie auth: `HttpOnly`, `Secure`, `SameSite=Lax`, `ExpireTimeSpan` 1 day, no sliding expiry.
- **Shared data-protection keys are required.** With more than one Fargate task, or after any deploy, each task would otherwise use its own key ring and reject the others' cookies. Persist keys to Redis (`Microsoft.AspNetCore.DataProtection.StackExchangeRedis`) or to Postgres. Easy to miss, and it only fails once there is more than one task.
- Behind the ALB, TLS ends at the ALB, so enable `ForwardedHeaders` or `Secure` cookies and redirects misbehave.
- POST and DELETE with cookie auth need CSRF protection (antiforgery or a required custom header).
- API Gateway: use an HTTP API with a VPC link to the ALB (a REST API needs an NLB). It throttles per route and stage. It does not know about cookie sessions, so the app does the real auth check. The HLD says the gateway "handles auth"; here it would only throttle.
- **Decision for the user:** every redirect would pass through API Gateway, which adds latency (roughly 10-30 ms, from memory) and about $1 per million requests, on the highest-volume path. A cheaper layout routes `/v1/*` through the gateway and `/{shortcode}` straight to the ALB (or CloudFront). That is a small CDK decision, but it changes what "throttling in front" covers.

## Timing capture (OpenTelemetry)

- Use `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `.Http`, `.StackExchangeRedis`, the Npgsql tracing source, and the OTLP exporter. The Api sends OTLP to an **ADOT collector sidecar** in the Fargate task. The collector exports metrics to CloudWatch (`awsemf`) and traces to X-Ray.
- The option names `AWS.Distro.OpenTelemetry`. I could not verify that package name offline. The OTLP exporter plus the sidecar needs no AWS-specific package in the app, and the same code works locally against a console exporter or a collector container. I would use that.
- "Every request's processing time is captured": the ASP.NET Core `http.server.request.duration` histogram counts every request, in aggregate. Per-request detail needs traces (sampled) or a log line. The click event also carries `durationMs`, so redirect timings are per-request on the analytics path too.
- Metric dimensions must use the **route template**, never the raw path. A shortcode dimension would create millions of CloudWatch metrics and a very large bill.

## Packages (declared in `.csproj`)

- Api: `Npgsql.EntityFrameworkCore.PostgreSQL`, `StackExchange.Redis`, `AWSSDK.SQS`, `AWSSDK.DynamoDBv2`, `AWSSDK.Extensions.NETCore.Setup`, the `OpenTelemetry.*` set above, `Microsoft.AspNetCore.DataProtection.StackExchangeRedis`.
- ClickProcessor: `Amazon.Lambda.Core`, `Amazon.Lambda.SQSEvents`, `Amazon.Lambda.Serialization.SystemTextJson`, `AWSSDK.DynamoDBv2`.
- Infra: `Amazon.CDK.Lib`.
- Tests: xUnit, `Testcontainers.PostgreSql`, `Testcontainers.Redis`, `Testcontainers.LocalStack` (integration only), `Amazon.Lambda.TestUtilities`.
- Versions are unchecked (no network).

## Infrastructure as code

- Prefer **CDK for .NET**: same language, and it can be tested with the assertions library. Cost: it needs Node.js installed (jsii, and the `cdk` CLI for synth/deploy). Terraform avoids Node but adds HCL and a separate binary. Either adds a toolchain that is not just `dotnet`.
- Put it under `src/UrlShortener.Infra` to stay within the "code under `src/**`" rule. It is not referenced by the Api, so `dotnet build src/UrlShortener.Api` ignores it. A decision is needed on whether Infra builds in CI, which would mean updating the build command on purpose.
- The same applies to `ClickProcessor`: the build command compiles only what the Api references. The test project references it, so `dotnet test` still compiles it.
- Resources:
  - VPC with private subnets for ECS, RDS and Redis. VPC endpoints for ECR, S3, CloudWatch Logs, SQS and Secrets Manager reduce NAT cost.
  - ECS Fargate service plus the ALB. HTTP API with a VPC link.
  - RDS Postgres Multi-AZ plus one read replica. ElastiCache Redis with a replica.
  - SQS queue, a DLQ with `maxReceiveCount` of about 5, the Lambda event source (batch size about 10, `ReportBatchItemFailures`), and the DynamoDB table.
  - Task roles scoped narrowly: the Api gets `sqs:SendMessage` on one queue and read on one table. Secrets come from Secrets Manager as ECS secrets.
- The HLD shows separate Read and Write services. The cheap way is one image deployed twice (or once at first), with ALB path rules splitting traffic, so reads scale independently. Start with one service.

## Migrations and schema (needs approval)

- Postgres tables: `users` and `urls` (`id`, `short_code` with a **unique index**, `original_url`, `user_id`, `is_active`, `created_at`), plus the list index above. One DbContext, one Migrations folder. With DynamoDB there is no second one.
- Because `**/Migrations/**` is protected, the schema work waits for explicit approval.
- Run migrations as a one-off ECS task or a deploy step, not on app startup, or every new task races to migrate.
- Two contexts for primary and replica (a `NoTracking` read context on the replica connection string) keeps replica use explicit.

## Rollout order

1. Api skeleton with Postgres, cookie auth, create/get/list/deactivate/delete, behind the interfaces, with fakes. Passes `dotnet build` and `dotnet test` with no Docker.
2. Redis adapter, tombstone logic, negative cache.
3. Click channel, SQS publisher, `ClickProcessor` with `IClickStore` (DynamoDB adapter and an in-memory fake).
4. OTel wiring, health checks.
5. CDK stacks. Deploy to a staging account first. ECS rolling deploy with the deployment circuit breaker on.
6. Alarms and a short runbook under `docs/`.

## Assessment

**Complexity: highest of the usual options.** Roughly 5 projects, 2 more toolchains (CDK/Node, Docker), 4 AWS data services, and several non-obvious correctness items (tombstones, shared key ring, idempotency, route-template metric labels). The application code is not unusual; the volume comes from infra and the edge cases around caches and replicas.

**Cost** (rough list-price estimates from memory, no network to verify; check with the AWS calculator):
- Fixed costs dominate. A small but honest Multi-AZ setup (NAT or VPC endpoints, ALB, 2 small Fargate tasks plus sidecars, Multi-AZ RDS plus a replica, Redis with a replica, CloudWatch) lands around $150-300/month before any traffic.
- Per-request costs are small. SQS batched sends are about 1 request per 10 clicks. DynamoDB on-demand and Lambda are cheap at this scale. The one per-request item that matters is API Gateway on the redirect path (about $100 per 100M redirects).
- Local development is free but needs Docker.

**Performance:** the redirect design (Redis, replica fallback, non-awaited batched SQS) should be comfortably under 100 ms. Risks are a slow Redis (hence the 50 ms timeout and fallback to the replica) and cold caches after a deploy. Unmeasured.

**Risk**
- Click loss: the in-process channel loses events on a task crash. On SIGTERM the publisher must drain (raise the ECS `stopTimeout`). Acceptable under eventual consistency, but counts are "at most" accurate, not exact.
- Duplicates: SQS standard queues deliver at least once, so counters can slightly over-count. Making them exact needs a dedupe item per `eventId` in a DynamoDB transaction, which doubles write cost. I would accept approximate counts and say so.
- Region failover: random codes avoid a shared sequence, but the claim "creation works after region failover" also needs a writable Postgres in the other region (cross-region replica promotion). This option as written is single-region Multi-AZ. Multi-region is not designed or costed here.
- Unverifiable in a Foreman run: nothing can be deployed from the agent environment. The best automated check is `cdk synth` and assertion tests. IAM and networking mistakes would only show on a real deploy.

**Operability**
- Alarms: ALB 5xx and p99 `TargetResponseTime`; SQS `ApproximateAgeOfOldestMessage` and DLQ depth; RDS replica lag and connections; Redis evictions and memory; Lambda errors and throttles; the app's "dropped clicks" metric.
- If SQS or the Lambda is down, redirects keep working and clicks queue (4-day default retention). If Redis is down, redirects fall back to the replica, which must be sized for that.
- Health endpoints live at `/_health/live` and `/_health/ready`. The leading underscore is not base62, so they can never collide with a short code on the `/{shortcode}` route.

## Testing

- `dotnet test tests/UrlShortener.Tests` should pass with **no Docker and no AWS**: unit tests with fakes for the repository, cache, publisher and click store. These cover code generation and collision retry, deactivate versus delete, tombstone behaviour, the processor's per-batch summing and partial-batch failure reporting, pagination, and cookie expiry.
- Integration tests (Testcontainers Postgres and Redis, LocalStack SQS/DynamoDB) are tagged `Integration` and run in a separate step. They are skipped when Docker is unavailable.
- Be honest about what the default run proves: the logic, not the real behaviour of Postgres, Redis, SQS or DynamoDB. EF Core InMemory and SQLite do not reproduce Npgsql's unique-violation behaviour, so the collision-retry path needs the Postgres container test to be trusted.
- LocalStack covers SQS and DynamoDB well enough for contract checks. It does not emulate ADOT/CloudWatch or real IAM, so alarms, permissions and collector config remain untested locally.

## Unknowns and what I would test first

1. **Is Docker available where `dotnet test` runs?** This decides whether integration tests can be part of the default command or must stay separate. Check first.
2. **Redirect p99** with Testcontainers Postgres and Redis and the non-awaited publisher, using a load tool (k6 or similar), including cache-miss and Redis-down cases.
3. **Deactivate race:** a test that deactivates a code while the replica is stale (or simulated stale) and asserts the redirect still returns 404.
4. **Duplicate and poison messages** through the processor: duplicate `eventId`, a malformed body, and a DynamoDB failure mid-batch, with DLQ routing.
5. **Auth across two app instances:** run two instances against a shared key ring and confirm a cookie from one is accepted by the other.
6. Open questions for the user: login mechanism; whether `/{shortcode}` goes through API Gateway; whether raw click events are ever needed (DynamoDB counters vs partitioned Postgres); whether multi-region failover is a real requirement; whether Infra belongs in the default build.

## Verdict

A sound production target that matches the HLD, with the best failure isolation (a failing analytics path cannot slow redirects or load the primary). It is also the most to build, run and pay for, and much of it cannot be verified without an AWS account. If chosen, build it in the layers above so the application works and is testable locally before any CDK is written, and use DynamoDB counters with the Lambda outside the VPC to avoid the extra database instance and the migration approval.
