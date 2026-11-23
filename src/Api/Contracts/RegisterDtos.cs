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
public abstract record RegisterRowDto : IRegisterRowNotes
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

    /// <summary>
    /// The currency <see cref="Amount"/> is in: on a real account, that account's; on a
    /// category, which has no currency of its own, the currency of the real account on
    /// the other side of the row's posting, or the category's own code when the posting
    /// is between two categories (ADR-0114 D7). Rows of one category register can be in
    /// different currencies.
    /// </summary>
    public string CurrencyCode { get; init; } = string.Empty;

    /// <summary>
    /// The running balance after this row. On a real account it is the account's own, in
    /// its own currency. On a category it combines accounts, so it is in
    /// <see cref="BalanceCurrencyCode"/>, the ledger's reporting currency: each transaction
    /// converted at its own date, summed in the register's walk order, rounded once
    /// (ADR-0114 D7). Null when a rate it needs is missing, with
    /// <see cref="BalanceRateUnavailable"/> naming each (ADR-0114 D9).
    /// </summary>
    public decimal? BalanceAfter { get; init; }

    /// <summary>The currency <see cref="BalanceAfter"/> is in: the account's own on a real
    /// account, the ledger's reporting currency on a category.</summary>
    public string BalanceCurrencyCode { get; init; } = string.Empty;

    /// <summary>
    /// Every missing rate the category balance needed, from the first transaction of the
    /// category to this row, when <see cref="BalanceAfter"/> is null for want of one: it and
    /// every later balance are then null, never a partial sum (ADR-0114 D9). Absent from
    /// the JSON otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RateUnavailable>? BalanceRateUnavailable { get; init; }

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

    /// <summary>
    /// The counterparty leg's own amount on a transfer between real accounts in different
    /// currencies, signed, in <see cref="CounterpartyCurrencyCode"/> (ADR-0114 D13, D14):
    /// the register shows it after the account chip. Null on a mirrored pair: a category
    /// row, a transfer in one currency, a posting with no other side.
    /// </summary>
    public decimal? CounterpartyAmount { get; init; }

    /// <summary>The currency <see cref="CounterpartyAmount"/> is in, the counterparty
    /// account's own; null exactly when it is.</summary>
    public string? CounterpartyCurrencyCode { get; init; }

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

    public string? HeaderMemo { get; init; }

    // The memo-and-tags fields of IRegisterRowNotes. Memo, Tags and HeaderMemo above
    // carry the same contract: what the row shows, and the transaction's memo only
    // where the header is in play. The raw per-leg values are not on the row, so
    // the browser cannot show anything the rule did not decide.
    public bool MemoInherited { get; init; }
    public bool TagsInherited { get; init; }
    public string? InheritedMemo { get; init; }
    public IReadOnlyList<string> InheritedTags { get; init; } = Array.Empty<string>();
    public Guid? InheritFromAccountId { get; init; }
    public IReadOnlyList<string>? HeaderTags { get; init; }
    public string NotesKind { get; init; } = string.Empty;
    public bool IsReadOnly { get; init; }

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

    /// <summary>
    /// The bank's figure for the transaction as the row's originals keep it
    /// (<c>txn_header_originals.amount</c>, ADR-0114 D15): its amount on its originating
    /// account, in the row's stored sign, exactly as sent. A JSON string, not a number, so
    /// every digit reaches the client, a trailing zero included. Null when the row has no
    /// originals, or they were captured before the column existed.
    /// </summary>
    public string? OriginalAmount { get; init; }

    /// <summary>True when an import or a promote-on-clear rounded the bank's figure,
    /// <see cref="OriginalAmount"/>, to the account currency's places (ADR-0114 D15). With
    /// <see cref="NeedsReview"/> it shows the Review marker.</summary>
    public bool RoundedAtImport { get; init; }

    /// <summary>
    /// The bank's cleared figure that put the row back in review because the row does not
    /// hold it (<c>txn_headers.review_cleared_amount</c>, ADR-0114 D15): a merged loser's
    /// when <see cref="ReviewClearedByMatch"/>, else the row's own, on a split whose lines
    /// add up to another total. A JSON string, exactly as sent, as <see cref="OriginalAmount"/>
    /// is. Null when no such hold explains the row's review. With <see cref="NeedsReview"/>
    /// it shows the Review marker in place of the rounded note.
    /// </summary>
    public string? ReviewClearedAmount { get; init; }

    /// <summary>True when <see cref="ReviewClearedAmount"/> is a merged loser's cleared
    /// figure, so the row is the winner it was folded into (ADR-0114 D15).</summary>
    public bool ReviewClearedByMatch { get; init; }

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

    // The override flag (has_overrides) is not on the row: nothing in the SPA or MCP
    // reads it (ADR-0100's originals and the view column stay).
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
    // 'security' / 'income' / 'transfer' / 'fee'. Invariant:
    // PostingRole IS NOT NULL ⇔ InvestmentAction IS NOT NULL. NOT enforced
    // by the database — migration 084 dropped the trigger — so every writer
    // that sets or clears an action must do the same to the legs' roles
    // (e.g. BulkTransactionsRepository.ClearInvestmentEventAsync). The SPA's
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

    /// <summary>The source file's authoritative total (mig 228) — the editor
    /// prefills from this rather than rebuilding shares x price.</summary>
    public decimal? IngestAmount { get; init; }

    // Mig 114: persisted OFX ticker hint, so the SPA's Accept flow can
    // record a provider_security_mapping with the SAME identifier the
    // next ingest will look up.
    public string? IngestSecurityTickerHint { get; init; }

    // ADR-0080: server-side investment-event aggregation. The register
    // returns one collapsed event per header (InvestmentEventProjector), so
    // these synthesized slot fields are part of the contract, not computed
    // client-side. Null on rows where
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

    /// <summary>The transfer slot's other leg, on the transfer account, signed, in
    /// <see cref="TransferCurrencyCode"/>, when that account is a real account in another
    /// currency than this one (ADR-0114 D13, D14; ADR-0028 Slot 6): an Xfr, BuyXfr, SellXfr
    /// or DividendXfr between currencies. Null when the transfer mirrors.</summary>
    public decimal? TransferAmount { get; init; }

    /// <summary>The transfer account's currency, which <see cref="TransferAmount"/> is
    /// in; null exactly when it is.</summary>
    public string? TransferCurrencyCode { get; init; }

    public decimal? FeeAmount { get; init; }
    public Guid? FeeCategoryId { get; init; }
    public string? FeeCategoryName { get; init; }

    /// <summary>
    /// The event's settled money (ADR-0073 D1) — the trade value — as opposed
    /// to <c>Amount</c>, which is ADR-0028's net of this account's legs.
    /// </summary>
    /// <remarks>
    /// The two disagree precisely when the event is cash-neutral, which is not
    /// an edge: a reinvestment, an in-kind transfer and a fee-consumed sale all
    /// net to zero by construction, so a register reading only the cash net
    /// shows each of them as zero while the editor behind the same row shows
    /// the trade value.
    /// Null when no leg carries a settled amount.
    /// </remarks>
    public decimal? SettledAmount { get; init; }
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
/// One entry of <c>GET /api/ledgers/{ledgerId}/transactions/entries</c>: a register
/// entry, with the account whose register shows it and that account's currency.
/// </summary>
/// <param name="AccountId">The account whose register shows <paramref name="Entry"/>.
/// The same header can be an entry on more than one account (a transfer between two
/// of them is one on each), so an entry is named by this and its key, never by its
/// header alone.</param>
/// <param name="CurrencyCode">That account's ISO 4217 currency, the one every amount in
/// <paramref name="Entry"/> is in. Entries on accounts in different currencies sit on one
/// page; nothing here converts them.</param>
/// <param name="Entry">The entry exactly as that account's register page
/// (<c>GET /transactions?account_id=</c>) returns it: a single row, or a split on the
/// side it belongs to as one group with its lines.</param>
public sealed record LedgerEntryDto(Guid AccountId, string CurrencyCode, RegisterEntryDto Entry);

/// <summary>
/// The newest register entries across a ledger, for the home screen's lists: every
/// account a person reads (not a category, not a holdings sibling; inactive ones too).
/// </summary>
/// <param name="Entries">The entries, newest first: by date, then as each register
/// orders the entries of one day, then by account.</param>
/// <param name="HasMore">Whether more entries match than the page holds. There is no
/// cursor: this is a first page, and the registers are where the rest is read.</param>
public sealed record LedgerEntriesPage(IReadOnlyList<LedgerEntryDto> Entries, bool HasMore);

/// <summary>
/// What a save (bank and investment PATCH, approve, merge) answers when the caller
/// names the register it is looking at: every entry the surviving header has in that
/// account, in the order and grouping the page shows them. The SPA replaces that
/// header's rows in its window with exactly these. A header with no entry there gets
/// 204 instead.
/// </summary>
/// <param name="Entries">One or more entries: one on the side the header belongs
/// to, one per line on an opposite side.</param>
public sealed record SavedEntriesResponse(IReadOnlyList<RegisterEntryDto> Entries);

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
/// <param name="Count">Register entries in this month for the requested
/// account, as the page groups them: one per header on the side it
/// belongs to, one per line on an opposite side. Useful for
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
/// after this header applies, as the register row shows it
/// (<see cref="RegisterRowDto.BalanceAfter"/>): on a real account the stored
/// <c>txn_header_account_balances.balance_after</c>; on a category converted into the
/// ledger's reporting currency, and null when a rate it needs is missing.</param>
/// <param name="NetAmount">Per-(header, account) cash delta, as stored (mig 098). The
/// SPA reads it onto every leg of the entry so multi-posting collapses match the
/// server-stored value.</param>
/// <param name="BalanceRateUnavailable">Every missing rate a null category balance needed
/// (ADR-0114 D9); absent from the JSON otherwise.</param>
public sealed record HeaderBalanceDto(
    Guid HeaderId,
    decimal? BalanceAfter,
    decimal NetAmount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? BalanceRateUnavailable = null);

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
/// One row of balance drift found by the balance check or the balance repair
/// (<c>RegisterRepository</c>), which the consistency check and the <c>balances</c>
/// projection's repair shape as mismatches. Either a running-balance row the walk of the
/// legs produces (the rows the recompute stores) whose stored
/// <c>txn_header_account_balances</c> row holds another <c>balance_after</c> or does not
/// exist, or a stored row the walk does not produce at all. From the check, the drift is
/// present now; from the repair, it was present before the repair rewrote the rows.
/// </summary>
/// <param name="StoredBefore">The stored <c>balance_after</c>; 0 when no row is stored
/// (<paramref name="Reason"/> <see cref="NotStored"/>), so <paramref name="Diff"/> is then
/// the whole walked balance rather than a difference against a value that does not
/// exist.</param>
/// <param name="RecomputedAfter">The walk's <c>balance_after</c> for the row; 0 when the
/// walk produces none (<paramref name="Reason"/> <see cref="NotDerived"/>), the row the
/// repair removes.</param>
/// <param name="Diff"><paramref name="RecomputedAfter"/> less <paramref name="StoredBefore"/>.</param>
/// <param name="Reason">What kind of drift: <see cref="ValueDiffers"/> (a stored row holds
/// another balance), <see cref="NotStored"/> (no row is stored for one the walk produces)
/// or <see cref="NotDerived"/> (a row is stored that the walk does not produce). Data, so
/// a reader never has to tell a missing row, or one that should not exist, from a stored
/// 0 by the figures.</param>
public sealed record BalanceHealthDriftDto(
    Guid AccountId,
    string AccountName,
    Guid HeaderId,
    DateTime PostedAt,
    decimal StoredBefore,
    decimal RecomputedAfter,
    decimal Diff,
    string Reason)
{
    /// <summary>A stored row holds a balance other than the walk's
    /// (<see cref="ConsistencyReasons.ValueDiffers"/>).</summary>
    public const string ValueDiffers = ConsistencyReasons.ValueDiffers;

    /// <summary>No row is stored for a (header, account) the walk produces
    /// (<see cref="ConsistencyReasons.NotStored"/>).</summary>
    public const string NotStored = ConsistencyReasons.NotStored;

    /// <summary>A row is stored for a (header, account) the walk does not produce
    /// (<see cref="ConsistencyReasons.NotDerived"/>).</summary>
    public const string NotDerived = ConsistencyReasons.NotDerived;
}

/// <summary>
/// What the balance check or the balance repair (<c>RegisterRepository</c>) found, before
/// the consistency check and the <c>balances</c> projection's repair shape it as that
/// projection's report. <see cref="Healthy"/> mirrors <c>Drifted.Count == 0</c>.
/// </summary>
/// <remarks>
/// From the check, the drift is present NOW: checking does not heal anything (migration
/// 206 splits calculation from persistence). Repair is a separate, deliberate
/// <c>POST .../balances/consistency/balances/repair</c>. A caller that believes the check
/// healed anything leaves the ledger stale.
/// </remarks>
public sealed record BalanceHealthReport(
    bool Healthy,
    int AccountsChecked,
    int RowsChecked,
    int DriftedCount,
    IReadOnlyList<BalanceHealthDriftDto> Drifted);
