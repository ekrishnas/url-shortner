using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Data;
using UrlShortener.Api.Users;
using UrlShortener.Tests.Fakes;

namespace UrlShortener.Tests.Urls;

public class UrlEndpointTests
{
    private static async Task<HttpClient> LoginAsync(FakeContext ctx)
    {
        ctx.Users.Seed(new User(1, "owner@example.com",
            PasswordHelper.Hash("Password123!"), DateTime.UtcNow));
        var client = ctx.CreateClient();
        await client.PostAsJsonAsync("/v1/users/login",
            new { email = "owner@example.com", password = "Password123!" });
        return client;
    }

    [Fact]
    public async Task Create_url_returns_201_with_code()
    {
        using var ctx = new FakeContext();
        var client = await LoginAsync(ctx);

        var response = await client.PostAsJsonAsync("/v1/urls",
            new { targetUrl = "https://example.com" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateResponse>();
        Assert.NotNull(body?.Code);
        Assert.Equal("https://example.com", body!.TargetUrl);
    }

    [Fact]
    public async Task Create_url_unauthenticated_returns_401()
    {
        using var ctx = new FakeContext();
        var response = await ctx.CreateClient().PostAsJsonAsync("/v1/urls",
            new { targetUrl = "https://example.com" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_urls_returns_200()
    {
        using var ctx = new FakeContext();
        ctx.Urls.Seed(new ShortUrl(1, "abc123", "https://example.com", 1, true, DateTime.UtcNow));
        var client = await LoginAsync(ctx);

        var response = await client.GetAsync("/v1/urls");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_url_by_id_found_returns_200()
    {
        using var ctx = new FakeContext();
        ctx.Urls.Seed(new ShortUrl(99, "abc123", "https://example.com", 1, true, DateTime.UtcNow));
        var client = await LoginAsync(ctx);

        var response = await client.GetAsync("/v1/urls/99");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_url_by_id_not_found_returns_404()
    {
        using var ctx = new FakeContext();
        var client = await LoginAsync(ctx);

        var response = await client.GetAsync("/v1/urls/9999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_url_found_returns_204()
    {
        using var ctx = new FakeContext();
        ctx.Urls.Seed(new ShortUrl(5, "abc123", "https://example.com", 1, true, DateTime.UtcNow));
        var client = await LoginAsync(ctx);

        var response = await client.PostAsync("/v1/urls/5/deactivate", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_url_not_found_returns_404()
    {
        using var ctx = new FakeContext();
        var client = await LoginAsync(ctx);

        var response = await client.PostAsync("/v1/urls/999/deactivate", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_url_found_returns_204()
    {
        using var ctx = new FakeContext();
        ctx.Urls.Seed(new ShortUrl(7, "abc123", "https://example.com", 1, true, DateTime.UtcNow));
        var client = await LoginAsync(ctx);

        var response = await client.DeleteAsync("/v1/urls/7");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_url_not_found_returns_404()
    {
        using var ctx = new FakeContext();
        var client = await LoginAsync(ctx);

        var response = await client.DeleteAsync("/v1/urls/9999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record CreateResponse(long Id, string Code, string TargetUrl, DateTime CreatedAt);
}
