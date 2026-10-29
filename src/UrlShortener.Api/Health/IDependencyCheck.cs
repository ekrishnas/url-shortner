namespace UrlShortener.Api.Health;

public interface IDependencyCheck
{
    string Name { get; }

    Task<bool> IsHealthyAsync(CancellationToken ct);
}
