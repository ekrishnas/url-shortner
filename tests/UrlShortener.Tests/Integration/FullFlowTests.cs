using System.Net;
using System.Net.Http.Json;

namespace UrlShortener.Tests.Integration;

[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public class FullFlowTests(IntegrationTestFixture fixture)
{
    private HttpClient Client => fixture.Factory!.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

    [Fact]
    public async Task Health_returns_200_with_real_services()
    {
        var response = await Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Register_login_create_and_redirect_full_flow()
    {
        var client = Client;

        // Register
        var reg = await client.PostAsJsonAsync("/v1/users/register",
            new { email = "test@example.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        // Login
        var login = await client.PostAsJsonAsync("/v1/users/login",
            new { email = "test@example.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Create URL
        var create = await client.PostAsJsonAsync("/v1/urls",
            new { targetUrl = "https://example.com" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreateResponse>();
        Assert.NotNull(created?.Code);

        // Redirect
        var redirect = await client.GetAsync($"/{created!.Code}");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("https://example.com", redirect.Headers.Location?.ToString());
    }

    private sealed record CreateResponse(long Id, string Code, string TargetUrl, DateTimeOffset CreatedAt);
}
