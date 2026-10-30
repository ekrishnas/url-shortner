using System.Security.Claims;
using FastEndpoints;

namespace UrlShortener.Api.Users;

public sealed record CurrentUserResponse(long Id, string Email);

public sealed class GetCurrentUserEndpoint : EndpointWithoutRequest<CurrentUserResponse>
{
    public override void Configure()
    {
        Get("/v1/users/me");
        Tags("Users");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = User.FindFirstValue(ClaimTypes.Email)!;
        await HttpContext.Response.SendAsync(new CurrentUserResponse(id, email), cancellation: ct);
    }
}
