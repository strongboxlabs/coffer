using Coffer.Api.Backup;

namespace Coffer.Api.Tests.Unit.Backup;

/// <summary>
/// Unit tests for the pure parts of <see cref="BackupService"/>. The
/// pg_dump/pg_restore shell-out itself is exercised end-to-end in the Docker
/// image (which ships postgresql-client); here we lock down the
/// connection-string → libpq env mapping the tools depend on.
/// </summary>
public sealed class BackupServiceTests
{
    [Fact]
    public void BuildPgEnvironment_maps_npgsql_connection_to_libpq_vars()
    {
        var (env, database) = BackupService.BuildPgEnvironment(
            "Host=db.internal;Port=6543;Username=coffer_service;Password=p@ss;Database=coffer");

        Assert.Equal("db.internal", env["PGHOST"]);
        Assert.Equal("6543", env["PGPORT"]);
        Assert.Equal("coffer_service", env["PGUSER"]);
        Assert.Equal("p@ss", env["PGPASSWORD"]);
        Assert.Equal("coffer", env["PGDATABASE"]);
        Assert.Equal("coffer", database);
    }

    [Fact]
    public void BuildPgEnvironment_defaults_the_port_when_unspecified()
    {
        var (env, _) = BackupService.BuildPgEnvironment(
            "Host=localhost;Username=u;Password=p;Database=coffer");

        // Npgsql's default port; the libpq tools must get an explicit value.
        Assert.Equal("5432", env["PGPORT"]);
    }
}
