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
        var currencyBySecurity = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in context.Export.AllItems)
        {
            if (item.ObjType != "csnap") continue;
            read++;

            var csnap = MdCsnap.From(item);

            // ADR-0114: a snapshot's rate is relative to its security's relative
            // currency (`rel_curr_id`) when MD records one, else to the file's base
            // currency, and the price is in that currency. A snapshot of a plain
            // currency (an exchange rate) is not a price and is skipped by the
            // mapper. A file whose price currency cannot be read never gets here:
            // the preview and ImportAsync refuse it (MdCurrencies.Problems). Throw
            // rather than stamp a currency the file does not name.
            if (!context.SecurityByMdId.ContainsKey(csnap.CurrId))
            {
                skipped++;
                continue;
            }
            if (!currencyBySecurity.TryGetValue(csnap.CurrId, out var priceCurrency))
            {
                priceCurrency = context.Currencies.RequirePriceCurrency(csnap.CurrId);
                currencyBySecurity[csnap.CurrId] = priceCurrency;
            }

            var result = PriceSnapshotMapper.Map(csnap, context.SecurityByMdId, priceCurrency, context.LedgerId);
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
