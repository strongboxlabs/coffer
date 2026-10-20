namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>recurring_occurrence_exceptions</c> (ADR-0047 D6 /
/// migration 125). One row suppresses a single
/// <c>(recurring_transaction_id, occurrence_date)</c> slot from the upcoming
/// expansion: the skipped slot never reappears in
/// <c>GET /reminders/upcoming</c> and
/// <c>RemindersRepository.FireAsync</c> refuses to materialize it.
/// </summary>
/// <remarks>
/// A skip writes NO header and never touches committed cash. Skip and fire are
/// mutually exclusive per slot — the repository rejects skipping an
/// already-fired occurrence and rejects firing a skipped one. The composite FK
/// <c>(recurring_transaction_id, ledger_id) -&gt; recurring_transactions(id,
/// ledger_id)</c> is <c>ON DELETE CASCADE</c> (the suppression is series-local
/// metadata, meaningless once the series is gone — contrast
/// <c>txn_headers.recurring_transaction_id</c> which is SET NULL because a
/// committed occurrence carries real cash). All columns are init-only: an
/// exception row is never mutated after creation.
/// </remarks>
internal sealed class RecurringOccurrenceExceptionRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    public Guid RecurringTransactionId { get; init; }
    public DateOnly OccurrenceDate { get; init; }
    public DateTime CreatedAt { get; init; }
    public Guid? CreatedByUserId { get; init; }
}
