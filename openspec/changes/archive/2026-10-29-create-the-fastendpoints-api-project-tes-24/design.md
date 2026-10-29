# Design

## Context

The repository holds no .NET code. `.foreman/target.yaml` fixes the gates: `dotnet build src/UrlShortener.Api` and `dotnet test tests/UrlShortener.Tests`. Both commands target a project directory, so no `.sln` is needed. Agents may write only under `src/`, `tests/` and `docs/`. The root `.gitignore` already ignores `[Bb]in/` and `[Oo]bj/`. See proposal.md for motivation.

## Goals / Non-Goals

**Goals:**
- Two projects that build and test with the existing commands, unchanged.
- A FastEndpoints layout (one endpoint class per route, feature folders) that later stories extend.
- An in-process liveness test with `WebApplicationFactory<Program>`.

**Non-Goals:**
- Readiness checks, ASP.NET health-check middleware, auth setup, Swagger, logging configuration, Docker, persistence.

## Decisions

### FastEndpoints instead of minimal APIs
The story names FastEndpoints, which overrides the spike's minimal-API recommendation. `Program.cs` calls `builder.Services.AddFastEndpoints()` and `app.UseFastEndpoints()` and nothing else route-related (no `app.MapGet`, `MapHealthChecks`, `MapControllers`).

### Health endpoint lives in its own `Health/` folder
`src/UrlShortener.Api/Health/HealthEndpoint.cs` holds a single class `HealthEndpoint : EndpointWithoutRequest<HealthResponse>` and its response record. Keeping it out of the five business folders keeps those clean; the five folders are required, not exclusive.
Alternative: ASP.NET `MapHealthChecks("/health")`. Rejected because it adds a non-FastEndpoints route and breaks "one endpoint class per route". It can be revisited when readiness checks need the database.

### Empty feature folders carry `.gitkeep`
`Urls/`, `Redirect/`, `Users/`, `Analytics/`, `Data/` each get an empty `.gitkeep`. No placeholder classes, so there is no dead code.

### Test hosting
`Program.cs` uses top-level statements and ends with `public partial class Program { }`. The test project uses `WebApplicationFactory<Program>` through `IClassFixture`. The app reads no connection strings or external config, so the factory needs no overrides.

### Package versions
Use the latest stable versions compatible with net10.0, pinned with explicit `Version` attributes in each `.csproj`. No `Directory.Packages.props`, `global.json` or `nuget.config` (all would be at the root or outside the allowed paths).

## API contract

### Project layout

```
src/UrlShortener.Api/
  UrlShortener.Api.csproj      Sdk="Microsoft.NET.Sdk.Web", TargetFramework net10.0,
                               Nullable enable, ImplicitUsings enable,
                               PackageReference FastEndpoints
  Program.cs                   AddFastEndpoints(); UseFastEndpoints(); public partial class Program { }
  appsettings.json             Logging defaults and "AllowedHosts": "*" only
  Properties/launchSettings.json (optional)
  Health/HealthEndpoint.cs     HealthEndpoint + HealthResponse
  Urls/.gitkeep
  Redirect/.gitkeep
  Users/.gitkeep
  Analytics/.gitkeep
  Data/.gitkeep
tests/UrlShortener.Tests/
  UrlShortener.Tests.csproj    Sdk="Microsoft.NET.Sdk", TargetFramework net10.0, IsPackable false,
                               PackageReference xunit, xunit.runner.visualstudio,
                               Microsoft.NET.Test.Sdk, Microsoft.AspNetCore.Mvc.Testing;
                               ProjectReference ../../src/UrlShortener.Api/UrlShortener.Api.csproj
  Health/HealthEndpointTests.cs
docs/api/health.md
```

Namespaces: `UrlShortener.Api.Health` for the endpoint, `UrlShortener.Tests.Health` for the tests.

### `GET /health`

| Item | Value |
|---|---|
| Method / path | `GET /health` (exact; no route prefix, no versioning) |
| Auth | Anonymous (`AllowAnonymous()` in `Configure()`) |
| Request | No body, no query parameters, no required headers |
| Success | `200 OK`, `Content-Type: application/json`, body `{"status":"Healthy"}` |
| Errors | None defined. The endpoint does no I/O and cannot fail by design. If the process is down, no response is returned. |
| Side effects | None. It must not inject or call a `DbContext`, Redis connection, `HttpClient` or any other external service. |

C# shape:

```csharp
namespace UrlShortener.Api.Health;

public sealed record HealthResponse(string Status);

public sealed class HealthEndpoint : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure()
    {
        Get("/health");
        AllowAnonymous();
    }

    public override Task HandleAsync(CancellationToken ct)
        => Send.OkAsync(new HealthResponse("Healthy"), ct); // or SendOkAsync on older FastEndpoints
}
```

FastEndpoints serialises with camelCase, so the property is emitted as `status`. Tests MUST assert only the status code (200), not the body, so a change in body wording does not break them.

### Other routes
None. Any other path returns `404`. `POST`/`PUT`/`DELETE /health` return `405` or `404` (whichever the routing layer produces); never `200`.

## Risks / Trade-offs

- [FastEndpoints renamed `SendOkAsync` to `Send.OkAsync` in recent major versions] → Implementer uses whichever API the pinned version exposes; `dotnet build` catches a mismatch.
- [net10.0 SDK missing on the build machine] → The build gate fails loudly; no root `global.json` is added, so whatever net10 SDK is installed is used.
- [NuGet restore needs network] → Assumed available per the requirement; no local feed is configured.
- [`target.yaml` lists `requires: [docker]` but tests do not use Docker] → Intentional; liveness tests run in process.
- [Unauthenticated `/health` exposes that the service is up] → Acceptable for liveness; the response carries no version or dependency details.

## Migration Plan

New projects only; nothing to migrate. Rollback is deleting `src/UrlShortener.Api`, `tests/UrlShortener.Tests` and `docs/api/health.md`.
