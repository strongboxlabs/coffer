using Dapper;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// The bank-route transaction step lists every transaction it does not import, with
/// its reason, and the import's no-dropped-transactions check fails on them. A count
/// without the list is a drop the wizard cannot show, so each skip has to be named.
/// </summary>
/// <remarks>
/// Run through the whole import, because the check is what the person importing
/// sees: a skip the step lists but the check ignores, or the reverse, is the defect.
/// Each of the step's two exits is driven: one with headers left to write, one with
/// every row skipped.
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class TransactionImportStepSkipsTests : DatabaseTest
{
    public TransactionImportStepSkipsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>A clean one-split, one naming a split account the export does not hold,
    /// one with no splits, and two sharing a FITID, of which the second is dropped.</summary>
    private const string SkippingExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},
            {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"c-shop","type":"e","name":"Shopping","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"txn","id":"t-clean","acctid":"a-bank","desc":"Shop","dt":"20260101",
             "0.id":"s-1","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-unknown-split","acctid":"a-bank","desc":"Shop","dt":"20260102",
             "0.id":"s-2","0.acctid":"c-missing","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-no-splits","acctid":"a-bank","desc":"Shop","dt":"20260103"},
            {"obj_type":"txn","id":"t-fitid-first","acctid":"a-bank","desc":"Shop","dt":"20260104",
             "ol_fi_id":"fi-1","ol_fitid_1":"fitid-1",
             "0.id":"s-3","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-fitid-second","acctid":"a-bank","desc":"Shop","dt":"20260105",
             "ol_fi_id":"fi-1","ol_fitid_1":"fitid-1",
             "0.id":"s-4","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"}
          ]
        }
        """;

    /// <summary>Only rows the step cannot map, so it leaves before writing anything.</summary>
    private const string NothingMapsExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},
            {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"txn","id":"t-unknown-split","acctid":"a-bank","desc":"Shop","dt":"20260102",
             "0.id":"s-1","0.acctid":"c-missing","0.samt":"1000","0.pamt":"-1000"},
            {"obj_type":"txn","id":"t-no-splits","acctid":"a-bank","desc":"Shop","dt":"20260103"}
          ]
        }
        """;

    [Fact]
    public async Task Each_skip_is_listed_with_its_reason_and_fails_the_check()
    {
        await using var conn = Db.OpenConnection();
        var result = await ImportAsync(conn, SkippingExport);

        var step = result.Steps.Single(s => s.StepName == "transactions");
        Assert.Equal(
            new (string, string, int?)[]
            {
                ("t-unknown-split", "UnknownSplitAccount", 20260102),
                ("t-no-splits", "NoSplits", 20260103),
                ("t-fitid-second", TransactionImportStep.DuplicateFitidReason, 20260105),
            },
            (step.Skips ?? []).Select(s => (s.TxnId, s.Reason, s.Date)).ToArray());
        Assert.Equal(3, step.Skipped);

        var check = result.Validation.Checks.Single(c => c.Name == "no-dropped-transactions");
        Assert.False(check.Passed);
        Assert.Contains("3 transaction(s) dropped", check.Message);
        Assert.Equal("3 transactions were not imported.", check.Summary);

        // The two kept headers were written: the skips are not the whole import.
        Assert.Equal(
            new[] { "t-clean", "t-fitid-first" },
            (await conn.QueryAsync<string>(
                "SELECT external_id FROM txn_headers WHERE ledger_id = @L ORDER BY external_id;",
                new { L = result.LedgerId })).ToArray());
    }

    [Fact]
    public async Task A_step_that_maps_nothing_still_lists_what_it_skipped()
    {
        await using var conn = Db.OpenConnection();
        var result = await ImportAsync(conn, NothingMapsExport);

        var step = result.Steps.Single(s => s.StepName == "transactions");
        Assert.Equal(0, step.Written);
        Assert.Equal(
            new[] { ("t-unknown-split", "UnknownSplitAccount"), ("t-no-splits", "NoSplits") },
            (step.Skips ?? []).Select(s => (s.TxnId, s.Reason)).ToArray());

        var check = result.Validation.Checks.Single(c => c.Name == "no-dropped-transactions");
        Assert.False(check.Passed);
        Assert.Equal("2 transactions were not imported.", check.Summary);
    }

    private static async Task<MoneydanceImportResult> ImportAsync(NpgsqlConnection conn, string json)
    {
        var owner = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO users (display_name) VALUES (@n) RETURNING id;",
            new { n = $"importer-{Guid.NewGuid():N}" });
        using var export = MdItemReader.ReadString(json);
        return await new MoneydanceImportService().ImportAsync(
            conn, export, ledgerName: $"Skips {Guid.NewGuid():N}", ownerUserId: owner,
            progress: null, cancellationToken: default,
            beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync);
    }
}
