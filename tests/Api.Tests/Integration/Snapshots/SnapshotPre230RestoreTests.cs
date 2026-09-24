using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Snapshots;

/// <summary>
/// Restoring a snapshot captured BEFORE migration 230 keeps the user's edits.
/// </summary>
/// <remarks>
/// <para>Before 230, <c>txn_headers</c> held the feed's values and
/// <c>txn_header_overrides</c> held the user's, resolved as
/// <c>COALESCE(o.x, h.x)</c>. 230 flipped that and dropped the table — so a
/// payload written by the old schema carries the user's work under a key the
/// new restore has no reason to look at. Ignoring it would reinsert the FEED
/// values and silently discard every edit in the ledger, on the
/// disaster-recovery path, with a 200 and nothing on screen to say so.</para>
///
/// <para>The <c>roundtrip</c> preflight stage cannot catch this: it round-trips
/// the CURRENT shape, never an old payload forward across a schema change. So
/// the payload here is built by taking a live one and rewriting it into the old
/// shape — feed values on the header, edits in an overrides array — which is
/// exactly what sits in a backup file taken last month.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SnapshotPre230RestoreTests
{
    private readonly PostgresFixture _fixture;

    public SnapshotPre230RestoreTests(PostgresFixture fixture) => _fixture = fixture;

    private static readonly DateTime FeedDate = new(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CuratedDate = new(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Restoring_a_pre_230_payload_folds_its_overrides_onto_the_headers()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");

        // The row as the FEED delivered it — which is what a pre-230
        // txn_headers row holds, edited or not.
        var (legId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -42m, FeedDate, payee: "SQ *COFFEE 0304");
        var headerId = await ledger.ResolveHeaderIdAsync(legId);

        await using var db = _fixture.NewDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var payload = JsonNode.Parse(await PayloadAsync(conn, ledger.LedgerId))!.AsObject();

        // Rewrite into the old shape: the sidecar 230 introduced does not exist,
        // and the user's edits live in an overrides array. PARTIAL on purpose —
        // payee and posted_at only — because most real override rows carried a
        // couple of fields, and it is the partial ones that catch a fold written
        // as an assignment instead of a COALESCE.
        payload.Remove("txn_header_originals");
        payload["txn_header_overrides"] = new JsonArray(
            new JsonObject
            {
                ["header_id"] = headerId.ToString(),
                ["ledger_id"] = ledger.LedgerId.ToString(),
                ["payee"] = "Blue Bottle Coffee",
                ["memo"] = null,
                ["posted_at"] = CuratedDate.ToString("o"),
                ["transacted_at"] = null,
                ["check_number"] = null,
                ["is_hidden"] = null,
            });

        await RestoreAsync(conn, ledger.LedgerId, payload.ToJsonString());

        await using var after = _fixture.NewDbContext();
        var header = await after.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == headerId);

        // The edits survived...
        Assert.Equal("Blue Bottle Coffee", header.Payee);
        Assert.Equal(CuratedDate, header.PostedAt);
        // ...and a field the override row left NULL was not blanked with it.
        // `SET transacted_at = o.transacted_at` would have nulled a NOT NULL
        // column here; the fold has to COALESCE.
        Assert.Equal(FeedDate, header.TransactedAt);

        // ...and the feed's values are recoverable, same as a live edit.
        var original = await after.TxnHeaderOriginals.AsNoTracking()
            .SingleAsync(o => o.HeaderId == headerId);
        Assert.Equal("SQ *COFFEE 0304", original.Payee);
        Assert.Equal(FeedDate, original.PostedAt);
    }

    [Fact]
    public async Task Restoring_a_post_230_payload_is_unaffected_by_the_compatibility_fold()
    {
        // The fold is gated on the old key being present. Without this, a
        // no-op-looking IF could quietly run on every restore — and the
        // pre-230 test above would still pass, because it only proves the
        // gate's TRUE arm.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");

        var (legId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -42m, FeedDate, payee: "SQ *COFFEE 0304");
        var headerId = await ledger.ResolveHeaderIdAsync(legId);
        await ledger.EditHeaderAsync(headerId, payee: "Blue Bottle Coffee", postedAt: CuratedDate);

        await using var db = _fixture.NewDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        var payload = await PayloadAsync(conn, ledger.LedgerId);
        Assert.DoesNotContain("txn_header_overrides", payload, StringComparison.Ordinal);

        await RestoreAsync(conn, ledger.LedgerId, payload);

        await using var after = _fixture.NewDbContext();
        var header = await after.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == headerId);
        Assert.Equal("Blue Bottle Coffee", header.Payee);
        Assert.Equal(CuratedDate, header.PostedAt);

        var original = await after.TxnHeaderOriginals.AsNoTracking()
            .SingleAsync(o => o.HeaderId == headerId);
        Assert.Equal("SQ *COFFEE 0304", original.Payee);
        Assert.Equal(FeedDate, original.PostedAt);
    }

    private static async Task<string> PayloadAsync(DbConnection conn, Guid ledgerId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fn_ledger_snapshot_payload(@l)::text";
        var p = cmd.CreateParameter();
        p.ParameterName = "l";
        p.Value = ledgerId;
        cmd.Parameters.Add(p);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task RestoreAsync(DbConnection conn, Guid ledgerId, string payload)
    {
        // Called directly, not through POST /snapshots/{id}/restore: no endpoint
        // accepts a hand-built payload, and a hand-built payload is the whole
        // subject. Parse-check first so a malformed rewrite fails here rather
        // than as an opaque plpgsql error.
        using (JsonDocument.Parse(payload)) { }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fn_ledger_snapshot_restore(@l, @p)";
        var l = cmd.CreateParameter();
        l.ParameterName = "l";
        l.Value = ledgerId;
        cmd.Parameters.Add(l);
        var pp = cmd.CreateParameter();
        pp.ParameterName = "p";
        pp.Value = payload;
        cmd.Parameters.Add(pp);
        await cmd.ExecuteNonQueryAsync();
    }
}
