using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Mappers;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Reminder import (ADR-0047 / migration 124). Each MD <c>reminder</c> becomes
/// a recurring-reminder SERIES: a <b>template</b> <c>txn_header</c> + legs
/// (flagged <c>is_recurring_template</c>) plus a slim
/// <c>recurring_transactions</c> row pointing at it. The template header+legs
/// are persisted via the same <see cref="TransactionsRepository.BulkUpsertAsync"/>
/// machinery as live txns, keyed by the synthetic <c>"mdreminder:{id}"</c>
/// external id; the recurring row is then inserted with
/// <c>template_header_id</c> set to the PERSISTED header id. Seed-once
/// (ADR-0052 D2): the importer only ever seeds an EMPTY ledger, so these are
/// plain inserts.
/// </summary>
public sealed class ReminderImportStep
{
    private readonly TransactionsRepository _transactionsRepo;
    private readonly RecurringTransactionsRepository _repository;
    private readonly string _importSource;

    public ReminderImportStep(
        TransactionsRepository transactionsRepo,
        RecurringTransactionsRepository repository,
        string importSource)
    {
        _transactionsRepo = transactionsRepo;
        _repository = repository;
        _importSource = importSource;
    }

    public async Task<ImportStepResult> ExecuteAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var read = 0;
        var skipped = 0;

        // Pass 1: map every reminder to a template header + legs + slim row.
        var headers = new List<TxnHeaderRow>();
        var legs = new List<TxnLegRow>();
        var rows = new List<RecurringTransactionRow>();

        foreach (var item in context.Export.AllItems)
        {
            if (item.ObjType != "reminder") continue;
            read++;

            var reminder = MdReminder.From(item);
            var result = ReminderMapper.Map(
                reminder, context.AccountByMdId, context.LedgerId, _importSource, item.RawJson);
            if (result.Header is null || result.Row is null)
            {
                skipped++;
                continue;
            }

            headers.Add(result.Header);
            legs.AddRange(result.Legs);
            rows.Add(result.Row);
        }

        if (headers.Count == 0)
            return new ImportStepResult(StepName: "recurring_transactions", Read: read, Written: 0, Skipped: skipped);

        // Pass 2: persist the template headers + legs, then the recurring
        // rows pointing at the persisted template header ids.
        var upsert = await _transactionsRepo
            .BulkUpsertAsync(headers, legs, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var written = 0;
        foreach (var row in rows)
        {
            var syntheticExternalId = "mdreminder:" + row.ExternalId;
            var templateHeaderId = upsert.Headers[(row.LedgerId, syntheticExternalId)];
            await _repository
                .UpsertByExternalIdAsync(row with { TemplateHeaderId = templateHeaderId }, cancellationToken)
                .ConfigureAwait(false);
            written++;
        }

        return new ImportStepResult(StepName: "recurring_transactions", Read: read, Written: written, Skipped: skipped);
    }

    public static async Task<ImportStepResult> RunAsync(
        NpgsqlConnection connection,
        ImportContext context,
        string importSource,
        CancellationToken cancellationToken = default)
    {
        var step = new ReminderImportStep(
            new TransactionsRepository(connection),
            new RecurringTransactionsRepository(connection),
            importSource);
        return await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
