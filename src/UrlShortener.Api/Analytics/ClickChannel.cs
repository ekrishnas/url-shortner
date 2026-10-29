using System.Threading.Channels;
using UrlShortener.Api.Data;

namespace UrlShortener.Api.Analytics;

public sealed record ClickRecord(long ShortUrlId, string? UserAgent, string? IpAddress);

public sealed class ClickChannel
{
    private readonly Channel<ClickRecord> _channel =
        Channel.CreateBounded<ClickRecord>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

    public bool TryWrite(ClickRecord record) => _channel.Writer.TryWrite(record);
    public ChannelReader<ClickRecord> Reader => _channel.Reader;
}

public sealed class ClickBackgroundService(ClickChannel channel, IServiceScopeFactory scopeFactory, ILogger<ClickBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var click in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<IUrlRepository>();
                await repo.RecordClickAsync(click.ShortUrlId, click.UserAgent, click.IpAddress, stoppingToken);
                await repo.UpsertStatsAsync(click.ShortUrlId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to record click for short_url_id={Id}", click.ShortUrlId);
            }
        }
    }
}
