using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// First step of the import pipeline: walk every <c>curr</c> row, translate
/// the security-typed ones into <see cref="SecurityRow"/>s, upsert them,
/// and populate <see cref="ImportContext.SecurityByMdId"/> with the
/// resulting MD-id → Coffer-id mapping. Plain currency entries are counted
/// (skipped) and contribute nothing to the database.
/// </summary>
public sealed class SecurityImportStep
{
    private readonly SecuritiesRepository _repository;

    public SecurityImportStep(SecuritiesRepository repository)
    {
        _repository = repository;
    }

    public async Task<ImportStepResult> ExecuteAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var read = 0;
        var written = 0;
        var skipped = 0;

        foreach (var item in context.Export.AllItems)
        {
            if (item.ObjType != "curr") continue;
            read++;

            var curr = MdCurr.From(item);
            if (!curr.IsSecurity)
            {
                skipped++;
                continue;
            }

            var row = SecurityMapper.Map(curr, context.LedgerId);
            if (row is null)
            {
                skipped++;
                continue;
            }

            var persistedId = await _repository.UpsertByExternalIdAsync(row, cancellationToken)
                                               .ConfigureAwait(false);
            context.SecurityByMdId[curr.Id] = new SecurityRef(persistedId, row.ShareDecimals);
            written++;
        }

        return new ImportStepResult(StepName: "securities", Read: read, Written: written, Skipped: skipped);
    }

    /// <summary>
    /// Convenience: build the repository and run the step in one call.
    /// </summary>
    public static async Task<ImportStepResult> RunAsync(
        NpgsqlConnection connection,
        ImportContext context,
        CancellationToken cancellationToken = default)
    {
        var step = new SecurityImportStep(new SecuritiesRepository(connection));
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
