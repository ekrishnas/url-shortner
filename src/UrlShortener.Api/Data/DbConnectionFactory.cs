using System.Diagnostics.CodeAnalysis;
using Npgsql;

namespace UrlShortener.Api.Data;

public interface IDbConnectionFactory
{
    NpgsqlConnection CreateWrite();
    NpgsqlConnection CreateRead();
}

[ExcludeFromCodeCoverage]
public sealed class DbConnectionFactory(IConfiguration config) : IDbConnectionFactory
{
    public NpgsqlConnection CreateWrite() =>
        new(config.GetConnectionString("Write")
            ?? throw new InvalidOperationException("ConnectionStrings:Write is not configured."));

    public NpgsqlConnection CreateRead() =>
        new(config.GetConnectionString("Read")
            ?? config.GetConnectionString("Write")
            ?? throw new InvalidOperationException("ConnectionStrings:Read is not configured."));
}
