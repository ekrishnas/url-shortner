using FastEndpoints;
using FluentValidation;
using UrlShortener.Api.Data;

namespace UrlShortener.Api.Users;

public sealed record RegisterRequest(string Email, string Password);
public sealed record RegisterResponse(long Id, string Email);

public sealed class RegisterValidator : Validator<RegisterRequest>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

public sealed class RegisterEndpoint(IUserRepository users) : Endpoint<RegisterRequest, RegisterResponse>
{
    public override void Configure()
    {
        Post("/v1/users/register");
        AllowAnonymous();
        Tags("Users");
    }

    public override async Task HandleAsync(RegisterRequest req, CancellationToken ct)
    {
        var existing = await users.FindByEmailAsync(req.Email, ct);
        if (existing is not null)
        {
            AddError(r => r.Email, "Email is already registered.");
            await HttpContext.Response.SendErrorsAsync(ValidationFailures, 409, cancellation: ct);
            return;
        }

        var hash = PasswordHelper.Hash(req.Password);
        var id = await users.CreateAsync(req.Email, hash, ct);
        await HttpContext.Response.SendAsync(new RegisterResponse(id, req.Email), 201, cancellation: ct);
    }
}
