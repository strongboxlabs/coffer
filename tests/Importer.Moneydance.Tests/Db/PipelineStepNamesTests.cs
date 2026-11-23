using Dapper;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// The step names an import reports are <see cref="MoneydanceImportService.PipelineStepNames"/>,
/// in order. A reader that shows the step in progress takes the next name from that list,
/// because progress is reported only once a step completes, so a step added, removed or
/// moved without the list would label the wrong step.
/// </summary>
[Collection(DbCollection.Name)]
public sealed class PipelineStepNamesTests : DatabaseTest
{
    private const string Export = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},
            {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"c-shop","type":"e","name":"Shopping","parentid":"a-root","currid":"c-usd"},
            {"obj_type":"txn","id":"t-1","acctid":"a-bank","desc":"Shop","dt":"20260101",
             "0.id":"s-1","0.acctid":"c-shop","0.samt":"1000","0.pamt":"-1000"}
          ]
        }
        """;

    public PipelineStepNamesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public void The_list_has_one_name_per_counted_step()
    {
        Assert.Equal(MoneydanceImportService.PipelineStepCount, MoneydanceImportService.PipelineStepNames.Count);
        Assert.Equal(
            MoneydanceImportService.PipelineStepNames.Count,
            MoneydanceImportService.PipelineStepNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task An_import_reports_the_listed_steps_in_order_and_counts_against_the_step_count()
    {
        await using var conn = Db.OpenConnection();
        var owner = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO users (display_name) VALUES (@n) RETURNING id;",
            new { n = $"importer-{Guid.NewGuid():N}" });

        var reported = new List<ImportProgressUpdate>();
        using var export = MdItemReader.ReadString(Export);
        var result = await new MoneydanceImportService().ImportAsync(
            conn, export, ledgerName: $"Steps {Guid.NewGuid():N}", ownerUserId: owner,
            progress: new Recorder(reported), cancellationToken: default,
            beforeCommit: Coffer.Api.Db.LegNotes.RecomputeLedgerOnAsync);

        Assert.Equal(MoneydanceImportService.PipelineStepNames, reported.Select(u => u.Detail).ToList());
        Assert.Equal(
            Enumerable.Range(1, MoneydanceImportService.PipelineStepCount).ToList(),
            reported.Select(u => u.Completed).ToList());
        Assert.All(reported, u => Assert.Equal(MoneydanceImportService.PipelineStepCount, u.Total));
        Assert.Equal(MoneydanceImportService.PipelineStepNames, result.Steps.Select(s => s.StepName).ToList());
    }

    /// <summary>Records each update on the reporting thread, in order.</summary>
    private sealed class Recorder(List<ImportProgressUpdate> into) : IProgress<ImportProgressUpdate>
    {
        public void Report(ImportProgressUpdate value) => into.Add(value);
    }
}
