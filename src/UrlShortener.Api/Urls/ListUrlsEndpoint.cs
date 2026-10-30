using System.Security.Claims;
using FastEndpoints;
using UrlShortener.Api.Data;

namespace UrlShortener.Api.Urls;

public sealed record ListUrlsRequest
{
    [QueryParam] public int Page { get; init; } = 1;
    [QueryParam] public int PageSize { get; init; } = 20;
}

public sealed record UrlSummary(long Id, string Code, string TargetUrl, bool IsActive, long ClickCount, DateTime CreatedAt);

public sealed class ListUrlsEndpoint(IUrlRepository urls) : Endpoint<ListUrlsRequest, IEnumerable<UrlSummary>>
{
    public override void Configure()
    {
        Get("/v1/urls");
        Tags("Urls");
    }

    public override async Task HandleAsync(ListUrlsRequest req, CancellationToken ct)
    {
        var userId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var page = Math.Max(1, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 100);

        var results = await urls.ListByUserAsync(userId, page, pageSize, ct);
        var response = results.Select(r => new UrlSummary(
            r.Url.Id, r.Url.Code, r.Url.TargetUrl, r.Url.IsActive, r.ClickCount, r.Url.CreatedAt));

        await HttpContext.Response.SendAsync(response, cancellation: ct);
    }
}
