using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
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
/// <remarks>
/// A security's type, subtype and exchange come from the type-<c>s</c> accounts
/// that hold it, where Moneydance writes them, not from its <c>curr</c> item
/// (ADR-0016, amendment of 2026-11-18). A security held in several accounts takes
/// all three from one of them: the first, in item order, with a type. A code
/// <see cref="MdSecurityType"/> does not know is imported as vehicle <c>other</c>
/// and listed on <see cref="ImportContext.UnrecognisedSecurityTypes"/>, which the
/// <c>security-types-recognised</c> check reports.
/// </remarks>
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

        var fieldsByCurrId = SecurityFieldsByCurrId(context.Export);

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

            var fields = fieldsByCurrId.GetValueOrDefault(curr.Id, MdSecurityFields.None);
            var row = SecurityMapper.Map(curr, fields, context.LedgerId);
            if (row is null)
            {
                skipped++;
                continue;
            }

            if (!MdSecurityType.Translate(fields.SecType, fields.SecSubtype).Recognised)
                context.UnrecognisedSecurityTypes.Add(new UnrecognisedSecurityType(curr.Id, fields.SecType!));

            var persistedId = await _repository.UpsertByExternalIdAsync(row, cancellationToken)
                                               .ConfigureAwait(false);
            context.SecurityByMdId[curr.Id] = new SecurityRef(persistedId, row.ShareDecimals);
            written++;
        }

        return new ImportStepResult(StepName: "securities", Read: read, Written: written, Skipped: skipped);
    }

    /// <summary>
    /// The security-type parameters per held security, keyed by the <c>currid</c> of
    /// the type-<c>s</c> accounts (a <c>curr</c> item's id). All three values come
    /// from one account: the first, in item order, with a type; failing that, the
    /// first with any value.
    /// </summary>
    internal static Dictionary<string, MdSecurityFields> SecurityFieldsByCurrId(MdExport export)
    {
        var map = new Dictionary<string, MdSecurityFields>(StringComparer.Ordinal);
        foreach (var item in export.AllItems)
        {
            if (item.ObjType != "acct" || item.GetString("type") != "s") continue;
            var acct = MdAcct.From(item);
            if (acct.CurrId is null || acct.Security is not { } found) continue;

            // The three values travel together, from one account: a type from one
            // account beside another's subtype could pair a code with a subtype no
            // account gave it. The first account with a type wins; until one is
            // seen, the first account with any value stands in.
            if (!map.TryGetValue(acct.CurrId, out var held)
                || (held.SecType is null && found.SecType is not null))
                map[acct.CurrId] = found;
        }
        return map;
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
