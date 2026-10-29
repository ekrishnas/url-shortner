using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UrlShortener.Tests.Health;

/// <summary>
/// Health tests that need real, reachable dependencies. A Postgres and a Redis container
/// are started once for this class; each test points some of the connection strings at
/// them and, where the test calls for it, others at an unreachable address.
/// </summary>
public class HealthEndpointTests : IClassFixture<DependencyContainersFixture>
{
    private readonly DependencyContainersFixture _dependencies;

    public HealthEndpointTests(DependencyContainersFixture dependencies)
    {
        _dependencies = dependencies;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_health_with_all_dependencies_reachable_returns_200()
    {
        using var factory = _dependencies.CreateHealthyFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_health_with_all_dependencies_reachable_returns_json_healthy_body()
    {
        using var factory = _dependencies.CreateHealthyFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadBodyAsync(response);
        Assert.Equal("Healthy", body.Status);
        Assert.Empty(body.Failing);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_health_without_credentials_and_dependencies_reachable_returns_200()
    {
        using var factory = _dependencies.CreateHealthyFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        Assert.Null(request.Headers.Authorization);
        Assert.Null(request.Content);

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_health_with_only_write_unreachable_reports_postgres_write()
    {
        using var factory = DependencyContainersFixture.CreateFactory(
            DependencyContainersFixture.UnreachablePostgres,
            _dependencies.PostgresConnectionString,
            _dependencies.RedisConnectionString);

        await AssertUnhealthyAsync(factory, "postgres-write");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_health_with_only_read_unreachable_reports_postgres_read()
    {
        using var factory = DependencyContainersFixture.CreateFactory(
            _dependencies.PostgresConnectionString,
            DependencyContainersFixture.UnreachablePostgres,
            _dependencies.RedisConnectionString);

        await AssertUnhealthyAsync(factory, "postgres-read");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Get_health_with_only_redis_unreachable_reports_redis()
    {
        using var factory = DependencyContainersFixture.CreateFactory(
            _dependencies.PostgresConnectionString,
            _dependencies.PostgresConnectionString,
            DependencyContainersFixture.UnreachableRedis);

        await AssertUnhealthyAsync(factory, "redis");
    }

    private static async Task AssertUnhealthyAsync(WebApplicationFactory<Program> factory, string expectedFailing)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadBodyAsync(response);
        Assert.Equal("Unhealthy", body.Status);
        Assert.Equal(new[] { expectedFailing }, body.Failing);
    }

    internal static async Task<(string Status, string[] Failing)> ReadBodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var status = root.GetProperty("status").GetString()!;
        var failing = root.GetProperty("failing").EnumerateArray().Select(e => e.GetString()!).ToArray();
        return (status, failing);
    }
}
