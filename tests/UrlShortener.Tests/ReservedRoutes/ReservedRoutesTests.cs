using UrlShortener.Api;

namespace UrlShortener.Tests.ReservedRoutes;

public class ReservedRoutesTests
{
    [Theory]
    [InlineData("v1")]
    [InlineData("V1")]
    [InlineData("health")]
    [InlineData("HEALTH")]
    [InlineData("swagger")]
    [InlineData("SWAGGER")]
    public void Reserved_names_are_reserved(string name) =>
        Assert.True(UrlShortener.Api.ReservedRoutes.IsReserved(name));

    [Theory]
    [InlineData("healthz")]
    [InlineData("v2")]
    [InlineData("swaggerui")]
    [InlineData("api")]
    [InlineData(" health")]
    public void Near_misses_are_not_reserved(string name) =>
        Assert.False(UrlShortener.Api.ReservedRoutes.IsReserved(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_empty_is_not_reserved(string? name) =>
        Assert.False(UrlShortener.Api.ReservedRoutes.IsReserved(name));
}
