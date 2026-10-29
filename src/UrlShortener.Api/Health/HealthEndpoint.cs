using FastEndpoints;

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
    {
        Logger.LogInformation("Health check: Healthy");
        return Send.OkAsync(new HealthResponse("Healthy"), ct);
    }
}
