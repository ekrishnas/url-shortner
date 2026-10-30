using FastEndpoints;
using StackExchange.Redis;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Data;

namespace UrlShortener.Api.Redirect;

public sealed record RedirectRequest { public string Code { get; init; } = ""; }

public sealed class RedirectEndpoint(
    IUrlRepository urls,
    IServiceProvider sp,
    ClickChannel clickChannel) : Endpoint<RedirectRequest>
{
    private const string CachePrefix = "url:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public override void Configure()
    {
        Get("/{code}");
        AllowAnonymous();
        Tags("Redirect");
    }

    public override async Task HandleAsync(RedirectRequest req, CancellationToken ct)
    {
        if (ReservedRoutes.IsReserved(req.Code))
        {
            await HttpContext.Response.SendNotFoundAsync(ct);
            return;
        }

        IConnectionMultiplexer? redis = null;
        try { redis = sp.GetService<IConnectionMultiplexer>(); } catch { /* unreachable Redis */ }
        string? targetUrl = null;
        long? shortUrlId = null;

        if (redis is not null)
        {
            try
            {
                var db = redis.GetDatabase();
                var cached = await db.StringGetAsync(CachePrefix + req.Code);
                if (cached.HasValue)
                {
                    var parts = ((string)cached!).Split('\n');
                    shortUrlId = long.Parse(parts[0]);
                    targetUrl = parts[1];
                }
            }
            catch { /* fall through to DB */ }
        }

        if (targetUrl is null)
        {
            try
            {
                var url = await urls.FindByCodeAsync(req.Code, ct);
                if (url is null) { await HttpContext.Response.SendNotFoundAsync(ct); return; }
                targetUrl = url.TargetUrl;
                shortUrlId = url.Id;

                if (redis is not null)
                {
                    try
                    {
                        var db = redis.GetDatabase();
                        await db.StringSetAsync(CachePrefix + req.Code, $"{url.Id}\n{url.TargetUrl}", CacheTtl);
                    }
                    catch { /* cache miss is fine */ }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                await HttpContext.Response.SendNotFoundAsync(ct);
                return;
            }
        }

        if (shortUrlId.HasValue)
        {
            clickChannel.TryWrite(new ClickRecord(
                shortUrlId.Value,
                HttpContext.Request.Headers.UserAgent.ToString(),
                HttpContext.Connection.RemoteIpAddress?.ToString()));
        }

        await HttpContext.Response.SendRedirectAsync(targetUrl!, isPermanent: false, allowRemoteRedirects: true);
    }
}
