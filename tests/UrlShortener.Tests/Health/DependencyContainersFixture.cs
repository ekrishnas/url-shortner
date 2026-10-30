using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace UrlShortener.Tests.Health;

/// <summary>
/// Starts one real Postgres and one real Redis container for the whole test class
/// and builds API hosts whose connection strings point at them (or at unreachable
/// addresses, per test).
/// </summary>
public sealed class DependencyContainersFixture : IAsyncLifetime
{
    // Nothing listens on port 1 on loopback, so connections are refused straight away.
    public const string UnreachablePostgres = "Host=127.0.0.1;Port=1;Database=leakprobe_db";
    public const string UnreachableRedis = "127.0.0.1:1";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().Build();
    private readonly RedisContainer _redis = new RedisBuilder().Build();

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    /// <summary>
    /// Builds a factory with each connection string overridden. A null value leaves that
    /// key alone (the settings file value applies); pass "" to blank it.
    /// </summary>
    public static WebApplicationFactory<Program> CreateFactory(
        string? write,
        string? read,
        string? redis,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        var overrides = new Dictionary<string, string?>();
        if (write is not null) overrides["ConnectionStrings:Write"] = write;
        if (read is not null) overrides["ConnectionStrings:Read"] = read;
        if (redis is not null) overrides["ConnectionStrings:Redis"] = redis;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(overrides));
            if (configureLogging is not null)
            {
                builder.ConfigureLogging(configureLogging);
            }
        });
    }

    /// <summary>Factory where all three dependencies are the running containers.</summary>
    public WebApplicationFactory<Program> CreateHealthyFactory() =>
        CreateFactory(PostgresConnectionString, PostgresConnectionString, RedisConnectionString);

    /// <summary>Factory where all three dependencies are unreachable. Needs no Docker.</summary>
    public static WebApplicationFactory<Program> CreateAllUnreachableFactory(
        Action<ILoggingBuilder>? configureLogging = null) =>
        CreateFactory(UnreachablePostgres, UnreachablePostgres, UnreachableRedis, configureLogging);
}
