namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of a row in <c>recurring_transactions</c> — a recurring-
/// reminder SERIES (ADR-0047, migration 124). The transaction SHAPE now lives
/// on the template <c>txn_header</c> + <c>txn_legs</c> referenced by
/// <see cref="TemplateHeaderId"/> (flagged <c>is_recurring_template</c>); this
/// row carries only recurrence metadata.
/// </summary>
/// <remarks>
/// <see cref="ExternalId"/> (the MD reminder id) keys the idempotent upsert.
/// <see cref="TemplateHeaderId"/> is resolved to the PERSISTED template header
/// id after that header is upserted (the importer derives a synthetic
/// <c>external_id</c> <c>"mdreminder:{ExternalId}"</c> for the template header
/// so it dedups by the same machinery as ordinary txns).
/// </remarks>
public sealed record RecurringTransactionRow(
    Guid Id,
    Guid LedgerId,
    string? ExternalId,
    string? Rrule,
    string? SourcePayload,
    int? AutoCommitDaysBefore,
    Guid? TemplateHeaderId,
    // Display/query pointer to the originating account (migration 125) - drives
    // the agenda amount. Resolved from the MD reminder's source account.
    Guid? SourceAccountId,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateOnly? NextDueDate,
    DateOnly? LastAcknowledgedDate,
    bool IsLoanReminder,
    bool IsActive,
    string Origin);
