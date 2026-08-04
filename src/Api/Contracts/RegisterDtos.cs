using System.Text.Json.Serialization;

namespace Coffer.Api.Contracts;

/// <summary>
/// Public per-row DTO for the register query — a <c>kind</c>-discriminated
/// union (ADR-0030 §2). Replaces the former single bag-of-nullable-per-
/// domain-fields record: consumers now pattern-match on <c>kind</c> and
/// touch only the fields their domain actually carries.
///
/// <para>The discriminant is the owning ACCOUNT's domain (mig 119's
/// <c>account_type</c>), not a per-leg signal — an investment register
/// renders every one of its rows with investment chrome (including cash
/// deposits and fee legs that touch no security), so <c>kind</c> follows
/// the account, not the leg's <c>posting_role</c>.</para>
///
/// <para>This base carries the ~40 universal fields every register row
/// has regardless of domain. <see cref="BankRowDto"/> adds nothing;
/// <see cref="InvestmentRowDto"/> adds the investment + ingest-prefill
/// fields. System.Text.Json emits/reads the <c>kind</c> discriminator
/// (<c>"bank"</c> / <c>"investment"</c>) as the first property of each
/// row object.</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BankRowDto), typeDiscriminator: "bank")]
[JsonDerivedType(typeof(InvestmentRowDto), typeDiscriminator: "investment")]
public abstract record RegisterRowDto
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public string? Payee { get; init; }
    public string? Memo { get; init; }
    public required decimal Amount { get; init; }
    public required DateTime PostedAt { get; init; }
    public DateTime? TransactedAt { get; init; }

    /// <summary>
    /// Normalized 3-state reconciliation vocabulary (migration 030):
    /// exactly one of <c>uncleared</c>, <c>reconciling</c>, <c>cleared</c>.
    /// Never null — the DB CHECK constraint enforces validity at the row
    /// level.
    /// </summary>
    public required string Status { get; init; }

    public required bool IsHidden { get; init; }
    public required bool HasOverrides { get; init; }
    public decimal? BalanceAfter { get; init; }
    public required string Origin { get; init; }
    public required bool IsPending { get; init; }
    public string? ExternalId { get; init; }

    // Register-parity columns surfaced via migration 018. The
    // counterparty fields are the "other side" of ADR-0019 symmetric
    // postings — the MD register treats them as the Category column.
    // Tags is always a (possibly empty) array, never null.
    public string? CheckNumber { get; init; }
    public required Guid CounterpartyId { get; init; }
    public Guid? TxnGroupId { get; init; }
    public required int LegIndex { get; init; }
    public Guid? CounterpartyAccountId { get; init; }
    public string? CounterpartyAccountName { get; init; }
    public string? CounterpartyAccountType { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>
    /// Migration 028: always-present header identity. Distinct from
    /// <see cref="TxnGroupId"/> (NULL for singles); HeaderId carries
    /// the owning header unconditionally so the SPA's inline-edit POST
    /// can address it without resolving leg→header on every save.
    /// </summary>
    public required Guid HeaderId { get; init; }

    // Migration 030: cleared-transition audit. ClearedAt is non-null
    // exactly when Status == "cleared" (DB CHECK enforces); ClearedByUserId
    // is the user who marked it (NULL after that user's row is deleted
    // — FK ON DELETE SET NULL).
    public DateTime? ClearedAt { get; init; }
    public Guid? ClearedByUserId { get; init; }

    /// <summary>
    /// Leg-insertion timestamp (txn_legs.created_at). All legs of a
    /// single insert event share this value (one transaction, one
    /// now()). Surfaced so the bulk-selection 'all' predicate honors
    /// <c>selectedAt</c> consistently. See ADR-0024.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    // Migration 032 (ADR-0025): raw leg-vs-header memo split so the
    // SPA's editor + display can distinguish per-posting from umbrella
    // memos. The Memo field above remains the full 4-way COALESCE.
    public string? LegMemo { get; init; }
    public string? HeaderMemo { get; init; }

    // Migration 034: OFX online-match identity — fitid + fi_id are the
    // OFX dedup composite key. Universal: both bank and investment rows
    // can originate from OFX. The audit-only columns (status / type /
    // orig_id) were dropped in mig 109 (ADR-0035 §4).
    public string? OnlineMatchFitid { get; init; }
    public string? OnlineMatchFiId { get; init; }

    // Migration 037 (slice 2c): bank-feed rows the user hasn't approved
    // yet. SPA renders flagged rows with a visual treatment until POST
    // /transactions/{headerId}/approve clears the flag.
    public required bool NeedsReview { get; init; }

    // ADR-0031 follow-up (migration 078/079): original provider JSON
    // payload, surfaced to the SPA's "Show raw provider data" diagnostic
    // modal. Universal — any provider-sourced row can carry it.
    public string? ProviderRawPayload { get; init; }

    // ADR-0034 mig 098/100: per-(header, account) net cash effect,
    // projected through resolved_transactions from
    // txn_header_account_balances. Same value on every leg of the
    // (header, account) pair. Null when no balance row exists yet.
    public decimal? HeaderAccountNetAmount { get; init; }

    // Mig 107: per-provider audit detail. One of `simplefin` / `mdplus`
    // / `ofx` / `qif` / `csv`. NULL when origin='manual'. Drives the
    // provenance icon's provider-detail hover.
    public string? ProviderKey { get; init; }

    // Mig 107: TRUE when at least one other row has merged into this
    // row. Drives the merge-winner overlay on the provenance icon.
    public required bool IsMergeWinner { get; init; }

    // Mig 107: bootstrap-import marker. `'moneydance_export'` on rows
    // from the MD JSON bootstrap; null on rows born in Coffer.
    public string? ImportSource { get; init; }

    // Mig 108: per-leg derived action. COALESCE(header.action, 'Xfr'
    // when the leg's counterparty sits on an asset-shaped account).
    // Universal: cash-shape headers gain a per-leg 'Xfr' on transfer
    // legs, so bank rows carry it too.
    public string? DerivedAction { get; init; }

    // Mig 108 / ADR-0036: distinct posting_index values of this header
    // that touch the row's account. Combined with HeaderTotalPostings,
    // the SPA distinguishes originating (counts equal) from target
    // (less than) — drives the read-only "↗ Split" chip.
    public required int AccountPostingsOnHeader { get; init; }

    // Mig 108 / ADR-0036: total distinct posting_index values across
    // the whole header (all accounts).
    public required int HeaderTotalPostings { get; init; }
}

/// <summary>
/// A register row on a bank-domain account (bank / credit_card / cash /
/// asset / liability / category). Carries only the universal fields —
/// the investment + ingest-prefill fields are absent by construction,
/// which is the whole point of the discriminated union (ADR-0030 §2).
/// </summary>
public sealed record BankRowDto : RegisterRowDto;

/// <summary>
/// A register row on an investment-domain account. Adds the investment-
/// leg metadata and the OFX ingest-prefill carriers on top of the
/// universal base. Every leg returned by the cross-account
/// <c>/legs</c> endpoint is projected as this shape (it serves the
/// investment editor, whose <c>legsToDraft</c> reads
/// <see cref="PostingRole"/> / <see cref="SecurityId"/> /
/// <see cref="Quantity"/> off every leg regardless of which account it
/// sits on).
/// </summary>
public sealed record InvestmentRowDto : RegisterRowDto
{
    // Header action (`buy` / `sell` / `div` / …). NULL on cash-shape
    // rows of an investment account (deposits, plain transfers).
    public string? InvestmentAction { get; init; }

    // Migration 045 (slice A1.c): investment-leg metadata. Joined from
    // `txn_legs` + `securities`. Null on the cash side of a posting.
    public Guid? SecurityId { get; init; }
    public string? SecurityTicker { get; init; }
    public string? SecurityName { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }

    // Migration 057 (slice A4.a): posting role marker. One of
    // 'security' / 'income' / 'transfer' / 'fee'. DB trigger enforces:
    // PostingRole IS NOT NULL ⇔ InvestmentAction IS NOT NULL. The SPA's
    // investment aggregator dispatches off this value.
    public string? PostingRole { get; init; }

    // ADR-0031 Phase 3d.1: provider-classifier action hint
    // (`buy` / `sell` / `dividend_cash` / `dividend_reinvest` /
    // `transfer`). Set only on feed rows whose description matched the
    // classifier patterns. The editor pre-fills the action picker from
    // it on open.
    public string? IngestActionHint { get; init; }

    // ADR-0031 Phase 3d.1: provider-classifier security hint resolved
    // at sync time via provider_security_mappings. Non-null when the
    // user has already mapped the same ticker for this provider.
    public Guid? IngestSecurityId { get; init; }

    // Mig 113: per-row investment prefill carriers (OFX UNITS /
    // UNITPRICE / COMMISSION+Fees+Load+…). The editor's bank→investment
    // upgrade flow (hintToDraft) reads these to pre-fill the shares /
    // price / fee draft slots.
    public decimal? IngestShares { get; init; }
    public decimal? IngestUnitPrice { get; init; }
    public decimal? IngestFee { get; init; }

    // Mig 114: persisted OFX ticker hint, so the SPA's Accept flow can
    // record a provider_security_mapping with the SAME identifier the
    // next ingest will look up.
    public string? IngestSecurityTickerHint { get; init; }

    // ADR-0080: server-side investment-event aggregation. The register now
    // returns one collapsed event per header (InvestmentEventProjector), so
    // these synthesized slot fields — previously computed client-side in
    // investmentAggregator.ts — are part of the contract. Null on rows where
    // the corresponding role leg is absent.
    //
    //   Category slot  — the income-role leg's counterparty (slot 6 line-1 left).
    //   Transfer slot  — the transfer-role (or derived-Xfr) leg's counterparty.
    //   Fee            — the single fee-role leg: |amount| + its category.
    public Guid? CategoryAccountId { get; init; }
    public string? CategoryAccountName { get; init; }
    public string? CategoryAccountType { get; init; }
    public Guid? TransferAccountId { get; init; }
    public string? TransferAccountName { get; init; }
    public string? TransferAccountType { get; init; }
    public decimal? FeeAmount { get; init; }
    public Guid? FeeCategoryId { get; init; }
    public string? FeeCategoryName { get; init; }
}

/// <summary>
/// One logical entry in the register — either a single transaction or a
/// multi-split group with its legs nested. The endpoint paginates by
/// entry (not by row) so a user-facing page of N entries always shows
/// N "things", regardless of how many legs each split contains. See
/// ADR-0019 (symmetric postings) for the underlying data shape.
/// </summary>
/// <remarks>
/// JSON shape:
/// <code>
/// // single transaction (txn.kind is the row discriminator):
/// { "kind": "txn", "txn": { "kind": "bank", ... } }
///
/// // multi-split group:
/// {
///   "kind": "group",
///   "groupId": "&lt;uuid&gt;",
///   "legs": [ { "kind": "investment", ... } (sorted by leg_index ASC) ]
/// }
/// </code>
/// The outer <c>kind</c> is this entry's txn/group discriminator; the
/// nested row objects carry their own <c>kind</c> (<c>bank</c> /
/// <c>investment</c>) per <see cref="RegisterRowDto"/>.
/// </remarks>
public sealed record RegisterEntryDto(
    string Kind,
    RegisterRowDto? Txn,
    Guid? GroupId,
    IReadOnlyList<RegisterRowDto>? Legs)
{
    public const string KindTxn = "txn";
    public const string KindGroup = "group";

    /// <summary>Construct a single-transaction entry.</summary>
    public static RegisterEntryDto ForTxn(RegisterRowDto txn) =>
        new(KindTxn, txn, GroupId: null, Legs: null);

    /// <summary>Construct a multi-split-group entry. Legs must be
    /// sorted by leg_index ascending — the SPA renders them in that
    /// order on expand.</summary>
    public static RegisterEntryDto ForGroup(
        Guid groupId,
        IReadOnlyList<RegisterRowDto> legs) =>
        new(KindGroup, Txn: null, groupId, legs);
}

/// <summary>
/// One page of register entries plus two opaque cursors — one for
/// each scroll direction. The composite cursor encodes
/// <c>(posted_at, created_at, entry_key)</c> where entry_key is
/// <c>COALESCE(txn_group_id, id)</c>; page boundaries always fall
/// between entries, never inside a group (ADR-0019 entry-keyed
/// pagination).
/// </summary>
/// <param name="Entries">Entries on this page, time-DESC.</param>
/// <param name="CursorForOlder">Pass this back with
/// <c>direction='before'</c> on the next call to load entries
/// older than this page's oldest. <c>null</c> when there are no
/// older entries to load (the timeline tail).</param>
/// <param name="CursorForNewer">Pass this back with
/// <c>direction='after'</c> on the next call to load entries newer
/// than this page's newest. <c>null</c> when there are no newer
/// entries to load (the timeline head — typically the first
/// "Load most recent" page).</param>
public sealed record RegisterPage(
    IReadOnlyList<RegisterEntryDto> Entries,
    string? CursorForOlder,
    string? CursorForNewer);

/// <summary>
/// One bucket on the date-aware scroll-track (ADR-0034 follow-up).
/// Drives the SPA's custom scroll-track that replaces the native
/// scrollbar on the register: each bucket is one month with at least
/// one visible entry. Months with no entries are absent — the SPA
/// renders the present buckets at uniform pixel height, so years
/// with sparse activity naturally cluster (Google Photos pattern).
/// </summary>
/// <param name="YearMonth">ISO-8601 month, <c>yyyy-MM</c>. Sortable as
/// a string. The SPA renders the year part as the gutter label and
/// uses the full key for cache identity.</param>
/// <param name="Count">Distinct register entries (header count, not
/// leg count) in this month for the requested account. Useful for
/// hover tooltips ("Mar 2024 — 87 entries") and a future activity
/// overlay, even though the v1 track sizing is uniform-per-month.</param>
/// <param name="SampleHeaderId">A header in this month — specifically
/// the most-recent one by canonical <c>(posted_at, seq)</c>. Used as
/// the re-seed anchor: clicking the bucket triggers
/// <c>register.refresh(SampleHeaderId)</c>, which opens a window with
/// that entry visible at the top.</param>
public sealed record IndexBucketDto(
    string YearMonth,
    int Count,
    Guid SampleHeaderId);

/// <summary>
/// Per-(header, account) balance projection from
/// <c>txn_header_account_balances</c>. Returned by the bulk
/// <c>POST /transactions/balances</c> endpoint so the SPA can refresh
/// just the balance + net-amount columns on its currently-loaded
/// register window after a mutation — without re-fetching the entire
/// page (which causes a virtuoso data-swap and a perceptible scroll
/// jump). The SPA patches each existing entry in place via
/// <c>register.mutateEntries</c>.
/// </summary>
/// <param name="HeaderId">Owning header. Pair with the per-account
/// scope from the request to identify the row.</param>
/// <param name="BalanceAfter">Running balance on the requested account
/// after this header applies. Sourced verbatim from
/// <c>txn_header_account_balances.balance_after</c>.</param>
/// <param name="NetAmount">Per-(header, account) cash delta. The
/// step contributed to <see cref="BalanceAfter"/> by this header
/// (mig 098). The SPA reads it onto every leg of the entry so
/// multi-posting collapses match the server-stored value.</param>
public sealed record HeaderBalanceDto(
    Guid HeaderId,
    decimal BalanceAfter,
    decimal NetAmount);

/// <summary>
/// Body for <c>POST /transactions/balances</c>. The list of header
/// ids the SPA wants fresh balance / net-amount values for — typically
/// every header in the current register window.
/// </summary>
public sealed class HeaderBalancesRequest
{
    public IReadOnlyList<Guid> HeaderIds { get; init; } = Array.Empty<Guid>();
}

/// <summary>
/// One row of balance drift surfaced by the balance health check.
/// Each entry represents a stored
/// <c>txn_header_account_balances.balance_after</c> value that differs
/// from what a fresh <c>fn_recompute_balances_for_account</c> would
/// produce for the same row. The health endpoint runs the recompute
/// (which is idempotent + auto-heals), so receiving a non-empty
/// <c>drifted</c> array means BOTH (a) drift was present at check
/// time and (b) it has now been corrected.
/// </summary>
public sealed record BalanceHealthDriftDto(
    Guid AccountId,
    string AccountName,
    Guid HeaderId,
    DateTime PostedAt,
    decimal StoredBefore,
    decimal RecomputedAfter,
    decimal Diff);

/// <summary>
/// Result of <c>POST /api/ledgers/{ledgerId}/balances/health</c>.
/// <see cref="Healthy"/> mirrors <c>Drifted.Count == 0</c>; if any
/// drift was present, the recompute side-effect has already healed
/// the rows.
/// </summary>
public sealed record BalanceHealthReport(
    bool Healthy,
    int AccountsChecked,
    int RowsChecked,
    int DriftedCount,
    IReadOnlyList<BalanceHealthDriftDto> Drifted);
