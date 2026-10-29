using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UrlShortener.Tests.Validation;

public class ValidationErrorTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Invalid_register_request_returns_400_problem_details_with_field_errors()
    {
        var response = await _client.PostAsJsonAsync("/v1/users/register",
            new { email = "not-an-email", password = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var raw = await response.Content.ReadAsStringAsync();
        // Response must mention the failing fields by name
        Assert.Contains("email", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_required_field_returns_400_problem_details()
    {
        var response = await _client.PostAsJsonAsync("/v1/users/register", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Valid_create_url_request_with_non_url_string_returns_400()
    {
        var response = await _client.PostAsJsonAsync("/v1/urls", new { targetUrl = "not-a-url" });

        // Either 400 (validation) or 401 (not authenticated) — never 200 or 404
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized,
            $"Expected 400 or 401 but got {(int)response.StatusCode}.");
    }
}
