using System.Net;
using System.Net.Http.Json;
using UrlShortener.Api.Data;
using UrlShortener.Api.Users;
using UrlShortener.Tests.Fakes;

namespace UrlShortener.Tests.Users;

public class UserEndpointTests
{
    [Fact]
    public async Task Register_new_user_returns_201()
    {
        using var ctx = new FakeContext();
        var response = await ctx.CreateClient().PostAsJsonAsync("/v1/users/register",
            new { email = "new@example.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Register_duplicate_email_returns_409()
    {
        using var ctx = new FakeContext();
        ctx.Users.Seed(new User(1, "dup@example.com",
            PasswordHelper.Hash("pass1234"), DateTime.UtcNow));

        var response = await ctx.CreateClient().PostAsJsonAsync("/v1/users/register",
            new { email = "dup@example.com", password = "Password123!" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Login_valid_credentials_returns_200()
    {
        using var ctx = new FakeContext();
        ctx.Users.Seed(new User(1, "user@example.com",
            PasswordHelper.Hash("Password123!"), DateTime.UtcNow));

        var response = await ctx.CreateClient().PostAsJsonAsync("/v1/users/login",
            new { email = "user@example.com", password = "Password123!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal("user@example.com", body!.Email);
    }

    [Fact]
    public async Task Login_wrong_password_returns_401()
    {
        using var ctx = new FakeContext();
        ctx.Users.Seed(new User(1, "user@example.com",
            PasswordHelper.Hash("correct"), DateTime.UtcNow));

        var response = await ctx.CreateClient().PostAsJsonAsync("/v1/users/login",
            new { email = "user@example.com", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_unknown_email_returns_401()
    {
        using var ctx = new FakeContext();
        var response = await ctx.CreateClient().PostAsJsonAsync("/v1/users/login",
            new { email = "nobody@example.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_current_user_when_authenticated_returns_200()
    {
        using var ctx = new FakeContext();
        ctx.Users.Seed(new User(42, "me@example.com",
            PasswordHelper.Hash("Password123!"), DateTime.UtcNow));
        var client = ctx.CreateClient();
        await client.PostAsJsonAsync("/v1/users/login",
            new { email = "me@example.com", password = "Password123!" });

        var response = await client.GetAsync("/v1/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_current_user_unauthenticated_returns_401()
    {
        using var ctx = new FakeContext();
        var response = await ctx.CreateClient().GetAsync("/v1/users/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_returns_200()
    {
        using var ctx = new FakeContext();
        ctx.Users.Seed(new User(1, "user@example.com",
            PasswordHelper.Hash("Password123!"), DateTime.UtcNow));
        var client = ctx.CreateClient();
        await client.PostAsJsonAsync("/v1/users/login",
            new { email = "user@example.com", password = "Password123!" });

        var response = await client.PostAsync("/v1/users/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
