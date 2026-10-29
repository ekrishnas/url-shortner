# Health endpoint

`GET /health` reports whether the UrlShortener API can reach its three dependencies:

| Dependency name | What is checked | Configuration key |
|---|---|---|
| `postgres-write` | Write Postgres database: opens a connection and runs `SELECT 1` | `ConnectionStrings:Write` |
| `postgres-read` | Read Postgres database: opens a connection and runs `SELECT 1` | `ConnectionStrings:Read` |
| `redis` | Redis: connects and sends `PING` | `ConnectionStrings:Redis` |

Each check runs on every request. Nothing is connected at startup.

## Summary

| Item | Value |
|---|---|
| Method and path | `GET /health` |
| Authentication | Anonymous. No credentials are required. |
| Request body | None |
| Query parameters | None |
| Required headers | None |
| Success response | `200 OK` |
| Failure response | `503 Service Unavailable` |
| Maximum response time | 5 seconds |

## Configuration

The connection strings are read from configuration when `/health` is called.

| Key | Environment variable | Format |
|---|---|---|
| `ConnectionStrings:Write` | `ConnectionStrings__Write` | Npgsql (Postgres) |
| `ConnectionStrings:Read` | `ConnectionStrings__Read` | Npgsql (Postgres) |
| `ConnectionStrings:Redis` | `ConnectionStrings__Redis` | StackExchange.Redis, for example `host:port` |

- The settings file `appsettings.json` sets all three keys to an empty string. A deployment that does not override them reports the matching dependency as failing.
- The settings file `appsettings.Development.json` sets local placeholders for development: `Write` and `Read` point at `localhost:5432` (database `urlshortener`), and `Redis` points at `localhost:6379`.
- A missing, empty or unparseable value makes its check fail. It does not stop the API from starting.
- Timeouts are fixed in code: each dependency check is limited to about 2 seconds, and the whole health check to 3 seconds.

Deploy with all three environment variables set. Without them, `/health` returns `503` by design.

## Request

No body, query parameters, or headers are required. Any that are sent are ignored.

```bash
curl -i http://<api-address>/health
```

Use the address the API is listening on.

## Responses

### Success: all dependencies reachable

Status `200 OK` with `Content-Type: application/json`:

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"status":"Healthy","failing":[]}
```

### Failure: one or more dependencies unreachable

Status `503 Service Unavailable` with `Content-Type: application/json`. This also applies when a connection string is missing, empty or cannot be parsed.

Example, where only the Read database is unreachable:

```http
HTTP/1.1 503 Service Unavailable
Content-Type: application/json; charset=utf-8

{"status":"Unhealthy","failing":["postgres-read"]}
```

Example, where all three dependencies are unreachable:

```http
HTTP/1.1 503 Service Unavailable
Content-Type: application/json; charset=utf-8

{"status":"Unhealthy","failing":["postgres-write","postgres-read","redis"]}
```

Response fields:

| Field | Type | Meaning |
|---|---|---|
| `status` | string | Exactly `"Healthy"` (200) or `"Unhealthy"` (503). |
| `failing` | array of strings | Always present. Lists each failing dependency once, in the order `postgres-write`, `postgres-read`, `redis`. Empty when healthy. |

There are no other fields. The body never contains exception messages, hostnames, ports, usernames or passwords. Operators can find the cause in the API's own logs: each failing dependency is logged at Warning level with its name.

### Response time

The response is sent within 5 seconds, even when a dependency does not answer. Unreachable dependencies are reported as failing once their short timeouts expire.

### Other paths and methods

| Request | Result |
|---|---|
| Any path other than `/health` | `404 Not Found` |
| `POST /health`, `PUT /health`, `DELETE /health` | `405 Method Not Allowed` or `404 Not Found`, whichever the routing layer returns |

These requests never return `200` or `503`.

### No response

If the API process is not running, no response is returned. Treat a connection refusal or timeout as "not live".

## Usage notes

- `503` means at least one dependency is unavailable. It does not mean the API process has crashed.
- If you use `/health` as a **liveness probe** in an orchestrator, a database or Redis outage will make the probe fail. The orchestrator may then restart the API containers, which does not fix the outage. Either point liveness probes at another target, or accept that behaviour.
- Each call opens a new connection to each dependency. Poll it at a modest rate, not in a tight loop.
- A check can still pass when a pooled Postgres connection points at a server that has just gone away, because the check reuses an idle pooled connection. `SELECT 1` then fails on that connection and the check reports the dependency as failing.
