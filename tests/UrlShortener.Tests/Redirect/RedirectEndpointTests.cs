using System.Net;
using UrlShortener.Api.Data;
using UrlShortener.Tests.Fakes;

namespace UrlShortener.Tests.Redirect;

public class RedirectEndpointTests
{
    [Fact]
    public async Task Known_code_redirects_to_target()
    {
        using var ctx = new FakeContext();
        ctx.Urls.Seed(new ShortUrl(1, "abc123", "https://example.com", 1, true, DateTimeOffset.UtcNow));
        var client = ctx.CreateClientNoRedirect();

        var response = await client.GetAsync("/abc123");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://example.com", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Unknown_code_returns_404()
    {
        using var ctx = new FakeContext();
        var response = await ctx.CreateClient().GetAsync("/unknownxyz");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("V1")]
    public async Task Reserved_code_returns_404(string code)
    {
        using var ctx = new FakeContext();
        var response = await ctx.CreateClient().GetAsync($"/{code}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
