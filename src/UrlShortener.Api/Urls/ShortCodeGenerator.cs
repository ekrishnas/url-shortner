using Sqids;

namespace UrlShortener.Api.Urls;

public sealed class ShortCodeGenerator(IConfiguration config)
{
    private static readonly SqidsEncoder<long> Encoder = new(new SqidsOptions { MinLength = 6 });

    public string Generate(long id)
    {
        var code = Encoder.Encode(id);
        return ReservedRoutes.IsReserved(code) ? Encoder.Encode(id + 1_000_000) : code;
    }
}
