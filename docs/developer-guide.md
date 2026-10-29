# Developer Guide

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for local environment and integration tests)

## Build and test

```bash
dotnet build src/UrlShortener.Api
dotnet test tests/UrlShortener.Tests
```

To run integration tests only (requires Docker):

```bash
dotnet test tests/UrlShortener.Tests --filter "Category=Integration"
```

## Local environment

```bash
docker compose -f src/compose.yaml up
```

The API starts on `http://localhost:8080`. Postgres listens on `5432`, Redis on `6379`.

## Configuration

### Connection strings

| Key | Description |
|-----|-------------|
| `ConnectionStrings:Write` | Primary Postgres connection string (writes and migrations) |
| `ConnectionStrings:Read` | Read-replica Postgres connection string (defaults to Write if not set) |
| `ConnectionStrings:Redis` | Redis connection string for the redirect cache |

When a connection string is empty the corresponding dependency check is skipped from `GET /health`.

### App:Role

Controls which endpoint groups are registered at startup.

| Value | Endpoints registered |
|-------|---------------------|
| `All` (default) | Everything |
| `Write` | Health, Redirect, Users (`/v1/users/*`), Urls (`/v1/urls/*`) |
| `Read` | Health, Redirect |

An unknown value throws `InvalidOperationException` at startup.

## GET /health

- Returns `200 OK` (liveness only) when no external dependencies are configured.
- Returns `200 OK` when Postgres and Redis are reachable.
- Returns `503 Service Unavailable` with JSON listing the failing checks when one is not.

## Validation error format

Invalid requests return `400 application/problem+json`:

```json
{
  "type": "https://www.rfc-editor.org/rfc/rfc7807",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "email": ["'Email' is not a valid email address."],
    "password": ["The length of 'Password' must be at least 8 characters."]
  }
}
```

## Reserved route names

The following codes can never be assigned to a short URL: `v1`, `health`, `swagger`.

## Swagger UI

Available at `http://localhost:8080/swagger` in Development or when `FastEndpoints.Swagger` is configured.
