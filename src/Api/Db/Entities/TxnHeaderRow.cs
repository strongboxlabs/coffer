namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_headers</c> (ADR-0022). One row per event;
/// carries the umbrella metadata (payee/memo/posted-at/check-number).
/// </summary>
/// <remarks>
/// <para>API mutations against header rows go through the
/// <c>RegisterRepository</c>'s LINQ surface. Tags attach via
/// <c>txn_header_tags</c>; overrides via <c>txn_header_overrides</c>.</para>
///
/// <para>Reconciliation status moved OFF the header to the per-leg overlay
/// <c>txn_leg_recon</c> (<see cref="TxnLegReconRow"/>; ADR-0082, migration
/// 171) — reconciliation is a per-account activity, so the header no longer
/// carries <c>status</c> / <c>cleared_at</c> / <c>cleared_by_user_id</c>.</para>
/// </remarks>
internal sealed class TxnHeaderRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string? ExternalId { get; init; }
    // Payee / Memo / CheckNumber / PostedAt / TransactedAt are
    // mutable (get; set;) on manual investment txns: ADR-0029's
    // PATCH endpoint reshapes the whole posting structure +
    // updates these header fields in place. Bank-shape PATCH
    // still goes through `txn_header_overrides` per ADR-0003 —
    // the override layer is the right pattern for FEED-IMPORTED
    // rows where the raw feed values must stay immutable. These
    // properties being `set` doesn't break that contract; it
    // just enables direct mutation on the manual path.
    public string? Payee { get; set; }
    public string? Memo { get; set; }
    public DateTime PostedAt { get; set; }
    public DateTime? TransactedAt { get; set; }
    public string? CheckNumber { get; set; }
    /// <summary>Bank-side state — TRUE while the bank itself has
    /// not cleared the transaction. Mutable (<c>get; set;</c>)
    /// because the sync service's promote-on-clear path flips it
    /// F when a previously-pending FITID re-arrives with
    /// <c>pending: false</c> (slice 2c). Manual rows write FALSE on
    /// insert; never flipped after that.</summary>
    public bool IsPending { get; set; }
    public bool IsHidden { get; set; }
    /// <summary>
    /// Mutable: slice 2c.6d's merge stamp flips this on the
    /// loser via the PATCH-with-mergeFromHeaderId path. The
    /// importer's MD-time merge writes still use direct SQL, so
    /// the value also lands here from outside the change tracker.
    /// </summary>
    public Guid? IsMergedInto { get; set; }
    public string? ImportSource { get; init; }
    /// <summary>OFX-protocol per-transaction unique id (migration
    /// 034). Populated by the MD importer (preserving MD's recorded
    /// OFX match state) and by future OFX/QFX direct importers from
    /// the wire fields. NOT written by SimpleFIN — SimpleFIN ids are
    /// proprietary strings (not OFX FITIDs) and live in
    /// <see cref="ExternalId"/> (mig 105). NULL on non-OFX rows.</summary>
    public string? OnlineMatchFitid { get; init; }
    /// <summary>OFX FI id — identifies the financial institution
    /// under the OFX protocol. Composite with <see cref="OnlineMatchFitid"/>
    /// for global uniqueness across multiple connected banks. NOT
    /// written by SimpleFIN (mig 105) — SimpleFIN's org_id is not
    /// an OFX FI_ID and is recoverable via the feed_connections row.</summary>
    public string? OnlineMatchFiId { get; init; }
    /// <summary>TRUE on rows the SimpleFIN sync service freshly
    /// inserted from a bank-posted feed item (slice 2c, migration
    /// 037). Register renders these with a visual flag until the
    /// user clicks Approve, which clears the bit. Mutable
    /// (<c>get; set;</c>) — the Approve endpoint writes the flag
    /// in place, same pattern as the other mutable in-place fields.</summary>
    public bool NeedsReview { get; set; }
    /// <summary>Investment-action label for the event (Buy / Sell /
    /// Div / DivReinvest / Interest / Transfer / MiscInc / MiscExp /
    /// Split). NULL on non-investment events. Migration 047 moved
    /// this from the per-leg column to the header — action is a
    /// property of the WHOLE event, not of an individual posting;
    /// multi-posting investment events (Buy+Fee, DivReinvest,
    /// buyx/sellx/divx) share one primary action across their
    /// postings.</summary>
    public string? Action { get; set; }
    /// <summary>ADR-0031 Phase 3c: provider-classifier output (action
    /// catalog per ADR-0027). Set by the orchestrator's brokerage
    /// branch when sync detects an investment-shape transaction
    /// description; null otherwise. The editor uses this to pre-fill
    /// the action picker on review; the user then upgrades the row
    /// to a real investment-shape header via /investment-transactions,
    /// which clears needs_review. Mutable so the upgrade path can
    /// null it out once <see cref="Action"/> takes over.</summary>
    public string? IngestActionHint { get; set; }
    /// <summary>Migration 113: provider-extracted share count for
    /// investment-shape OFX rows (OFX <c>UNITS</c>). Populated by
    /// <c>OfxFileProvider.MapInvestmentTransaction</c>; null on
    /// bank/credit rows and on SimpleFIN brokerage rows (which don't
    /// carry shares natively). Read by <c>hintToDraft</c> to pre-fill
    /// the editor's shares input.</summary>
    public decimal? IngestShares { get; set; }
    /// <summary>Migration 113: provider-extracted per-share price for
    /// investment OFX rows (OFX <c>UNITPRICE</c>). Same population
    /// rules as <see cref="IngestShares"/>.</summary>
    public decimal? IngestUnitPrice { get; set; }
    /// <summary>Migration 113: aggregated per-row fee — sum of
    /// Commission + Fees + Load + Markup + Markdown depending on
    /// which OFX investment subtype carried them. NULL when the wire
    /// had no fee-shaped fields OR all summed to zero. Single
    /// aggregated value matches ADR-0029's editor model (no per-kind
    /// breakdown).</summary>
    public decimal? IngestFee { get; set; }
    /// <summary>Migration 114: provider-extracted security identifier
    /// string (OFX: SECLIST-resolved ticker or raw CUSIP fallback).
    /// Persisted at ingest time so the editor's Accept flow can
    /// record a provider_security_mapping with the SAME identifier
    /// the next ingest will look up. NULL on bank/credit rows,
    /// SimpleFIN rows (which re-derive from the payee classifier),
    /// and manual entries.</summary>
    public string? IngestSecurityTickerHint { get; set; }
    /// <summary>ADR-0031 follow-up (migration 078): original provider
    /// JSON for this transaction (SimpleFinTransaction shape today;
    /// OFX / CSV shapes later). Captured verbatim by the orchestrator
    /// on insert and backfilled on the alreadyKnown dedup branch when
    /// previously null. Diagnostic / classifier-iteration use only —
    /// the orchestrator does not parse or validate it. Stored as
    /// JSONB; EF maps via HasColumnType("jsonb"). Mutable so the
    /// backfill path can update in place.</summary>
    public string? ProviderRawPayload { get; set; }
    /// <summary>Specific ingest provider that wrote this row (mig 107).
    /// Values: <c>simplefin</c>, <c>mdplus</c>, <c>ofx</c>,
    /// <c>qif</c>, <c>csv</c>. NULL when <see cref="Origin"/> is
    /// <c>manual</c>. Audit detail — distinct from <see cref="Origin"/>
    /// which is the icon-level mechanism (manual / online_import /
    /// file_import). The DB CHECK
    /// <c>ck_txn_headers_provider_key_iff_not_manual</c> enforces
    /// the bi-implication.</summary>
    public string? ProviderKey { get; set; }
    /// <summary>TRUE when at least one other row has
    /// <see cref="IsMergedInto"/> pointing at this row (mig 107).
    /// Maintained atomically with the merge mutation in
    /// <c>TransactionsRepository.PatchAsync</c>. Drives the
    /// register's merge-winner overlay. Monotonic — there is no
    /// unmerge surface today; once TRUE, stays TRUE.</summary>
    public bool IsMergeWinner { get; set; }
    public DateTime CreatedAt { get; init; }
    /// <summary>
    /// ADR-0034 v2 (migration 095): strictly-monotonic insertion-order
    /// tiebreaker. Populated by the <c>txn_headers_seq</c> sequence on
    /// INSERT; immutable thereafter (enforced by
    /// <c>trg_reject_txn_headers_seq_update</c>). The canonical sort
    /// pair for every transaction-time running-window calculation is
    /// <c>(posted_at, seq)</c> — UUID tiebreakers are gone.
    /// </summary>
    public long Seq { get; init; }

    /// <summary>
    /// ADR-0047 / migration 124: TRUE marks this header (and its legs) a
    /// recurring-reminder TEMPLATE — never a live cash event. Excluded from
    /// every live read surface via the <c>live_txn_headers</c> view and never
    /// enters the balance / holdings walk. Set once at insert (a template
    /// never flips to live; a fired occurrence is a NEW live header).
    /// </summary>
    public bool IsRecurringTemplate { get; init; }

    /// <summary>
    /// ADR-0047 / migration 124: on a FIRED occurrence (a committed header
    /// materialized from a reminder), points back to the series. NULL on
    /// ordinary rows AND on the template itself. Mutable because firing
    /// stamps it; composite FK <c>(recurring_transaction_id, ledger_id)</c>
    /// is DB-enforced (ON DELETE SET NULL — committed cash survives a series
    /// delete).
    /// </summary>
    public Guid? RecurringTransactionId { get; set; }

    /// <summary>
    /// ADR-0047 / migration 124: the series slot a fired occurrence fills
    /// (the occurrence's scheduled date), so a materialized instance is
    /// traceable to its (series, date). NULL off the reminder path.
    /// </summary>
    public DateOnly? OccurrenceDate { get; set; }
}
