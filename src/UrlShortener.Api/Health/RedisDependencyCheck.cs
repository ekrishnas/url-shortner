using StackExchange.Redis;

namespace UrlShortener.Api.Health;

public sealed class RedisDependencyCheck(IConfiguration configuration) : IDependencyCheck
{
    public string Name => "redis";

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var connectionString = configuration.GetConnectionString("Redis");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return false;
            }

            var options = ConfigurationOptions.Parse(connectionString);
            options.ConnectTimeout = 2000;
            options.AsyncTimeout = 2000;
            options.SyncTimeout = 2000;
            options.ConnectRetry = 1;
            options.AbortOnConnectFail = true;

            await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(options).WaitAsync(ct);
            await multiplexer.GetDatabase().PingAsync().WaitAsync(ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
