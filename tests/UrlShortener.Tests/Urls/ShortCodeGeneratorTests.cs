using Microsoft.Extensions.Configuration;
using UrlShortener.Api.Urls;

namespace UrlShortener.Tests.Urls;

public class ShortCodeGeneratorTests
{
    private static ShortCodeGenerator Make() =>
        new(new ConfigurationBuilder().Build());

    [Fact]
    public void Generate_returns_non_empty_code()
    {
        var gen = Make();
        var code = gen.Generate(1);
        Assert.False(string.IsNullOrEmpty(code));
    }

    [Fact]
    public void Generate_returns_code_at_least_6_chars()
    {
        var gen = Make();
        Assert.True(gen.Generate(1).Length >= 6);
        Assert.True(gen.Generate(999999).Length >= 6);
    }

    [Fact]
    public void Generate_does_not_return_reserved_route()
    {
        var gen = Make();
        for (long i = 1; i <= 100; i++)
        {
            var code = gen.Generate(i);
            Assert.False(UrlShortener.Api.ReservedRoutes.IsReserved(code),
                $"Generated reserved code '{code}' for id {i}");
        }
    }

    [Fact]
    public void Generate_is_deterministic_for_same_id()
    {
        var gen = Make();
        Assert.Equal(gen.Generate(42), gen.Generate(42));
    }
}
