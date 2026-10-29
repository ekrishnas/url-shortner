# Proposal

## Why

The repository has no code yet, so the gates in `.foreman/target.yaml` (`dotnet build src/UrlShortener.Api` and `dotnet test tests/UrlShortener.Tests`) cannot pass and no feature work can start. This change creates the FastEndpoints API project and its xUnit test project, with a single liveness route (`GET /health`), so every later story has a building, tested skeleton to add to.

## What Changes

- Add the ASP.NET Core web project `src/UrlShortener.Api` (net10.0, `Microsoft.NET.Sdk.Web`) using FastEndpoints, with one endpoint class per route.
- Add the empty feature folders `Urls/`, `Redirect/`, `Users/`, `Analytics/` and `Data/` under `src/UrlShortener.Api/`, each holding a `.gitkeep` so git tracks it.
- Add one route, `GET /health`, served by a FastEndpoints endpoint in `src/UrlShortener.Api/Health/`. It allows anonymous access, returns 200 with `{ "status": "Healthy" }`, and touches no external dependency.
- Make `Program` public (`public partial class Program { }`) so tests can host the app with `WebApplicationFactory<Program>`.
- Add the xUnit project `tests/UrlShortener.Tests` (net10.0) that references the API project and `Microsoft.AspNetCore.Mvc.Testing`, with an in-process test for `GET /health`.
- Add a short `docs/api/health.md` describing the route and how to build and test.
- FastEndpoints is chosen over the minimal APIs recommended in `docs/spikes/20261029-143946-8aa9/analysis.md`, because the story names it.

Out of scope: business endpoints, database or Redis packages and code, `App:Role` handling, ASP.NET `AddHealthChecks()`/readiness checks, Dockerfile or compose files, any `.sln`/`.slnx`, and any file outside `src/`, `tests/` and `docs/`.

## Capabilities

### New Capabilities
- `service-health`: the liveness endpoint that reports whether the API process is up and serving requests.

### Modified Capabilities
(none)

## Impact

- New code: `src/UrlShortener.Api/**`, `tests/UrlShortener.Tests/**`, `docs/api/health.md`.
- New NuGet dependencies: `FastEndpoints` (API); `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing` (tests).
- New public HTTP route: `GET /health`.
- No changes to `.foreman/target.yaml`, `.gitignore` (which already ignores `bin/` and `obj/`) or any other root file.
