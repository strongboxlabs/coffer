using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Snapshots;

/// <summary>
/// Row-level security on the snapshot tables (migration 231).
///
/// <para><c>ledger_snapshots</c> holds a complete copy of every row of a ledger —
/// the same rows its source tables protect with RLS — and had none, nor did
/// <c>ledger_snapshot_parts</c>. Both were gated only by the API's
/// <c>LedgerAuthorizer</c>, so anything reaching the database as
/// <c>coffer_app</c> outside that gate could read any ledger's full contents.</para>
///
/// <para>The isolation test goes UNDER the API deliberately, as
/// <c>DeploymentScopeRlsTests</c> does: an endpoint test would pass on the
/// authorizer alone and prove nothing about the policy.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SnapshotTablesRlsTests
{
    private readonly PostgresFixture _fixture;

    public SnapshotTablesRlsTests(PostgresFixture fixture) => _fixture = fixture;

    // `ledger_snapshot_parts` has no EF mapping — it is written and read by the
    // migration-193 Postgres functions — so it is counted here in SQL. Raw SQL is
    // out of scope for audit-no-raw-sql.sh (src/Api only) and there is no LINQ
    // path to an unmapped table.
    private static async Task<int> CountPartsAsync(Coffer.Api.Db.AppDbContext db, Guid snapshotId)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT count(*)::int FROM ledger_snapshot_parts WHERE snapshot_id = @s";
        var p = cmd.CreateParameter();
        p.ParameterName = "s";
        p.Value = snapshotId;
        cmd.Parameters.Add(p);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task One_ledgers_snapshots_are_invisible_and_unwritable_to_another_ledgers_owner()
    {
        var mine = await SyntheticLedger.CreateAsync(_fixture);
        var theirs = await SyntheticLedger.CreateAsync(_fixture);

        async Task<Guid> SeedAsync(SyntheticLedger ledger)
        {
            await using var db = _fixture.NewDbContext();   // coffer_service, BYPASSRLS
            var snap = new LedgerSnapshotRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledger.LedgerId,
                CreatedAt = DateTime.UtcNow,
                CreatedByUserId = ledger.UserId,
                Kind = "manual",
                Description = "rls-probe",
                SchemaVersion = "231_rls_on_snapshot_tables.sql",
                ContentSizeUncompressed = 0,
            };
            db.LedgerSnapshots.Add(snap);
            await db.SaveChangesAsync();
            return snap.Id;
        }

        var mySnapshotId = await SeedAsync(mine);
        var theirSnapshotId = await SeedAsync(theirs);

        await using (var asMe = _fixture.NewAppDbContextAsUser(mine.UserId))
        {
            // A snapshot IS the ledger, so seeing someone else's row is seeing
            // their data.
            Assert.False(await asMe.LedgerSnapshots.AnyAsync(s => s.Id == theirSnapshotId));
            Assert.Equal(0, await asMe.LedgerSnapshots
                .Where(s => s.Id == theirSnapshotId).ExecuteDeleteAsync());

            // The SAME statements against my own row must succeed. Without this
            // the table could simply be inaccessible to coffer_app and every
            // assertion above would still pass — snapshots would be broken, not
            // protected. The policy has to DISCRIMINATE, not merely deny.
            Assert.True(await asMe.LedgerSnapshots.AnyAsync(s => s.Id == mySnapshotId));
        }

        // Symmetrically, so a policy that happened to favour the first-created
        // ledger would not pass either.
        await using (var asThem = _fixture.NewAppDbContextAsUser(theirs.UserId))
        {
            Assert.True(await asThem.LedgerSnapshots.AnyAsync(s => s.Id == theirSnapshotId));
            Assert.False(await asThem.LedgerSnapshots.AnyAsync(s => s.Id == mySnapshotId));
        }
    }

    [Fact]
    public async Task A_real_capture_still_writes_its_payload_parts_under_the_new_policies()
    {
        // THE failure mode the follow-up called out, and the reason this test
        // exists rather than a green suite being taken as proof: capture runs as
        // the CALLER — migration 193's functions carry no SECURITY DEFINER — so a
        // policy the request context cannot satisfy does not error. It makes
        // capture write nothing, and the emptiness is only discovered at restore.
        //
        // So this drives a real capture through the endpoint and then counts the
        // PART ROWS, which is the thing that would silently be zero.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/snapshots",
            new CreateSnapshotRequest("under-rls"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var snap = (await resp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        await using var asService = _fixture.NewDbContext();
        Assert.True(await CountPartsAsync(asService, snap.Id) > 0,
            "Capture wrote no ledger_snapshot_parts rows. The endpoint reported success, "
            + "so this is the silent-empty-capture failure the RLS policies risk: the "
            + "migration-193 write functions run as the caller and a policy the request "
            + "context cannot satisfy filters their INSERTs away instead of erroring.");
    }

    // Same shape as SnapshotsTests: the cookie needs its name, and cookie
    // handling must be off or the factory's own container competes with it.
    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }
}
