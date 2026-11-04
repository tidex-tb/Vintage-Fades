using System.Web;
using Npgsql;

namespace VintageFades.Infrastructure.Persistence;

/// <summary>
/// Hosting platforms (Railway, Render, Heroku-style) expose PostgreSQL as a URL:
/// postgres://user:password@host:port/database?sslmode=require. Npgsql needs key=value pairs, so convert it.
/// </summary>
public static class DatabaseUrl
{
    public static string ToConnectionString(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme is not ("postgres" or "postgresql"))
            throw new ArgumentException("DATABASE_URL must start with postgres:// or postgresql://.", nameof(url));

        var credentials = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null,
            GssEncryptionMode = GssEncryptionMode.Disable
        };

        var sslMode = HttpUtility.ParseQueryString(uri.Query)["sslmode"];
        if (!string.IsNullOrEmpty(sslMode) && Enum.TryParse<SslMode>(sslMode.Replace("-", ""), ignoreCase: true, out var mode))
            builder.SslMode = mode;
        return builder.ConnectionString;
    }

    /// <summary>ConnectionStrings:DefaultConnection wins; otherwise DATABASE_URL is converted.</summary>
    public static string? Resolve(string? connectionString, string? databaseUrl) =>
        !string.IsNullOrWhiteSpace(connectionString) ? connectionString
        : !string.IsNullOrWhiteSpace(databaseUrl) ? ToConnectionString(databaseUrl)
        : null;
}
