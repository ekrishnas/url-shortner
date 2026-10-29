using System.Diagnostics;

namespace UrlShortener.Api.Analytics;

public sealed class TimingMiddleware(RequestDelegate next, ILogger<TimingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        double elapsedMs = 0;

        context.Response.OnStarting(() =>
        {
            elapsedMs = sw.Elapsed.TotalMilliseconds;
            if (!context.Response.HasStarted)
            {
                context.Response.Headers["X-Processing-Time-Ms"] = elapsedMs.ToString("F2");
            }
            return Task.CompletedTask;
        });

        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            logger.LogInformation("{Method} {Path} {Status} {ElapsedMs:F2}ms",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                sw.Elapsed.TotalMilliseconds);
        }
    }
}
