using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace UrlShortener.Tests.Integration;

[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<IntegrationTestFixture>
{
    public const string Name = "Integration";
}

public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private RedisContainer? _redis;
    public WebApplicationFactory<Program>? Factory { get; private set; }

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithDatabase("urlshortener")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        _redis = new RedisBuilder().Build();

        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:Write", _postgres.GetConnectionString());
                b.UseSetting("ConnectionStrings:Read", _postgres.GetConnectionString());
                b.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
            });

        _ = Factory.Server; // ensure startup runs
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_redis is not null) await _redis.DisposeAsync();
    }
}
