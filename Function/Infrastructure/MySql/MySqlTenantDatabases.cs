using System.Security.Cryptography;
using Function.Features;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Function.Infrastructure.MySql;

public class MySqlOptions
{
    public const string SectionName = "MySql";

    /// <summary>Connection used by this worker to create/drop tenant databases (needs CREATE USER and GRANT).</summary>
    public string AdminConnectionString { get; set; } = "";

    /// <summary>MySQL host:port as seen from the WordPress containers (docker network), e.g. rso-mysql:3306.</summary>
    public string HostForSites { get; set; } = "";
}

public record TenantDatabase(string Host, string Name, string User, string Password);

public interface ITenantDatabases
{
    /// <summary>Creates (or reuses) the site's database and user. The user's password is reset on every call.</summary>
    Task<TenantDatabase> CreateAsync(SiteKey site, CancellationToken ct);

    Task DropAsync(SiteKey site, CancellationToken ct);
}

public class MySqlTenantDatabases(IOptions<MySqlOptions> options, ILogger<MySqlTenantDatabases> logger) : ITenantDatabases
{
    private const string PasswordChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public async Task<TenantDatabase> CreateAsync(SiteKey site, CancellationToken ct)
    {
        var database = site.Database;
        var user = site.DatabaseUser;
        var password = RandomNumberGenerator.GetString(PasswordChars, 32);

        await using var connection = await OpenAsync(ct);
        // Identifiers come from SiteKey (validated / hashed), so interpolating them is safe. The password is a parameter.
        await ExecuteAsync(connection, $"CREATE DATABASE IF NOT EXISTS `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", ct);
        await ExecuteAsync(connection, $"CREATE USER IF NOT EXISTS '{user}'@'%' IDENTIFIED BY @password", ct, password);
        await ExecuteAsync(connection, $"ALTER USER '{user}'@'%' IDENTIFIED BY @password", ct, password);
        await ExecuteAsync(connection, $"GRANT ALL PRIVILEGES ON `{database}`.* TO '{user}'@'%'", ct);

        logger.LogInformation("Database {Database} with user {User} ready", database, user);
        return new TenantDatabase(options.Value.HostForSites, database, user, password);
    }

    public async Task DropAsync(SiteKey site, CancellationToken ct)
    {
        var database = site.Database;
        var user = site.DatabaseUser;

        await using var connection = await OpenAsync(ct);
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS `{database}`", ct);
        await ExecuteAsync(connection, $"DROP USER IF EXISTS '{user}'@'%'", ct);

        logger.LogInformation("Database {Database} and user {User} dropped", database, user);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(options.Value.AdminConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, CancellationToken ct, string? password = null)
    {
        await using var command = new MySqlCommand(sql, connection);
        if (password is not null) command.Parameters.AddWithValue("@password", password);
        await command.ExecuteNonQueryAsync(ct);
    }
}
