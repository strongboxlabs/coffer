using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// The startup gate that decides whether a key-less boot is legal (ADR-0092 D3).
/// The stakes are asymmetric and that asymmetry is what these tests pin: a false
/// "virgin" mints a fresh KEK over live wrapped material and orphans it, while a
/// false "not virgin" merely refuses to boot, which the operator resolves by
/// supplying the key or passing <c>--adopt-new-kek</c>. So every uncertain
/// outcome must report true.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WrappedMaterialProbeTests
{
    private readonly PostgresFixture _fixture;

    public WrappedMaterialProbeTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reports_wrapped_material_when_a_ledger_carries_a_wrapped_lek()
    {
        // Seeded here rather than assumed: relying on other tests in the shared
        // collection to have left a wrapped LEK behind makes this order-dependent
        // and it silently passed for the wrong reason the first time.
        await using var db = _fixture.NewDbContext();
        db.Ledgers.Add(new LedgerRow
        {
            Id = Guid.NewGuid(),
            Name = $"probe-{Guid.NewGuid():N}",
            WrappedLek = new LedgerKeyService(new MasterKey(new byte[32], "v1")).CreateWrappedLek(),
            LekKekId = "v1",
        });
        await db.SaveChangesAsync();

        Assert.True(WrappedMaterialProbe.Exists(_fixture.ServiceConnectionString));
    }

    [Fact]
    public void Reports_true_and_surfaces_the_reason_when_the_database_is_unreachable()
    {
        // Fails CLOSED. An unreachable database is not evidence of a virgin
        // install, and treating it as one is the expensive mistake.
        Exception? captured = null;
        var unreachable = "Host=127.0.0.1;Port=1;Database=nope;Username=nobody;"
            + "Password=nobody;Timeout=1;Command Timeout=1";

        var result = WrappedMaterialProbe.Exists(unreachable, ex => captured = ex);

        Assert.True(result);
        Assert.NotNull(captured);
    }

    [Fact]
    public void Reports_false_on_a_reachable_database_with_no_schema()
    {
        // The genuinely virgin case, and the one that matters most: the API's
        // first boot probes BEFORE migrations, so none of the three tables exist.
        // Every check therefore raises 42P01, and that has to read as "absent",
        // not as a probe failure — the fail-closed branch would refuse the boot
        // and brick every fresh install.
        Exception? captured = null;

        var result = WrappedMaterialProbe.Exists(
            _fixture.EmptyDatabaseConnectionString(), ex => captured = ex);

        Assert.Null(captured);
        Assert.False(result);
    }

    [Fact]
    public void Reports_false_on_a_migrated_database_with_no_wrapped_values()
    {
        // Distinct path from the one above: the tables DO exist and every wrapped
        // column is null. That's an install which migrated but never created a
        // ledger — still virgin for D3's purposes, so a key may be minted.
        var connectionString = _fixture.EmptyDatabaseConnectionString("coffer_migrated_probe");
        MigrationRunner.Run(
            connectionString,
            MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory),
            NullLogger.Instance);

        Exception? captured = null;
        var result = WrappedMaterialProbe.Exists(connectionString, ex => captured = ex);

        Assert.Null(captured);
        Assert.False(result);
    }

    [Fact]
    public void Rejects_a_blank_connection_string()
        => Assert.Throws<ArgumentException>(() => WrappedMaterialProbe.Exists("  "));
}
