namespace UrlShortener.Api;

public static class ReservedRoutes
{
    private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "v1", "health", "swagger"
    };

    public static bool IsReserved(string? code) =>
        !string.IsNullOrEmpty(code) && _reserved.Contains(code);

    public static IReadOnlySet<string> All => _reserved;
}
