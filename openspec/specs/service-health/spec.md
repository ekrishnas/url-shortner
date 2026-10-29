# service-health Specification

## Purpose
Lets operators, load balancers and orchestrators check that the URL shortener API process is running and able to serve HTTP requests.

## Requirements

### Requirement: Liveness endpoint
The API SHALL expose `GET /health`, which MUST return HTTP 200 when the application process is running and the Write Postgres database, the Read Postgres database and Redis are all reachable.

#### Scenario: App is running
- **WHEN** a client sends `GET /health` to a running instance of the API whose Write database, Read database and Redis are all reachable
- **THEN** the response status is 200 OK

#### Scenario: Response body reports healthy
- **WHEN** a client sends `GET /health` to a running instance of the API whose Write database, Read database and Redis are all reachable
- **THEN** the response has content type `application/json` and the body is `{ "status": "Healthy", "failing": [] }`

### Requirement: Anonymous access to liveness
The liveness endpoint MUST NOT require authentication or any request headers, query parameters or body.

#### Scenario: Request without credentials
- **WHEN** a client sends `GET /health` with no `Authorization` header, no cookies and no body
- **THEN** the response status is not 401 or 403

#### Scenario: Request without credentials when dependencies are reachable
- **WHEN** a client sends `GET /health` with no `Authorization` header, no cookies and no body, and the Write database, Read database and Redis are all reachable
- **THEN** the response status is 200 OK

### Requirement: Only liveness is served
In this version the API SHALL serve no HTTP routes other than `GET /health`.

#### Scenario: Unknown route
- **WHEN** a client sends `GET /` or `GET /urls` to the API
- **THEN** the response status is 404 Not Found

#### Scenario: Wrong method on health route
- **WHEN** a client sends `POST /health`
- **THEN** the response status is 405 Method Not Allowed or 404 Not Found, and never 200

### Requirement: Connection strings come from configuration
The API SHALL read the Write Postgres, Read Postgres and Redis connection strings from the configuration keys `ConnectionStrings:Write`, `ConnectionStrings:Read` and `ConnectionStrings:Redis`, so that any configuration source, including environment variables, can set them. No connection string SHALL be hard-coded in source code.

#### Scenario: Environment variable overrides a connection string
- **WHEN** the API runs with `ConnectionStrings__Redis` set to an unreachable host and port, and a client sends `GET /health`
- **THEN** the response reports `redis` as failing, even if the settings file holds a reachable value

#### Scenario: Write and Read are checked independently
- **WHEN** the Write connection string points at a reachable database and the Read connection string points at an unreachable host and port
- **THEN** `GET /health` reports `postgres-read` as failing and does not report `postgres-write` as failing

### Requirement: Failure response names failing dependencies
When any of the Write database, Read database or Redis is unreachable, `GET /health` MUST return HTTP 503 with content type `application/json` and a body `{ "status": "Unhealthy", "failing": [...] }`, where `failing` lists the name of every unreachable dependency (`postgres-write`, `postgres-read`, `redis`) in that order, and no reachable dependency.

#### Scenario: Write database unreachable
- **WHEN** only the Write connection string points at an unreachable host and port, and a client sends `GET /health`
- **THEN** the response status is 503 and the body is `{ "status": "Unhealthy", "failing": ["postgres-write"] }`

#### Scenario: Read database unreachable
- **WHEN** only the Read connection string points at an unreachable host and port, and a client sends `GET /health`
- **THEN** the response status is 503 and the body is `{ "status": "Unhealthy", "failing": ["postgres-read"] }`

#### Scenario: Redis unreachable
- **WHEN** only the Redis connection string points at an unreachable host and port, and a client sends `GET /health`
- **THEN** the response status is 503 and the body is `{ "status": "Unhealthy", "failing": ["redis"] }`

#### Scenario: Everything unreachable
- **WHEN** all three connection strings point at unreachable hosts or ports, and a client sends `GET /health`
- **THEN** the response status is 503 and the body is `{ "status": "Unhealthy", "failing": ["postgres-write", "postgres-read", "redis"] }`

### Requirement: Missing connection string fails its check
If a connection string is missing, empty or cannot be parsed, the API MUST report that dependency as failing in `GET /health` and MUST NOT fail or refuse to start because of it.

#### Scenario: Read connection string missing
- **WHEN** the API starts with no value for `ConnectionStrings:Read`, and a client sends `GET /health`
- **THEN** the host has started, and the response status is 503 with `postgres-read` in `failing`

### Requirement: Startup does not depend on reachability
The API MUST start and serve requests even when the Write database, Read database or Redis is unreachable at startup; it SHALL NOT connect to them in a way that blocks or fails startup.

#### Scenario: Routing works without dependencies
- **WHEN** the API runs with none of its dependencies reachable, and a client sends `GET /urls`
- **THEN** the response status is 404 Not Found

### Requirement: Health check time is bounded
`GET /health` MUST respond within 5 seconds even when a dependency is unreachable, and SHALL stop checking when the client cancels the request.

#### Scenario: Unreachable dependency answers quickly
- **WHEN** all three connection strings point at unreachable hosts or ports, and a client sends `GET /health`
- **THEN** the response arrives within 5 seconds

### Requirement: Health output does not leak connection details
The `GET /health` response body and the logs written by the health check MUST contain only dependency names and statuses. They MUST NOT contain exception messages, hostnames, ports, usernames or passwords.

#### Scenario: Failure body holds only names and status
- **WHEN** a dependency is unreachable and a client sends `GET /health`
- **THEN** the body contains only the `status` and `failing` properties, and contains none of the host, port, username or password from the connection strings

### Requirement: Failing checks are logged
Each failing dependency check MUST be logged at Warning level or higher, naming the dependency.

#### Scenario: Redis failure is logged
- **WHEN** Redis is unreachable and a client sends `GET /health`
- **THEN** a log entry at Warning level or higher names `redis`
