using System.Security.Claims;
using FastEndpoints;
using UrlShortener.Api.Data;

namespace UrlShortener.Api.Urls;

public sealed record DeleteUrlRequest { public long Id { get; init; } }

public sealed class DeleteUrlEndpoint(IUrlRepository urls) : Endpoint<DeleteUrlRequest>
{
    public override void Configure()
    {
        Delete("/v1/urls/{id}");
        Tags("Urls");
    }

    public override async Task HandleAsync(DeleteUrlRequest req, CancellationToken ct)
    {
        var userId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var ok = await urls.DeleteAsync(req.Id, userId, ct);
        if (!ok) { await HttpContext.Response.SendNotFoundAsync(ct); return; }
        await HttpContext.Response.SendNoContentAsync(ct);
    }
}
