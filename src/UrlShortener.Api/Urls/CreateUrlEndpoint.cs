using System.Security.Claims;
using FastEndpoints;
using FluentValidation;
using UrlShortener.Api.Data;

namespace UrlShortener.Api.Urls;

public sealed record CreateUrlRequest(string TargetUrl);
public sealed record CreateUrlResponse(long Id, string Code, string TargetUrl, DateTimeOffset CreatedAt);

public sealed class CreateUrlValidator : Validator<CreateUrlRequest>
{
    public CreateUrlValidator()
    {
        RuleFor(x => x.TargetUrl).NotEmpty().MaximumLength(2048)
            .Must(u => Uri.TryCreate(u, UriKind.Absolute, out var r) && (r.Scheme == "http" || r.Scheme == "https"))
            .WithMessage("TargetUrl must be a valid http or https URL.");
    }
}

public sealed class CreateUrlEndpoint(IUrlRepository urls, ShortCodeGenerator codeGen) : Endpoint<CreateUrlRequest, CreateUrlResponse>
{
    public override void Configure()
    {
        Post("/v1/urls");
        Tags("Urls");
    }

    public override async Task HandleAsync(CreateUrlRequest req, CancellationToken ct)
    {
        var userId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var tempCode = Guid.NewGuid().ToString("N")[..12];
        var url = await urls.CreateAsync(tempCode, req.TargetUrl, userId, ct);
        var code = codeGen.Generate(url.Id);
        await urls.UpdateCodeAsync(url.Id, code, ct);
        await HttpContext.Response.SendAsync(new CreateUrlResponse(url.Id, code, url.TargetUrl, url.CreatedAt), 201, cancellation: ct);
    }
}
