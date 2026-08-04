namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>recurring_transactions</c> — a recurring-reminder SERIES
/// (ADR-0047). Migration 124 reshaped this table from the ADR-0010 denormalized
/// single-source→target row into recurrence METADATA plus a pointer to the
/// template transaction:
/// </summary>
/// <remarks>
/// <para>The transaction SHAPE (accounts, amount, splits, investment legs) no
/// longer lives here — it lives on the <b>template</b> <c>txn_header</c> +
/// <c>txn_legs</c> referenced by <see cref="TemplateHeaderId"/> (flagged
/// <c>is_recurring_template</c>). This row carries only WHEN + HOW the series
/// repeats. Firing an occurrence clones the template into a committed header
/// stamped with this row's id (ADR-0047 D5).</para>
///
/// <para><see cref="TemplateHeaderId"/>'s DB FK is composite + ledger-scoped +
/// <c>DEFERRABLE INITIALLY DEFERRED</c> (it and
/// <c>txn_headers.recurring_transaction_id</c> reference each other; the
/// deferred check resolves the cycle on snapshot restore). EF models the
/// single-column nav; the DB composite FK enforces ledger coherence — the
/// same pattern as the <c>is_merged_into</c> self-reference.</para>
/// </remarks>
internal sealed class RecurringTransactionRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    /// <summary>Idempotent re-import key (migration 013); the MD reminder's id
    /// for imported series, NULL for in-app-created ones.</summary>
    public string? ExternalId { get; init; }

    /// <summary>RFC 5545 recurrence rule (migration 124, replaces the discrete
    /// frequency columns). Expanded to occurrence dates by the C#
    /// RecurrenceExpander. NULL only on a reshaped-but-not-yet-re-imported
    /// legacy row (dormant until the importer re-materializes it).</summary>
    public string? Rrule { get; set; }

    /// <summary>Raw Moneydance reminder object, lossless (the
    /// <c>provider_raw_payload</c> pattern). JSONB. Preserves split structure /
    /// acdays / anything the structured model omits. NULL on in-app series and
    /// on pre-124 legacy rows.</summary>
    public string? SourcePayload { get; set; }

    /// <summary>MD <c>acdays</c> (migration 124). NULL = manual approve; N ≥ 0
    /// = auto-commit the occurrence N days before its due date (the firing
    /// worker is a later slice).</summary>
    public int? AutoCommitDaysBefore { get; set; }

    /// <summary>The template <c>txn_header</c> carrying this series'
    /// transaction shape. NULL on a reshaped-but-not-yet-re-imported legacy
    /// row.</summary>
    public Guid? TemplateHeaderId { get; set; }

    /// <summary>Display/query pointer to the series' originating account
    /// (migration 125) - NOT shape (the shape lives on the template legs); it
    /// lets the agenda/list compute the series AMOUNT as the net of the template
    /// legs on this account (Moneydance parity). NULL on a custom reminder with
    /// no single source and on pre-125 rows until re-import/edit sets it.</summary>
    public Guid? SourceAccountId { get; set; }

    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    /// <summary>Maintained cursor — the next un-fired occurrence date.
    /// Advanced on fire / skip.</summary>
    public DateOnly? NextDueDate { get; set; }
    public DateOnly? LastAcknowledgedDate { get; set; }

    /// <summary>Passive parity flag carried from import (ADR-0047). Set true
    /// for a managed loan-payment reminder — its split is computed from
    /// loan_terms + the loan balance at fire/display time.</summary>
    public bool IsLoanReminder { get; set; }

    /// <summary>The loan account this series is the managed payment reminder for
    /// (migration 168) — set when the loan editor sets one up, or backfilled
    /// from the inferred template-leg link. At most one series per loan account
    /// (partial unique index). NULL on every non-loan reminder.</summary>
    public Guid? LoanAccountId { get; set; }
    public bool IsActive { get; set; }
    /// <summary><c>manual</c> | <c>moneydance_import</c>.</summary>
    public string Origin { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
