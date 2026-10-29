using Dapper;

namespace UrlShortener.Api.Data;

public interface IUrlRepository
{
    Task<ShortUrl?> FindByCodeAsync(string code, CancellationToken ct = default);
    Task<IEnumerable<(ShortUrl Url, long ClickCount)>> ListByUserAsync(long userId, int page, int pageSize, CancellationToken ct = default);
    Task<(ShortUrl Url, long ClickCount)?> GetByIdForUserAsync(long id, long userId, CancellationToken ct = default);
    Task<ShortUrl> CreateAsync(string code, string targetUrl, long userId, CancellationToken ct = default);
    Task UpdateCodeAsync(long id, string code, CancellationToken ct = default);
    Task<bool> DeactivateAsync(long id, long userId, CancellationToken ct = default);
    Task<bool> DeleteAsync(long id, long userId, CancellationToken ct = default);
    Task RecordClickAsync(long shortUrlId, string? userAgent, string? ipAddress, CancellationToken ct = default);
    Task UpsertStatsAsync(long shortUrlId, CancellationToken ct = default);
}

public sealed class UrlRepository(IDbConnectionFactory db) : IUrlRepository
{
    public async Task<ShortUrl?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        await using var conn = db.CreateRead();
        return await conn.QuerySingleOrDefaultAsync<ShortUrl>(
            "SELECT id, code, target_url, user_id, is_active, created_at FROM short_urls WHERE code = @code AND is_active = true",
            new { code });
    }

    public async Task<IEnumerable<(ShortUrl Url, long ClickCount)>> ListByUserAsync(long userId, int page, int pageSize, CancellationToken ct = default)
    {
        await using var conn = db.CreateRead();
        var rows = await conn.QueryAsync<ShortUrl, long, (ShortUrl, long)>(
            @"SELECT s.id, s.code, s.target_url, s.user_id, s.is_active, s.created_at,
                     COALESCE(st.click_count, 0) AS click_count
              FROM short_urls s
              LEFT JOIN url_stats st ON st.short_url_id = s.id
              WHERE s.user_id = @userId
              ORDER BY s.created_at DESC
              LIMIT @pageSize OFFSET @offset",
            (url, clicks) => (url, clicks),
            new { userId, pageSize, offset = (page - 1) * pageSize },
            splitOn: "click_count");
        return rows;
    }

    public async Task<(ShortUrl Url, long ClickCount)?> GetByIdForUserAsync(long id, long userId, CancellationToken ct = default)
    {
        await using var conn = db.CreateRead();
        var rows = await conn.QueryAsync<ShortUrl, long, (ShortUrl, long)>(
            @"SELECT s.id, s.code, s.target_url, s.user_id, s.is_active, s.created_at,
                     COALESCE(st.click_count, 0) AS click_count
              FROM short_urls s
              LEFT JOIN url_stats st ON st.short_url_id = s.id
              WHERE s.id = @id AND s.user_id = @userId",
            (url, clicks) => (url, clicks),
            new { id, userId },
            splitOn: "click_count");
        var result = rows.FirstOrDefault();
        if (result.Item1 is null) return null;
        return result;
    }

    public async Task<ShortUrl> CreateAsync(string code, string targetUrl, long userId, CancellationToken ct = default)
    {
        await using var conn = db.CreateWrite();
        return await conn.QuerySingleAsync<ShortUrl>(
            @"INSERT INTO short_urls(code, target_url, user_id)
              VALUES(@code, @targetUrl, @userId)
              RETURNING id, code, target_url, user_id, is_active, created_at",
            new { code, targetUrl, userId });
    }

    public async Task UpdateCodeAsync(long id, string code, CancellationToken ct = default)
    {
        await using var conn = db.CreateWrite();
        await conn.ExecuteAsync(
            "UPDATE short_urls SET code = @code WHERE id = @id",
            new { id, code });
    }

    public async Task<bool> DeactivateAsync(long id, long userId, CancellationToken ct = default)
    {
        await using var conn = db.CreateWrite();
        var rows = await conn.ExecuteAsync(
            "UPDATE short_urls SET is_active = false WHERE id = @id AND user_id = @userId AND is_active = true",
            new { id, userId });
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(long id, long userId, CancellationToken ct = default)
    {
        await using var conn = db.CreateWrite();
        var rows = await conn.ExecuteAsync(
            "DELETE FROM short_urls WHERE id = @id AND user_id = @userId",
            new { id, userId });
        return rows > 0;
    }

    public async Task RecordClickAsync(long shortUrlId, string? userAgent, string? ipAddress, CancellationToken ct = default)
    {
        await using var conn = db.CreateWrite();
        await conn.ExecuteAsync(
            "INSERT INTO click_events(short_url_id, user_agent, ip_address) VALUES(@shortUrlId, @userAgent, @ipAddress)",
            new { shortUrlId, userAgent, ipAddress });
    }

    public async Task UpsertStatsAsync(long shortUrlId, CancellationToken ct = default)
    {
        await using var conn = db.CreateWrite();
        await conn.ExecuteAsync(
            @"INSERT INTO url_stats(short_url_id, click_count, last_clicked_at)
              VALUES(@shortUrlId, 1, now())
              ON CONFLICT (short_url_id) DO UPDATE
                SET click_count = url_stats.click_count + 1,
                    last_clicked_at = now()",
            new { shortUrlId });
    }
}
