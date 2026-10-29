using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UrlShortener.Tests.Health;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_health_returns_200()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_health_without_credentials_returns_200_not_401_or_403()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        Assert.Null(request.Headers.Authorization);
        Assert.Null(request.Content);

        var response = await _client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_health_with_no_external_services_configured_returns_200()
    {
        // The test host supplies no database, Redis or other service configuration;
        // liveness must still succeed.
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/urls")]
    public async Task Get_unknown_route_returns_404(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Wrong_method_on_health_returns_405_or_404_never_200(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/health");

        var response = await _client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound,
            $"Expected 405 or 404 for {method} /health but got {(int)response.StatusCode}.");
    }
}
