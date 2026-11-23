namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_headers</c> (ADR-0022). One row per event;
/// carries the shared metadata (payee/posted-at/check-number), and the memo
/// and tags only of a multi-posting transaction: a one-posting transaction's
/// are on its legs.
/// </summary>
/// <remarks>
/// <para>API mutations against header rows go through the
/// <c>RegisterRepository</c>'s LINQ surface. Transaction-level tags attach via
/// <c>txn_header_tags</c>. Since migration 230 this row always holds the
/// CURRENT values; the feed's are captured once, on the first edit, to
/// <c>txn_header_originals</c> (<see cref="TxnHeaderOriginalRow"/>).</para>
///
/// <para>Reconciliation status moved OFF the header to the per-leg overlay
/// <c>txn_leg_recon</c> (<see cref="TxnLegReconRow"/>; ADR-0082, migration
/// 171) — reconciliation is a per-account activity, so the header no longer
/// carries <c>status</c> / <c>cleared_at</c> / <c>cleared_by_user_id</c>.</para>
///
/// <para>No public constructor: a header is built only by the factories in
/// <c>TxnHeaderRow.Factories.cs</c>, each of which takes the originating account
/// (migration 263), so a header written without one cannot be expressed. EF
/// materialises through the private parameterless constructor.</para>
/// </remarks>
internal sealed partial class TxnHeaderRow
{
    private TxnHeaderRow() { }

    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string? ExternalId { get; init; }
    // Payee / CheckNumber / PostedAt / TransactedAt are mutable (get; set;) and
    // the write paths assign them directly; the memo is the write primitive's. ADR-0003's
    // guarantee that a feed-imported row's raw values stay recoverable lives in
    // the sidecar (migration 230): HeaderOriginals.CaptureAsync snapshots the
    // feed's values to txn_header_originals before the first edit overwrites
    // them — while reads pay for no COALESCE and a cleared field is expressible.
    public string? Payee { get; set; }

    /// <summary>
    /// The transaction's memo: set only on a multi-posting transaction (a split, an
    /// event of several postings); a one-posting transaction keeps its memos on its
    /// legs. Written only by the write primitive's adapter, through
    /// <see cref="AssignMemo"/>.
    /// </summary>
    public string? Memo { get; private set; }

    /// <summary>Store a planned header memo (see <see cref="Memo"/>).</summary>
    internal void AssignMemo(string? memo) => Memo = memo;

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
    /// <summary>The event's investment action: a stored value from the
    /// ADR-0027 catalog (<c>buy</c> / <c>sell</c> / <c>dividend_cash</c> /
    /// <c>dividend_reinvest</c> / … and ADR-0065's <c>transfer_shares</c>),
    /// never the word the SPA shows for it. NULL on non-investment events.
    /// Migration 047 moved this from the per-leg column to the header —
    /// action is a property of the WHOLE event, not of an individual
    /// posting; multi-posting investment events (Buy+Fee, Reinvest,
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

    /// <summary>The authoritative total the source file stated (mig 228), so
    /// the editor never rebuilds it from shares x price. Null when the provider
    /// stated none, or for rows imported before that migration.</summary>
    public decimal? IngestAmount { get; set; }

    /// <summary>
    /// Migration 301 (ADR-0114 D15): the bank's cleared figure, exactly as sent, that put
    /// the row back in review because the row does not hold it: a merged loser's when
    /// <see cref="ReviewClearedByMatch"/>, else the row's own, on a split whose lines add up
    /// to another total. Null when the last promote-on-clear to hold the row did so for a
    /// rounding, and on a row none held. Read only while <see cref="NeedsReview"/>.
    /// </summary>
    /// <remarks>Written only with the flag it explains, through
    /// <see cref="HoldForClearedFigure"/> and <see cref="HoldForRounding"/>, so a reason
    /// left from an earlier hold is never read as the current one.</remarks>
    public decimal? ReviewClearedAmount { get; private set; }

    /// <summary>Migration 301 (ADR-0114 D15): true when <see cref="ReviewClearedAmount"/>
    /// is a merged loser's cleared figure, so this row is the winner it was folded into.</summary>
    public bool ReviewClearedByMatch { get; private set; }

    /// <summary>Put the row in review because the bank cleared it, or a row merged into it
    /// (<paramref name="byMatch"/>), at <paramref name="bankFigure"/>, which the row does
    /// not hold (ADR-0114 D15).</summary>
    internal void HoldForClearedFigure(decimal bankFigure, bool byMatch)
    {
        NeedsReview = true;
        ReviewClearedAmount = bankFigure;
        ReviewClearedByMatch = byMatch;
    }

    /// <summary>Put the row in review because its cleared figure was rounded to its
    /// currency's places (ADR-0114 D15); the rounded note says why, so no cleared figure
    /// is kept.</summary>
    internal void HoldForRounding()
    {
        NeedsReview = true;
        ReviewClearedAmount = null;
        ReviewClearedByMatch = false;
    }
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

    /// <summary>
    /// The <c>ledger_operations</c> run (family <c>ingest</c>) whose import created this
    /// row; NULL for a manual entry, a pre-221 row, or an import whose operation audit
    /// retention has since pruned (mig 221).
    /// </summary>
    /// <remarks>
    /// Exists so an import can be UNDONE as a set. File imports have no dedup by
    /// decision — CSV rows carry no issuer id, and any content-derived identity either
    /// collapses two genuinely identical transactions or duplicates everything when a
    /// download window shifts. Recording which run wrote a row answers "I uploaded that
    /// twice" exactly, where guessing could not.
    /// </remarks>
    public Guid? LedgerOperationId { get; set; }
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

    /// <summary>
    /// The account the transaction was entered from (migration 263):
    /// the entering side of a one-split, the split side of a multi-split, the
    /// brokerage of an investment event (the source brokerage of an in-kind transfer).
    /// </summary>
    /// <remarks>
    /// Set by the factory that built the header; changed only by
    /// <see cref="AssignOriginatingAccount"/>, when a reshape hands ownership to the
    /// side that does it (a split from the other side, turning a row into an event,
    /// moving an event to another brokerage) or a repoint leaves no leg on it.
    /// Nullable because a header with no legs names no account; every API writer
    /// builds one with legs, so it is never NULL on a header the API writes.
    /// </remarks>
    public Guid? OriginatingAccountId { get; private set; }

    /// <summary>Hand the header to another account (see <see cref="OriginatingAccountId"/>).</summary>
    internal void AssignOriginatingAccount(Guid accountId) => OriginatingAccountId = accountId;

    /// <summary>Whether a delete of this transaction removes it outright (see
    /// <see cref="IsDeletedOutright"/>), else it is hidden.</summary>
    internal bool DeletesOutright() =>
        IsDeletedOutright(ExternalId, RecurringTransactionId, OccurrenceDate, IsRecurringTemplate);

    /// <summary>
    /// Whether the single delete of a transaction (bank or investment) removes it outright,
    /// which un-merges every row merged into it (<c>is_merged_into</c> ON DELETE SET NULL):
    /// a row with no external id that is no reminder occurrence. A feed or import row is
    /// hidden, so a re-sync does not bring it back, and so is a reminder occurrence, whose
    /// slot stamp keeps the slot consumed (migration 218).
    /// </summary>
    internal static bool IsDeletedOutright(
        string? externalId, Guid? recurringTransactionId, DateOnly? occurrenceDate, bool isRecurringTemplate) =>
        externalId is null
        && !(recurringTransactionId is not null && occurrenceDate is not null && !isRecurringTemplate);
}
