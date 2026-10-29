using UrlShortener.Api.Data;

namespace UrlShortener.Tests.Fakes;

public sealed class FakeUserRepository : IUserRepository
{
    private readonly Dictionary<string, User> _byEmail = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId = 1;

    public void Seed(User user) => _byEmail[user.Email] = user;

    public Task<User?> FindByEmailAsync(string email, CancellationToken ct = default) =>
        Task.FromResult(_byEmail.TryGetValue(email, out var u) ? u : null);

    public Task<long> CreateAsync(string email, string passwordHash, CancellationToken ct = default)
    {
        var id = _nextId++;
        _byEmail[email] = new User(id, email, passwordHash, DateTimeOffset.UtcNow);
        return Task.FromResult(id);
    }
}

public sealed class FakeUrlRepository : IUrlRepository
{
    private readonly Dictionary<long, ShortUrl> _byId = new();
    private readonly Dictionary<string, ShortUrl> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId = 1;

    public void Seed(ShortUrl url) { _byId[url.Id] = url; _byCode[url.Code] = url; }

    public Task<ShortUrl?> FindByCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(_byCode.TryGetValue(code, out var u) ? u : null);

    public Task<IEnumerable<(ShortUrl Url, long ClickCount)>> ListByUserAsync(
        long userId, int page, int pageSize, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values
            .Where(u => u.UserId == userId)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => (u, 0L)));

    public Task<(ShortUrl Url, long ClickCount)?> GetByIdForUserAsync(
        long id, long userId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var u) && u.UserId == userId)
            return Task.FromResult<(ShortUrl, long)?>(( u, 0L));
        return Task.FromResult<(ShortUrl, long)?>(null);
    }

    public Task<ShortUrl> CreateAsync(string code, string targetUrl, long userId, CancellationToken ct = default)
    {
        var id = _nextId++;
        var url = new ShortUrl(id, code, targetUrl, userId, true, DateTimeOffset.UtcNow);
        _byId[id] = url;
        _byCode[code] = url;
        return Task.FromResult(url);
    }

    public Task UpdateCodeAsync(long id, string code, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var url))
        {
            _byCode.Remove(url.Code);
            var updated = url with { Code = code };
            _byId[id] = updated;
            _byCode[code] = updated;
        }
        return Task.CompletedTask;
    }

    public Task<bool> DeactivateAsync(long id, long userId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var url) && url.UserId == userId)
        {
            _byId[id] = url with { IsActive = false };
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> DeleteAsync(long id, long userId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var url) && url.UserId == userId)
        {
            _byId.Remove(id);
            _byCode.Remove(url.Code);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task RecordClickAsync(long shortUrlId, string? userAgent, string? ipAddress, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpsertStatsAsync(long shortUrlId, CancellationToken ct = default) =>
        Task.CompletedTask;
}
