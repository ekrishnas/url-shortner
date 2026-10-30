using System.Security.Claims;
using FastEndpoints;
using UrlShortener.Api.Data;

namespace UrlShortener.Api.Urls;

public sealed record GetUrlRequest { public long Id { get; init; } }
public sealed record UrlDetail(long Id, string Code, string TargetUrl, bool IsActive, long ClickCount, DateTime CreatedAt);

public sealed class GetUrlEndpoint(IUrlRepository urls) : Endpoint<GetUrlRequest, UrlDetail>
{
    public override void Configure()
    {
        Get("/v1/urls/{id}");
        Tags("Urls");
    }

    public override async Task HandleAsync(GetUrlRequest req, CancellationToken ct)
    {
        var userId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await urls.GetByIdForUserAsync(req.Id, userId, ct);
        if (result is null) { await HttpContext.Response.SendNotFoundAsync(ct); return; }

        await HttpContext.Response.SendAsync(new UrlDetail(
            result.Value.Url.Id, result.Value.Url.Code, result.Value.Url.TargetUrl,
            result.Value.Url.IsActive, result.Value.ClickCount, result.Value.Url.CreatedAt),
            cancellation: ct);
    }
}
