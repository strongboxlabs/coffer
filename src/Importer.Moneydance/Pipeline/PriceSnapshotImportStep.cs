using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Fifth step of the import pipeline: walk every <c>csnap</c> item, map it
/// to a <see cref="SecurityPriceRow"/>, and bulk-upsert into
/// <c>security_prices</c>. Snapshots whose <c>currid</c> doesn't resolve to
/// a known security (plain currency exchange-rate samples) are counted as
/// skipped.
/// </summary>
public sealed class PriceSnapshotImportStep
{
    private readonly SecurityPricesRepository _repository;

    public PriceSnapshotImportStep(SecurityPricesRepository repository)
    {
        _repository = repository;
    }

    public async Task<ImportStepResult> ExecuteAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var read = 0;
        var skipped = 0;
        var rows = new List<SecurityPriceRow>();

        foreach (var item in context.Export.AllItems)
        {
            if (item.ObjType != "csnap") continue;
            read++;

            var csnap = MdCsnap.From(item);
            var result = PriceSnapshotMapper.Map(csnap, context.SecurityByMdId, context.LedgerId);
            if (result.Row is null)
            {
                skipped++;
                continue;
            }
            rows.Add(result.Row);
        }

        if (rows.Count == 0)
            return new ImportStepResult(StepName: "security_prices", Read: read, Written: 0, Skipped: skipped);

        await _repository.BulkUpsertAsync(rows, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ImportStepResult(
            StepName: "security_prices",
            Read: read,
            Written: rows.Count,
            Skipped: skipped);
    }

    public static async Task<ImportStepResult> RunAsync(
        NpgsqlConnection connection,
        ImportContext context,
        CancellationToken cancellationToken = default)
    {
        var step = new PriceSnapshotImportStep(new SecurityPricesRepository(connection));
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
