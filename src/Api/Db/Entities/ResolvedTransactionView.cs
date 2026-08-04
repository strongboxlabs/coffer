namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF Core query type backing the <c>resolved_transactions</c> view from
/// migration 005 (per <c>0003-immutable-feed-and-overrides</c> ADR). The
/// view layers the transaction_overrides COALESCE logic on top of the
/// raw <c>transactions</c> table so register reads see one consistent
/// shape — application code never touches the raw table directly.
/// </summary>
/// <remarks>
/// Internal: the public DTO that crosses the wire is
/// <c>ResolvedTransactionDto</c> in the Transactions feature folder.
/// Repositories project from this type to the DTO at the boundary
/// (engineering-standards §4.2.2: "Project from the view type to a
/// public DTO at the repository boundary so the API surface stays
/// separable from the EF model"). Keyless via
/// <c>HasNoKey().ToView(...)</c> in <see cref="AppDbContext"/>; EF won't
/// try to write through it.
/// </remarks>
internal sealed class ResolvedTransactionView
{
    public Guid Id { get; init; }
    public Guid AccountId { get; init; }
    public string? Payee { get; init; }
    public string? Memo { get; init; }
    public decimal Amount { get; init; }
    public DateTime PostedAt { get; init; }
    public DateTime? TransactedAt { get; init; }

    /// <summary>
    /// Normalized 3-state reconciliation vocabulary
    /// <c>(uncleared, reconciling, cleared)</c> — migration 030.
    /// Non-nullable on the view (CHECK constraint on the underlying
    /// table guarantees a value).
    /// </summary>
    public string Status { get; init; } = "uncleared";

    public bool IsHidden { get; init; }
    public bool HasOverrides { get; init; }
    public decimal? BalanceAfter { get; init; }
    public string Origin { get; init; } = string.Empty;
    public bool IsPending { get; init; }
    public Guid? IsMergedInto { get; init; }
    public string? InvestmentAction { get; init; }
    public string? ExternalId { get; init; }
    public DateTime CreatedAt { get; init; }

    // Register-parity columns added in migration 018.
    public string? CheckNumber { get; init; }
    public Guid CounterpartyId { get; init; }
    public Guid? TxnGroupId { get; init; }
    public int LegIndex { get; init; }
    public Guid? CounterpartyAccountId { get; init; }
    public string? CounterpartyAccountName { get; init; }
    public string? CounterpartyAccountType { get; init; }

    // Postgres text[] maps to string[] in Npgsql. Default to empty
    // array so callers don't deal with null; the view's COALESCE
    // ensures the DB never sends null for this column.
    public string[] Tags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Always-present header identity (migration 028). Distinct from
    /// <see cref="TxnGroupId"/>: the latter is the legacy
    /// is-this-a-multi-split discriminator (NULL for singles), while
    /// <c>HeaderId</c> is unconditionally the owning header so the
    /// SPA's inline-edit POSTs can address it.
    /// </summary>
    public Guid HeaderId { get; init; }

    /// <summary>
    /// Timestamp the user (or a future bulk-reconcile action) moved
    /// the header into <c>status='cleared'</c>. NULL otherwise.
    /// Paired with <see cref="ClearedByUserId"/>; the DB CHECK
    /// constraint enforces <c>(status='cleared') ⇔ (cleared_at IS NOT NULL)</c>.
    /// Added in migration 030.
    /// </summary>
    public DateTime? ClearedAt { get; init; }

    /// <summary>
    /// User who marked this header as cleared. NULL when uncleared
    /// or reconciling, or when the original clearing user has been
    /// removed (FK ON DELETE SET NULL).
    /// </summary>
    public Guid? ClearedByUserId { get; init; }

    /// <summary>
    /// Migration 032 (ADR-0025): raw leg-level memo, no header
    /// fallback. <see cref="Memo"/> above remains the full 4-way
    /// COALESCE (still convenient for single-row register display);
    /// <c>LegMemo</c> is just <c>COALESCE(lo.leg_memo, l.leg_memo)</c>
    /// — NULL when the leg has no memo of its own. The SPA's editor
    /// loads this into the per-posting memo field; the register's
    /// split-leg row displays it directly so header memo doesn't
    /// bleed onto leg rows.
    /// </summary>
    public string? LegMemo { get; init; }

    /// <summary>
    /// Migration 032 (ADR-0025): raw header-level memo, no leg
    /// fallback. <c>COALESCE(o.memo, h.memo)</c>. The SPA's editor
    /// loads this into the umbrella "Memo" field; the register's
    /// split-parent row displays it directly.
    /// </summary>
    public string? HeaderMemo { get; init; }

    // Migration 034: OFX online-match identity (fitid + fi_id —
    // the composite OFX dedup key). The audit-only status / type /
    // orig_id columns were dropped in mig 109 (ADR-0035 §4); their
    // data lives inside provider_raw_payload on rows that need it.
    public string? OnlineMatchFitid { get; init; }
    public string? OnlineMatchFiId { get; init; }

    /// <summary>
    /// Migration 037 (slice 2c): TRUE on bank-feed rows the user
    /// hasn't approved yet. Header-projected directly; the register
    /// renders flagged rows with a visual treatment until the user
    /// clicks Approve. FALSE on manual entries, MD-imported rows,
    /// and rows the user has already approved.
    /// </summary>
    public bool NeedsReview { get; init; }

    // Migration 045 (slice A1.c): investment-leg metadata pulled
    // from `txn_legs` (preferring whichever side of the posting
    // carries non-null values — typically the holdings-side leg).
    // All NULL on rows whose posting doesn't touch a security
    // (the bulk of the view: every bank, credit card, and category
    // leg). Joined to `securities` so the ticker + name come
    // through in one query.
    //
    // Commission is intentionally NOT projected. ADR-0019 Rule 5
    // makes the fee LEG (separate paired txn_headers row) the
    // source of truth; the `txn_legs.commission` column is dead
    // (0 of 130K rows populate it). The fee row renders naturally
    // in the same `txn_group_id` group.
    public Guid? SecurityId { get; init; }
    public string? SecurityTicker { get; init; }
    public string? SecurityName { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }

    /// <summary>
    /// Migration 057: investment posting role marker projected straight
    /// from <c>txn_legs.posting_role</c>. One of <c>'security'</c>,
    /// <c>'income'</c>, <c>'transfer'</c>, <c>'fee'</c> on investment
    /// legs; <c>NULL</c> on non-investment legs (the bulk of bank /
    /// credit-card transactions). Enforced by a trigger:
    /// <c>posting_role IS NOT NULL ⇔ txn_headers.action IS NOT NULL</c>.
    /// Drives the SPA's investment-row classification (fee chip, income
    /// badge, transfer rendering) without category sniffing.
    /// </summary>
    public string? PostingRole { get; init; }

    /// <summary>
    /// ADR-0031 Phase 3d.1: provider-classifier action hint
    /// projected from <c>txn_headers.ingest_action_hint</c>. Set
    /// only on feed-imported rows whose description matched the
    /// SimpleFinDescriptionClassifier patterns; NULL on manual /
    /// MD-imported rows and on unclassifiable brokerage rows.
    /// Editor reads this to pre-fill the action picker on review.
    /// </summary>
    public string? IngestActionHint { get; init; }

    /// <summary>
    /// ADR-0031 Phase 3d.1: provider-classifier security hint
    /// resolved via <c>provider_security_mappings</c> at sync time.
    /// Set when the user has previously mapped the same ticker;
    /// NULL otherwise (the user picks the security manually on
    /// review, which records the mapping for future syncs).
    /// </summary>
    public Guid? IngestSecurityId { get; init; }

    /// <summary>
    /// Migration 113: provider-extracted share count for investment
    /// OFX rows (OFX <c>UNITS</c>). NULL on every other row.
    /// Editor's bank→investment upgrade flow reads this to pre-fill
    /// the shares input.
    /// </summary>
    public decimal? IngestShares { get; init; }

    /// <summary>
    /// Migration 113: provider-extracted per-share price for
    /// investment OFX rows (OFX <c>UNITPRICE</c>). Same population
    /// rules as <see cref="IngestShares"/>.
    /// </summary>
    public decimal? IngestUnitPrice { get; init; }

    /// <summary>
    /// Migration 113: aggregated per-row fee for investment OFX rows
    /// (sum of OFX Commission + Fees + Load + Markup + Markdown).
    /// NULL when zero or when the wire didn't carry a fee.
    /// </summary>
    public decimal? IngestFee { get; init; }

    /// <summary>
    /// Migration 114: provider-extracted security identifier string
    /// (OFX SECLIST-resolved ticker or raw CUSIP fallback). The SPA
    /// reads this on Accept to send the right provider_security_id
    /// in the editor's <c>providerSecurityHint</c> payload so the
    /// mapping survives for the next ingest. NULL for non-OFX rows.
    /// </summary>
    public string? IngestSecurityTickerHint { get; init; }

    /// <summary>
    /// ADR-0031 follow-up (migration 078/079): the original provider
    /// JSON payload for this transaction. NULL on manual + MD-imported
    /// rows + feed rows synced before this column existed (re-sync
    /// backfills via the orchestrator alreadyKnown branch). Projected
    /// as a string here — JSON parsing stays SPA-side.
    /// </summary>
    public string? ProviderRawPayload { get; init; }

    /// <summary>
    /// ADR-0034 v2 (migration 097): owning header's <c>seq</c> projected
    /// through. The canonical sort tiebreaker for every register read
    /// path; pairs with <see cref="PostedAt"/>.
    /// </summary>
    public long HeaderSeq { get; init; }

    /// <summary>
    /// ADR-0034 mig 098/100: per-(header, account) net cash effect.
    /// Same value on every leg of this <c>(header, account)</c> pair,
    /// so the SPA can read it once per entry instead of summing legs.
    /// </summary>
    public decimal? HeaderAccountNetAmount { get; init; }
    /// <summary>Mig 107: per-provider audit detail. NULL when
    /// origin='manual'. See <c>TxnHeaderRow.ProviderKey</c>.</summary>
    public string? ProviderKey { get; init; }
    /// <summary>Mig 107: TRUE when at least one other row was merged
    /// into this row. Drives the register's merge-winner overlay
    /// on the provenance icon.</summary>
    public bool IsMergeWinner { get; init; }
    /// <summary>Mig 107: bootstrap-import marker.
    /// <c>'moneydance_export'</c> on rows from the MD JSON bootstrap;
    /// NULL on rows born in Coffer. Audit / debug only — not surfaced
    /// in the register UI.</summary>
    public string? ImportSource { get; init; }

    /// <summary>
    /// Mig 108: per-leg derived action.
    /// <c>COALESCE(header.action, 'Xfr' when this leg's counterparty
    /// sits on an asset-shaped account, NULL otherwise)</c>.
    ///
    /// <para>True investment events (Buy / Sell / Div / DivReinvest /
    /// Interest / MiscInc / MiscExp / Split / Transfer set on the
    /// header by the importer) pass through unchanged. Cash-shape
    /// headers with NULL <see cref="InvestmentAction"/> (paycheck
    /// splits, manual inter-account transfers) gain a per-leg
    /// <c>'Xfr'</c> label when the leg's counter sits on a non-
    /// category account.</para>
    ///
    /// <para>The SPA aggregator switches on <see cref="InvestmentAction"/>
    /// (header-level) for collapse-vs-expand. Per-posting target rows
    /// render <see cref="DerivedAction"/> directly in the Action chip.</para>
    /// </summary>
    public string? DerivedAction { get; init; }

    /// <summary>
    /// Mig 108: distinct <c>posting_index</c> values of this header
    /// that have a leg on the row's <see cref="AccountId"/>. Combined
    /// with <see cref="HeaderTotalPostings"/>, the register-assembler
    /// distinguishes the originating account (counts equal — keep as
    /// one split-parent entry) from a target account (this count is
    /// less — render one entry per posting on this account).
    /// </summary>
    public int AccountPostingsOnHeader { get; init; }

    /// <summary>
    /// Mig 108: total distinct <c>posting_index</c> values across the
    /// whole header (all accounts). Paired with
    /// <see cref="AccountPostingsOnHeader"/>.
    /// </summary>
    public int HeaderTotalPostings { get; init; }

    /// <summary>
    /// Mig 119 (ADR-0030 §2): the owning account's <c>account_type</c>
    /// — the register-row discriminant. The repository projects
    /// <c>'investment'</c> rows to <c>InvestmentRowDto</c> and every
    /// other type (bank / credit_card / cash / asset / liability /
    /// category) to <c>BankRowDto</c>. Discrimination is by ACCOUNT
    /// domain, not per-leg <see cref="PostingRole"/>: an investment
    /// register renders all its rows (incl. cash deposits and fee
    /// legs) with investment chrome.
    /// </summary>
    public string AccountType { get; init; } = string.Empty;
}
