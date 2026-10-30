using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Api.Data;

namespace UrlShortener.Tests.Fakes;

public sealed class FakeContext : IDisposable
{
    public WebApplicationFactory<Program> Factory { get; }
    public FakeUserRepository Users { get; }
    public FakeUrlRepository Urls { get; }

    public FakeContext()
    {
        Users = new FakeUserRepository();
        Urls = new FakeUrlRepository();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Test");
            b.ConfigureTestServices(s =>
            {
                s.AddScoped<IUserRepository>(_ => Users);
                s.AddScoped<IUrlRepository>(_ => Urls);
            });
        });
    }

    public HttpClient CreateClient() => Factory.CreateClient();

    public HttpClient CreateClientNoRedirect() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public void Dispose() => Factory.Dispose();
}
