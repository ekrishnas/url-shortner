using FastEndpoints;

namespace UrlShortener.Api.Health;

public sealed record HealthResponse(string Status, IReadOnlyList<string> Failing);

public sealed class HealthEndpoint(IEnumerable<IDependencyCheck> checks) : EndpointWithoutRequest<HealthResponse>
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);

    public override void Configure()
    {
        Get("/health");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var all = checks.ToList();
        var results = await Task.WhenAll(all.Select(check => RunAsync(check, ct)));

        var failing = new List<string>();
        for (var i = 0; i < all.Count; i++)
        {
            if (!results[i])
            {
                failing.Add(all[i].Name);
                Logger.LogWarning("Health check failed: {Dependency}", all[i].Name);
            }
        }

        if (failing.Count == 0)
        {
            Logger.LogInformation("Health check: Healthy");
            await Send.OkAsync(new HealthResponse("Healthy", []), ct);
            return;
        }

        await Send.ResponseAsync(new HealthResponse("Unhealthy", failing), 503, ct);
    }

    private static async Task<bool> RunAsync(IDependencyCheck check, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CheckTimeout);
        try
        {
            return await check.IsHealthyAsync(cts.Token);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
