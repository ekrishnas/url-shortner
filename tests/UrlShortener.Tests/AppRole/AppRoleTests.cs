using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UrlShortener.Tests.AppRole;

public class AppRoleTests
{
    [Fact]
    public async Task All_role_registers_user_and_url_endpoints()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("App:Role", "All"));
        var client = factory.CreateClient();

        // Login endpoint is registered (bad credentials → 400/401, never 404)
        var login = await client.PostAsJsonAsync("/v1/users/login",
            new { email = "x@x.com", password = "password" });
        Assert.NotEqual(HttpStatusCode.NotFound, login.StatusCode);

        // URLs endpoint is registered (unauthenticated → 401, never 404)
        var urls = await client.GetAsync("/v1/urls");
        Assert.NotEqual(HttpStatusCode.NotFound, urls.StatusCode);
    }

    [Fact]
    public void Invalid_role_throws_at_startup()
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b => b.UseSetting("App:Role", "Invalid"));
            _ = factory.Server; // triggers startup
        });
    }

    [Fact]
    public async Task Write_role_registers_user_endpoints()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("App:Role", "Write"));
        var client = factory.CreateClient();

        // Login endpoint is registered
        var login = await client.PostAsJsonAsync("/v1/users/login",
            new { email = "x@x.com", password = "password" });
        Assert.NotEqual(HttpStatusCode.NotFound, login.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_always_registered_regardless_of_role()
    {
        foreach (var role in new[] { "All", "Write", "Read" })
        {
            var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b => b.UseSetting("App:Role", role));
            var client = factory.CreateClient();

            // Health is always registered (status depends on connection strings)
            var health = await client.GetAsync("/health");
            Assert.NotEqual(HttpStatusCode.NotFound, health.StatusCode);
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, health.StatusCode);
        }
    }
}
