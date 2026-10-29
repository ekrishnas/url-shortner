# Health endpoint

`GET /health` reports that the UrlShortener API process is running and accepting HTTP requests.

It is a liveness check. It does not test the database, cache, or any other dependency.

## Summary

| Item | Value |
|---|---|
| Method and path | `GET /health` |
| Authentication | Anonymous. No credentials are required. |
| Request body | None |
| Query parameters | None |
| Required headers | None |
| Success response | `200 OK` |

## Request

No body, query parameters, or headers are required.

```bash
curl -i http://localhost:5000/health
```

The port depends on how the API is launched. Use the address the API is listening on.

## Response

### Success

Status `200 OK` with `Content-Type: application/json`:

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"status":"Healthy"}
```

The body contains a single property, `status`, with the value `Healthy`.

Clients should check the status code. The body wording may change in future versions, so do not depend on the exact text beyond the status code.

### Errors

The endpoint defines no error responses. It performs no I/O and does not call any external service, so it does not fail by design.

If the process is not running, no response is returned at all. Treat a connection refusal or timeout as "not live".

### Other paths and methods

| Request | Result |
|---|---|
| Any path other than `/health` | `404 Not Found` |
| `POST /health`, `PUT /health`, `DELETE /health` | `405 Method Not Allowed` or `404 Not Found`, whichever the routing layer returns |

These requests never return `200`.

## Usage notes

- Use this endpoint for load balancer or orchestrator liveness probes.
- It does not confirm that the API can serve URL shortening, redirect, or analytics requests. A readiness check for dependencies is not part of this endpoint.
- The endpoint sends no authentication challenge and returns no version or dependency details.
