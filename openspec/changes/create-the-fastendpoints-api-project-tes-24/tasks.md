# Tasks

## 1. Implementation

- [ ] 1.1 Create `src/UrlShortener.Api/UrlShortener.Api.csproj` (`Microsoft.NET.Sdk.Web`, `net10.0`, nullable and implicit usings enabled, pinned stable `FastEndpoints` PackageReference); verify `dotnet restore src/UrlShortener.Api` succeeds
- [ ] 1.2 Create `src/UrlShortener.Api/Program.cs` with `AddFastEndpoints()`, `UseFastEndpoints()`, no `Map*` route calls, ending in `public partial class Program { }`; add `appsettings.json` with logging defaults and `AllowedHosts` only; verify by inspection that no `app.Map` call exists
- [ ] 1.3 Create `src/UrlShortener.Api/Health/HealthEndpoint.cs` with `HealthResponse` and `HealthEndpoint` exactly as in design.md "API contract" (`GET /health`, anonymous, 200 `{"status":"Healthy"}`, no injected dependencies); verify it is the only endpoint class in the file
- [ ] 1.4 Add empty `.gitkeep` files to `src/UrlShortener.Api/Urls/`, `Redirect/`, `Users/`, `Analytics/` and `Data/`; verify `git status` lists all five
- [ ] 1.5 Run `dotnet build src/UrlShortener.Api` and verify it exits 0; verify no `*.sln`/`*.slnx` file exists and no file outside `src/`, `tests/`, `docs/` was added

## 2. Tests

- [x] 2.1 Create `tests/UrlShortener.Tests/UrlShortener.Tests.csproj` (`Microsoft.NET.Sdk`, `net10.0`, `IsPackable` false, pinned `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing`, `ProjectReference` to `../../src/UrlShortener.Api/UrlShortener.Api.csproj`); verify `dotnet build tests/UrlShortener.Tests` exits 0
- [ ] 2.2 Add `tests/UrlShortener.Tests/Health/HealthEndpointTests.cs` using `IClassFixture<WebApplicationFactory<Program>>`: `GET /health` asserts status 200 only (spec "App is running", "No external services available")
- [ ] 2.3 In the same file, add a test sending `GET /health` with no auth header asserting 200 (spec "Request without credentials"), a test that `GET /urls` returns 404 (spec "Unknown route"), and a test that `POST /health` is not 200 (spec "Wrong method on health route")
- [ ] 2.4 Run `dotnet test tests/UrlShortener.Tests` and verify it exits 0, reports at least one passed test and no skipped tests

## 3. Documentation

- [x] 3.1 Write `docs/api/health.md` describing `GET /health` (purpose: liveness only, anonymous, 200 with `{"status":"Healthy"}`, no dependency checks) and the build and test commands `dotnet build src/UrlShortener.Api` and `dotnet test tests/UrlShortener.Tests`; verify the documented commands run as written
