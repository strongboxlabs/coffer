using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Walks every <c>csplit</c> item in the export, maps it to a
/// <see cref="SecuritySplitRow"/>, and bulk-upserts into <c>security_splits</c>.
/// Stock-split events are security metadata in Moneydance (and in Coffer post
/// migration 060) — they don't appear in the txn stream. Splits whose
/// <c>curr</c> doesn't resolve to a known security are counted as skipped.
/// </summary>
/// <remarks>
/// Runs after <see cref="SecurityImportStep"/> (depends on
/// <see cref="ImportContext.SecurityByMdId"/>) and before
/// <see cref="InvestmentTransactionImportStep"/>'s Pass 5 recompute, so when
/// the recompute function walks the unified (legs ∪ splits) event stream for
/// each holding the new split rows are already visible.
/// </remarks>
public sealed class SecuritySplitImportStep
{
    private readonly SecuritySplitsRepository _repository;

    public SecuritySplitImportStep(SecuritySplitsRepository repository)
    {
        _repository = repository;
    }

    public async Task<ImportStepResult> ExecuteAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var read = 0;
        var skipped = 0;
        var rows = new List<SecuritySplitRow>();

        foreach (var item in context.Export.AllItems)
        {
            if (item.ObjType != "csplit") continue;
            read++;

            var csplit = MdCsplit.From(item);
            var result = SecuritySplitMapper.Map(csplit, context.SecurityByMdId, context.LedgerId);
            if (result.Row is null)
            {
                skipped++;
                continue;
            }
            rows.Add(result.Row);
        }

        if (rows.Count == 0)
            return new ImportStepResult(StepName: "security_splits", Read: read, Written: 0, Skipped: skipped);

        await _repository.BulkUpsertAsync(rows, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ImportStepResult(
            StepName: "security_splits",
            Read: read,
            Written: rows.Count,
            Skipped: skipped);
    }

    public static async Task<ImportStepResult> RunAsync(
        NpgsqlConnection connection,
        ImportContext context,
        CancellationToken cancellationToken = default)
    {
        var step = new SecuritySplitImportStep(new SecuritySplitsRepository(connection));
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
