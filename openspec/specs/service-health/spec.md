# service-health Specification

## Purpose
Lets operators, load balancers and orchestrators check that the URL shortener API process is running and able to serve HTTP requests.

## Requirements

### Requirement: Liveness endpoint
The API SHALL expose `GET /health`, which MUST return HTTP 200 whenever the application process is running and able to handle requests.

#### Scenario: App is running
- **WHEN** a client sends `GET /health` to a running instance of the API
- **THEN** the response status is 200 OK

#### Scenario: Response body reports healthy
- **WHEN** a client sends `GET /health` to a running instance of the API
- **THEN** the response has content type `application/json` and the body is `{ "status": "Healthy" }`

### Requirement: Anonymous access to liveness
The liveness endpoint MUST NOT require authentication or any request headers, query parameters or body.

#### Scenario: Request without credentials
- **WHEN** a client sends `GET /health` with no `Authorization` header, no cookies and no body
- **THEN** the response status is 200 OK, not 401 or 403

### Requirement: Liveness is independent of external dependencies
The liveness endpoint SHALL report only that the process is up. It MUST NOT contact a database, cache, message broker or any other external service, so its result does not depend on their availability.

#### Scenario: No external services available
- **WHEN** the API runs with no database, Redis or other external service reachable or configured, and a client sends `GET /health`
- **THEN** the response status is 200 OK

### Requirement: Only liveness is served
In this version the API SHALL serve no HTTP routes other than `GET /health`.

#### Scenario: Unknown route
- **WHEN** a client sends `GET /` or `GET /urls` to the API
- **THEN** the response status is 404 Not Found

#### Scenario: Wrong method on health route
- **WHEN** a client sends `POST /health`
- **THEN** the response status is 405 Method Not Allowed or 404 Not Found, and never 200
