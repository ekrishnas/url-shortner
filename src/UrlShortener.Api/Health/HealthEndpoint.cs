using FastEndpoints;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace UrlShortener.Api.Health;

public sealed class HealthEndpoint(HealthCheckService healthCheckService, ILogger<HealthEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/health");
        AllowAnonymous();
        Tags("Health");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var report = await healthCheckService.CheckHealthAsync(ct);

        var failing = report.Entries
            .Where(e => e.Value.Status != HealthStatus.Healthy)
            .Select(e => e.Key)
            .ToArray();

        foreach (var name in failing)
            logger.LogWarning("Health check '{name}' is unhealthy", name);

        var status = report.Status == HealthStatus.Healthy ? "Healthy" : "Unhealthy";
        var statusCode = report.Status == HealthStatus.Healthy ? 200 : 503;

        await HttpContext.Response.SendAsync(
            new HealthResponse(status, failing),
            statusCode,
            cancellation: ct);
    }
}

internal sealed record HealthResponse(string Status, string[] Failing);
