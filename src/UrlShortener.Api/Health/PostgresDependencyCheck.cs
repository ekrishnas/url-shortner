using Npgsql;

namespace UrlShortener.Api.Health;

public sealed class PostgresDependencyCheck(string name, string connectionStringName, IConfiguration configuration) : IDependencyCheck
{
    public string Name => name;

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var connectionString = configuration.GetConnectionString(connectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return false;
            }

            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Timeout = 2,
                CommandTimeout = 2,
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
