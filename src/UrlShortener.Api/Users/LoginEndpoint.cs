using System.Security.Claims;
using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using UrlShortener.Api.Data;

namespace UrlShortener.Api.Users;

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(long Id, string Email);

public sealed class LoginValidator : Validator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class LoginEndpoint(IUserRepository users) : Endpoint<LoginRequest, LoginResponse>
{
    public override void Configure()
    {
        Post("/v1/users/login");
        AllowAnonymous();
        Tags("Users");
    }

    public override async Task HandleAsync(LoginRequest req, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email, ct);
        if (user is null || !PasswordHelper.Verify(req.Password, user.PasswordHash))
        {
            AddError("Invalid email or password.");
            await HttpContext.Response.SendErrorsAsync(ValidationFailures, 401, cancellation: ct);
            return;
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        await HttpContext.Response.SendAsync(new LoginResponse(user.Id, user.Email), 200, cancellation: ct);
    }
}
