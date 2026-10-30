using System.Net.Http.Json;
using UrlShortener.Api.Data;
using UrlShortener.Api.Users;
using UrlShortener.Tests.Fakes;

namespace UrlShortener.Tests.Snapshots;

[UsesVerify]
public class ApiSnapshotTests
{
    private static VerifySettings StableSettings()
    {
        var s = new VerifySettings();
        s.ScrubMember("createdAt");
        s.ScrubMember("id");
        s.ScrubMember("code");
        s.ScrubMember("traceId");
        return s;
    }

    [Fact]
    public async Task Register_response_matches_snapshot()
    {
        using var ctx = new FakeContext();
        var response = await ctx.CreateClient().PostAsJsonAsync("/v1/users/register",
            new { email = "snap@example.com", password = "Password123!" });

        var json = await response.Content.ReadAsStringAsync();
        await VerifyJson(json, StableSettings());
    }

    [Fact]
    public async Task Login_response_matches_snapshot()
    {
        using var ctx = new FakeContext();
        ctx.Users.Seed(new User(1, "snap@example.com",
            PasswordHelper.Hash("Password123!"), DateTime.UtcNow));

        var response = await ctx.CreateClient().PostAsJsonAsync("/v1/users/login",
            new { email = "snap@example.com", password = "Password123!" });

        var json = await response.Content.ReadAsStringAsync();
        await VerifyJson(json, StableSettings());
    }

    [Fact]
    public async Task Create_url_response_matches_snapshot()
    {
        using var ctx = new FakeContext();
        ctx.Users.Seed(new User(1, "snap@example.com",
            PasswordHelper.Hash("Password123!"), DateTime.UtcNow));
        var client = ctx.CreateClient();
        await client.PostAsJsonAsync("/v1/users/login",
            new { email = "snap@example.com", password = "Password123!" });

        var response = await client.PostAsJsonAsync("/v1/urls",
            new { targetUrl = "https://example.com" });

        var json = await response.Content.ReadAsStringAsync();
        await VerifyJson(json, StableSettings());
    }

    [Fact]
    public async Task Validation_error_response_matches_snapshot()
    {
        using var ctx = new FakeContext();
        var response = await ctx.CreateClient().PostAsJsonAsync("/v1/users/register",
            new { email = "not-an-email", password = "x" });

        var settings = StableSettings();
        settings.ScrubMember("instance");
        var json = await response.Content.ReadAsStringAsync();
        await VerifyJson(json, settings);
    }

    [Fact]
    public async Task Health_response_matches_snapshot()
    {
        using var ctx = new FakeContext();
        var response = await ctx.CreateClient().GetAsync("/health");
        var json = await response.Content.ReadAsStringAsync();
        await VerifyJson(json, StableSettings());
    }

    [Fact]
    public async Task OpenApi_document_matches_snapshot()
    {
        using var ctx = new FakeContext();
        var response = await ctx.CreateClient().GetAsync("/swagger/v1/swagger.json");

        var settings = StableSettings();
        settings.ScrubMember("servers");
        var json = await response.Content.ReadAsStringAsync();
        await VerifyJson(json, settings);
    }
}
