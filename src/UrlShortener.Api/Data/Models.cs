namespace UrlShortener.Api.Data;

public sealed record User(long Id, string Email, string PasswordHash, DateTimeOffset CreatedAt);

public sealed record ShortUrl(
    long Id,
    string Code,
    string TargetUrl,
    long UserId,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record UrlStats(long ShortUrlId, long ClickCount, DateTimeOffset? LastClickedAt);

public sealed record ClickEvent(
    long ShortUrlId,
    DateTimeOffset ClickedAt,
    string? UserAgent,
    string? IpAddress);
