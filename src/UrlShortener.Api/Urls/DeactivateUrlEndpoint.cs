using System.Security.Claims;
using FastEndpoints;
using UrlShortener.Api.Data;

namespace UrlShortener.Api.Urls;

public sealed record DeactivateUrlRequest { public long Id { get; init; } }

public sealed class DeactivateUrlEndpoint(IUrlRepository urls) : Endpoint<DeactivateUrlRequest>
{
    public override void Configure()
    {
        Post("/v1/urls/{id}/deactivate");
        Tags("Urls");
    }

    public override async Task HandleAsync(DeactivateUrlRequest req, CancellationToken ct)
    {
        var userId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var ok = await urls.DeactivateAsync(req.Id, userId, ct);
        if (!ok) { await HttpContext.Response.SendNotFoundAsync(ct); return; }
        await HttpContext.Response.SendNoContentAsync(ct);
    }
}
