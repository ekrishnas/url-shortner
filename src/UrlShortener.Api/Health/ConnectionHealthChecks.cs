using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using StackExchange.Redis;

namespace UrlShortener.Api.Health;

public sealed class PostgresHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(connectionString))
            return HealthCheckResult.Unhealthy("Connection string is not configured.");

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 1 };
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch
        {
            return HealthCheckResult.Unhealthy("Could not connect.");
        }
    }
}

public sealed class RedisHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(connectionString))
            return HealthCheckResult.Unhealthy("Connection string is not configured.");

        try
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = true;
            options.ConnectTimeout = 1000;
            options.ReconnectRetryPolicy = new ExponentialRetry(0);
            using var mux = await ConnectionMultiplexer.ConnectAsync(options);
            await mux.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch
        {
            return HealthCheckResult.Unhealthy("Could not connect.");
        }
    }
}
