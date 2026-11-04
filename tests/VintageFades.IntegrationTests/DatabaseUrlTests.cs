using Npgsql;
using VintageFades.Infrastructure.Persistence;

namespace VintageFades.IntegrationTests;

public class DatabaseUrlTests
{
    [Fact]
    public void Converts_platform_database_url()
    {
        var cs = new NpgsqlConnectionStringBuilder(
            DatabaseUrl.ToConnectionString("postgresql://app%40user:p%40ss:word@db.internal:6543/vintage?sslmode=require"));
        Assert.Equal("db.internal", cs.Host);
        Assert.Equal(6543, cs.Port);
        Assert.Equal("vintage", cs.Database);
        Assert.Equal("app@user", cs.Username);
        Assert.Equal("p@ss:word", cs.Password);
        Assert.Equal(SslMode.Require, cs.SslMode);
    }

    [Fact]
    public void Defaults_port_and_prefers_explicit_connection_string()
    {
        Assert.Equal(5432, new NpgsqlConnectionStringBuilder(DatabaseUrl.ToConnectionString("postgres://u:p@h/d")).Port);
        Assert.Equal("Host=x", DatabaseUrl.Resolve("Host=x", "postgres://u:p@h/d"));
        Assert.Null(DatabaseUrl.Resolve(null, ""));
        Assert.Throws<ArgumentException>(() => DatabaseUrl.ToConnectionString("mysql://u:p@h/d"));
    }
}
