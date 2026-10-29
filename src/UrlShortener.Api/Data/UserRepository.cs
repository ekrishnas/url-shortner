using Dapper;

namespace UrlShortener.Api.Data;

public interface IUserRepository
{
    Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<long> CreateAsync(string email, string passwordHash, CancellationToken ct = default);
}

public sealed class UserRepository(IDbConnectionFactory db) : IUserRepository
{
    public async Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var conn = db.CreateRead();
        return await conn.QuerySingleOrDefaultAsync<User>(
            "SELECT id, email, password_hash, created_at FROM users WHERE email = @email",
            new { email });
    }

    public async Task<long> CreateAsync(string email, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = db.CreateWrite();
        return await conn.ExecuteScalarAsync<long>(
            "INSERT INTO users(email, password_hash) VALUES(@email, @passwordHash) RETURNING id",
            new { email, passwordHash });
    }
}
