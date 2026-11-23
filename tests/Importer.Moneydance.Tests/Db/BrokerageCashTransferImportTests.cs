using Dapper;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// A brokerage cash move that Moneydance left without an investment txntype
/// lands as a 'transfer' all the way through: header action, both legs'
/// posting role, and the register's <c>derived_action</c>.
/// </summary>
/// <remarks>
/// <para>The mapper tests pin the classification; this pins that nothing
/// between the mapper and the register drops it. The register derives
/// <c>COALESCE(h.action, 'Xfr' only between two real accounts)</c>, so a
/// brokerage row whose other side is a CATEGORY showed no action at all
/// before, and the investment editor opened on "Pick action...".</para>
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class BrokerageCashTransferImportTests : DatabaseTest
{
    public BrokerageCashTransferImportTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_qif_cash_row_to_a_category_persists_and_reads_back_as_a_transfer()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;

        await ImportAsync(conn, ledgerId, targetAcct: "a-inc", extraAccounts: """
            {"obj_type":"acct","id":"a-inc","type":"i","name":"Interest","parentid":"a-root","currid":"c-usd"},
            """);

        var header = await conn.QuerySingleAsync<(Guid Id, string? Action, string? CheckNumber)>(
            "SELECT id, action, check_number FROM txn_headers WHERE ledger_id = @L;", new { L = ledgerId });
        Assert.Equal("transfer", header.Action);
        Assert.Equal("XOut", header.CheckNumber);

        var roles = (await conn.QueryAsync<string?>(
            "SELECT posting_role FROM txn_legs WHERE header_id = @H;", new { H = header.Id })).ToList();
        Assert.Equal(2, roles.Count);
        Assert.All(roles, r => Assert.Equal("transfer", r));

        var brokerageId = await AccountIdAsync(conn, ledgerId, "a-brk");
        var derived = await conn.QuerySingleAsync<string?>(
            "SELECT derived_action FROM resolved_transactions WHERE header_id = @H AND account_id = @A;",
            new { H = header.Id, A = brokerageId });
        Assert.Equal("transfer", derived);
    }

    [Fact]
    public async Task A_qif_cash_row_to_the_blank_category_lands_on_uncategorized_as_a_transfer()
    {
        // The full service, not just the steps: the blank category has no
        // account_external_ids junction of its own, and the import's
        // account-count parity check must still pass.
        await using var conn = Db.OpenConnection();

        var json = """
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
                {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
                {"obj_type":"acct","id":"a-blank","type":"e","name":"","parentid":"a-root","currid":"c-usd"},
                {"obj_type":"txn","id":"t-xout","acctid":"a-brk","desc":"Cash out","dt":"20260101","chk":"XOut",
                 "qif_invst_action":"XOut","xfer_type":"xfrtp_bank",
                 "0.id":"s-1","0.acctid":"a-blank","0.invest.splittype":"xfr",
                 "0.samt":"1000","0.pamt":"-1000"}
              ]
            }
            """;

        var result = await new MoneydanceImportService().ImportAsync(
            conn, MdItemReader.ReadString(json), ledgerName: $"Brokerage cash {Guid.NewGuid():N}",
            ownerUserId: LedgerRow.SystemUserId, progress: null, cancellationToken: default,
            beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync);
        var ledgerId = result.LedgerId;

        var parity = Assert.Single(result.Validation.Checks, c => c.Name == "account-count-parity");
        Assert.True(parity.Passed, parity.Message);

        var uncategorizedId = await conn.QuerySingleAsync<Guid>(
            "SELECT id FROM accounts WHERE ledger_id = @L AND is_system AND account_type = 'category' AND name = 'Uncategorized';",
            new { L = ledgerId });
        var header = await conn.QuerySingleAsync<(Guid Id, string? Action)>(
            "SELECT id, action FROM txn_headers WHERE ledger_id = @L;", new { L = ledgerId });
        Assert.Equal("transfer", header.Action);

        var brokerageId = await AccountIdAsync(conn, ledgerId, "a-brk");
        var legs = (await conn.QueryAsync<(Guid AccountId, string? PostingRole)>(
            "SELECT account_id, posting_role FROM txn_legs WHERE header_id = @H;", new { H = header.Id })).ToList();
        Assert.Equal(2, legs.Count);
        Assert.Contains(legs, l => l.AccountId == uncategorizedId);
        Assert.Contains(legs, l => l.AccountId == brokerageId);
        Assert.All(legs, l => Assert.Equal("transfer", l.PostingRole));
    }

    /// <summary>
    /// Import one Moneydance brokerage ("v") account plus
    /// <paramref name="extraAccounts"/>, and one QIF-origin XOut row from the
    /// brokerage to <paramref name="targetAcct"/>, through the real account and
    /// transaction steps.
    /// </summary>
    private static async Task ImportAsync(
        NpgsqlConnection conn, Guid ledgerId, string targetAcct, string extraAccounts)
    {
        var json = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
                {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
                {{extraAccounts}}
                {"obj_type":"txn","id":"t-xout","acctid":"a-brk","desc":"Cash out","dt":"20260101","chk":"XOut",
                 "qif_invst_action":"XOut","xfer_type":"xfrtp_bank",
                 "0.id":"s-1","0.acctid":"{{targetAcct}}","0.invest.splittype":"xfr",
                 "0.samt":"1000","0.pamt":"-1000"}
              ]
            }
            """;

        var context = new ImportContext(MdItemReader.ReadString(json), ledgerId);
        await AccountImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, "test:brokerage-cash-transfer");
        await StoredNotesSeed.ComputeImportedNotesAsync(conn, ledgerId);
    }

    private static Task<Guid> AccountIdAsync(NpgsqlConnection conn, Guid ledgerId, string mdId) =>
        conn.QuerySingleAsync<Guid>(
            "SELECT id FROM accounts WHERE ledger_id = @L AND external_id = @X;",
            new { L = ledgerId, X = mdId });

}
