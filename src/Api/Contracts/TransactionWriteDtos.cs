namespace Coffer.Api.Contracts;

/// <summary>
/// Request body for
/// <c>PUT /api/ledgers/{ledgerId}/transactions/{headerId}/recon-status</c>.
/// Reconciliation is per-account (ADR-0082), so the body carries the
/// <see cref="AccountId"/> whose register is toggling the state — the status
/// is set on that account's leg of the transaction, not the header.
/// </summary>
public sealed class SetReconStatusRequest
{
    /// <summary>
    /// Must be one of <c>uncleared</c>, <c>reconciling</c>,
    /// <c>cleared</c>. Anything else returns
    /// <c>transaction-recon-status-invalid</c>.
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// The account whose reconciliation state is being set (ADR-0082). The
    /// register supplies the account it's showing; the status applies to that
    /// account's leg(s) of the transaction.
    /// </summary>
    public Guid AccountId { get; init; }
}

/// <summary>
/// Response body for the delete endpoint. The caller surfaces a
/// different confirmation toast depending on whether the row was
/// physically removed or hidden — see the API endpoint docs for the
/// policy.
/// </summary>
/// <param name="Kind">Either <c>"hard-deleted"</c> (header gone +
/// cascades) or <c>"soft-hidden"</c> (is_hidden=true; row preserved
/// for re-source idempotency).</param>
public sealed record DeleteTransactionResponse(string Kind);

/// <summary>
/// One row of <c>GET /api/ledgers/{ledgerId}/payees</c>. Built by
/// aggregating <c>txn_headers</c> (with <c>txn_header_overrides</c>
/// COALESCE applied) so the suggestion list reflects what the user
/// last typed, not whatever the importer originally wrote.
/// </summary>
/// <param name="Name">Resolved payee text — the override value when
/// present, the header value otherwise.</param>
/// <param name="Count">Number of headers in this ledger that resolve
/// to this payee. Drives the primary sort.</param>
/// <param name="LastUsedAt">Most recent <c>posted_at</c> among the
/// headers with this payee. Drives the tiebreaker sort.</param>
public sealed record PayeeSuggestion(
    string Name,
    int Count,
    DateTime LastUsedAt);

/// <summary>
/// One posting in a transaction's create or edit shape (ADR-0025).
/// A transaction is a list of these — single-row when N=1, split
/// when N>1. Sign convention matches the register's signed Amount
/// column: <see cref="Amount"/> is on the source-side leg (the
/// account being viewed); the server writes the paired counterparty
/// leg's amount as <c>-Amount</c> so each posting sums to zero per
/// ADR-0019.
/// </summary>
public sealed class TransactionPosting
{
    /// <summary>
    /// PATCH only: the existing source-side leg id this posting
    /// preserves. When the value matches an existing leg on the
    /// header, that posting is kept (counterparty / amount /
    /// memo updated as supplied). When <c>null</c> or unmatched,
    /// a new posting is created. On <c>POST</c> this is always
    /// ignored.
    /// </summary>
    public Guid? LegId { get; init; }
    /// <summary>
    /// The "other side" of this posting — a category for income
    /// / expense, or another asset/liability account for
    /// transfers. Must live in the same ledger and differ from
    /// <c>sourceAccountId</c>.
    /// </summary>
    public Guid CounterpartyAccountId { get; init; }
    /// <summary>
    /// Signed amount on the source-side leg. Negative = outflow.
    /// Zero rejected (silently meaningless posting).
    /// </summary>
    public decimal Amount { get; init; }
    /// <summary>
    /// Optional per-posting memo (MD's <c>split.desc</c>).
    /// Distinct from the header memo.
    /// </summary>
    public string? LegMemo { get; init; }
}

/// <summary>
/// Request body for
/// <c>POST /api/ledgers/{ledgerId}/transactions</c> — create a
/// manual transaction with one or more postings (ADR-0025).
///
/// <c>postings.Count == 1</c> creates a single-row transaction;
/// <c>postings.Count &gt; 1</c> creates a multi-split. There is no
/// separate "create split" endpoint — the schema treats them
/// identically and so does this surface.
/// </summary>
public sealed class CreateTransactionRequest
{
    public DateTime PostedAt { get; init; }
    public string? Payee { get; init; }
    public string? Memo { get; init; }
    /// <summary>
    /// Optional check number — short free-text matching what MD writes
    /// to <c>txn.chk</c>. Persisted on the new header.
    /// </summary>
    public string? CheckNumber { get; init; }
    public DateTime? TransactedAt { get; init; }
    /// <summary>
    /// The register's account — every posting's source-side leg
    /// goes here. All postings share this single source account
    /// (mirroring how MD's 14-split paycheck has 14 postings all
    /// touching one bank account on the source side).
    /// </summary>
    public Guid SourceAccountId { get; init; }
    /// <summary>
    /// One or more postings. The schema's sum-to-zero invariant is
    /// per-posting (each <c>(source, counterparty)</c> pair sums
    /// to zero), not across the transaction; the total of the
    /// transaction is whatever the postings sum to.
    /// </summary>
    public IReadOnlyList<TransactionPosting> Postings { get; init; }
        = Array.Empty<TransactionPosting>();

    /// <summary>
    /// Slice 2c.6b: tags to attach to the new header. Same
    /// case-insensitive create-on-first-use semantics as the PATCH
    /// surface. Omitted / null / <c>[]</c> all produce a no-tags
    /// transaction (the editor sends an empty list when the user
    /// added no tags, never the omitted form).
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// The postings sub-shape of <see cref="PatchTransactionRequest"/>.
/// When present, replaces the header's postings list wholesale —
/// existing legs whose <see cref="TransactionPosting.LegId"/> is
/// referenced by an item are preserved (with the supplied
/// fields applied); existing legs not referenced are deleted;
/// items without <c>legId</c> become new postings. Order in
/// <see cref="Items"/> determines the new <c>posting_index</c>.
/// </summary>
public sealed class PatchTransactionPostings
{
    /// <summary>
    /// The register's account. Must match every existing
    /// source-side leg's account (the SPA can't move a
    /// transaction across accounts via this endpoint — that's a
    /// distinct operation that doesn't exist yet).
    /// </summary>
    public Guid SourceAccountId { get; init; }
    public IReadOnlyList<TransactionPosting> Items { get; init; }
        = Array.Empty<TransactionPosting>();
}

/// <summary>
/// Request body for
/// <c>PATCH /api/ledgers/{ledgerId}/transactions/{headerId}</c>.
///
/// Header fields (optional) + an optional <see cref="Postings"/>
/// reshape (ADR-0025). When <c>postings</c> is supplied the server
/// reconciles the existing legs to match the requested list —
/// covering single ↔ split conversion, posting add / remove,
/// reorder, and amount / counterparty / memo edits inside one
/// atomic Postgres transaction. When <c>postings</c> is omitted
/// the postings are untouched and only header fields update.
/// </summary>
public sealed class PatchTransactionRequest
{
    public string? Payee { get; init; }
    public string? Memo { get; init; }
    /// <summary>
    /// Optional check number override. Goes through the same
    /// <c>txn_header_overrides</c> layer as Payee / Memo per ADR-
    /// 0003: feed-imported transactions keep their canonical
    /// <c>check_number</c> on <c>txn_headers</c>, the user's
    /// edited value lands on the override row.
    /// </summary>
    public string? CheckNumber { get; init; }
    /// <summary>Bank-side posted date (the register's primary date column).</summary>
    public DateTime? PostedAt { get; init; }
    /// <summary>Tax / transaction date — distinct from posted_at.</summary>
    public DateTime? TransactedAt { get; init; }
    /// <summary>
    /// When supplied, replaces the postings list wholesale per the
    /// reconcile rules in <see cref="PatchTransactionPostings"/>.
    /// When <c>null</c> / omitted, the postings list is untouched.
    /// </summary>
    public PatchTransactionPostings? Postings { get; init; }

    /// <summary>
    /// Slice 2c.6a: when <c>true</c>, also clears
    /// <c>needs_review</c> on this header inside the same Postgres
    /// transaction. Lets the SPA collapse the "edit + approve a
    /// bank-feed row" flow into one round-trip. Idempotent when
    /// the row is already approved.
    /// </summary>
    public bool? Approve { get; init; }

    /// <summary>
    /// Slice 2c.6b: replace the tag set on this header in the same
    /// PATCH. When supplied, the resulting <c>txn_header_tags</c>
    /// pairings match exactly <see cref="Tags"/>: tag names not in
    /// the list lose their pairing, names not yet in the ledger's
    /// <c>tags</c> dictionary are created on first use.
    /// <list type="bullet">
    ///   <item><c>null</c> / omitted → tags untouched.</item>
    ///   <item><c>[]</c> → all tags removed.</item>
    /// </list>
    /// Matching against the ledger dictionary is case-insensitive;
    /// the first user-supplied casing wins on insert.
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Fold this PATCH's target into the header named here. Direction is
    /// INVERTED (post "merge-direction-invert"): the URL <c>headerId</c> (this
    /// PATCH's target) is the LOSER — the server stamps
    /// <c>is_merged_into = &lt;this MergeFromHeaderId&gt;</c> on it — and
    /// <c>MergeFromHeaderId</c> is the surviving WINNER (marked
    /// <c>is_merge_winner=true</c>, adopting the loser's posted date). The user
    /// picked the canonical row in the candidates panel; the fresh needs_review
    /// editor row vanishes from the register (its <c>external_id</c> is kept so
    /// future syncs dedup against it). A merge-only PATCH carries just this field
    /// (+ optional <c>approve</c>); no postings — the loser is tombstoned, not
    /// reshaped.
    ///
    /// <para>Validation (all → 422 <c>merge-source-invalid</c>): the editor row
    /// must still be a fresh <c>needs_review</c>, un-merged, visible row; the
    /// candidate must exist in the same ledger and be settled
    /// (<c>!needs_review</c>), un-merged, and visible (a prior merge WINNER is
    /// allowed — one-hop collapse); and the two must differ. Origin is NOT
    /// constrained — a candidate may be manual, bank-fed, or imported.</para>
    /// </summary>
    public Guid? MergeFromHeaderId { get; init; }
}

// ----------------------------------------------------------------------
// Bulk selection (ADR-0024)
// ----------------------------------------------------------------------

/// <summary>
/// Maximum number of header ids the SPA may carry in
/// <see cref="SelectionRequest.ExcludeIds"/> (in <c>"all"</c> kind) or
/// <see cref="SelectionRequest.HeaderIds"/> (in <c>"explicit"</c> kind).
/// Caps the request payload at ~360 KB worst case and pre-empts the
/// pathological "select all then individually uncheck 50K rows" shape
/// — past this point the SPA should encourage the user to refine the
/// filter instead.
/// </summary>
public static class SelectionLimits
{
    public const int MaxIds = 10_000;
}

/// <summary>
/// Discriminated request shape for the bulk-action endpoints. Carries
/// the user's selection across two distinct modes:
///
/// <list type="bullet">
///   <item><b>"explicit"</b> — the user clicked specific row
///   checkboxes. <see cref="HeaderIds"/> enumerates the chosen header
///   ids; <see cref="StatusFilter"/>, <see cref="SelectedAt"/>, and
///   <see cref="ExcludeIds"/> are ignored.</item>
///   <item><b>"all"</b> — the user clicked the header "select all"
///   checkbox. The selection is <i>everything in the current view
///   filter as of <see cref="SelectedAt"/>, minus <see cref="ExcludeIds"/></i>.
///   <see cref="HeaderIds"/> is ignored.</item>
/// </list>
///
/// The <c>"all"</c> shape captures Gmail-style "all 1247 selected"
/// semantics in one round-trip — the SPA never enumerates ids, the
/// server resolves the predicate. <see cref="SelectedAt"/> pins the
/// predicate to the moment the user clicked select-all, so rows
/// created after that point (manual entry, feed sync) do NOT silently
/// join the selection.
/// </summary>
public sealed class SelectionRequest
{
    /// <summary>Discriminator: <c>"explicit"</c> or <c>"all"</c>. Any other
    /// value returns <c>selection-kind-invalid</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Header ids the user explicitly selected (in
    /// <c>kind="explicit"</c> mode). Capped at
    /// <see cref="SelectionLimits.MaxIds"/>. Ignored in <c>"all"</c>
    /// mode.
    /// </summary>
    public IReadOnlyList<Guid> HeaderIds { get; init; } = Array.Empty<Guid>();

    /// <summary>
    /// Account scope. In <c>"all"</c> mode it narrows the predicate to one
    /// account in the ledger (null = every account). In <c>"explicit"</c> mode
    /// the bulk-apply query ignores it (it acts on <see cref="HeaderIds"/>), but
    /// the summary still uses it to compute the account-scoped Σ shown in the
    /// footer.
    /// </summary>
    public Guid? AccountId { get; init; }

    /// <summary>
    /// Status filter for <c>"all"</c> mode — one of <c>"all"</c>,
    /// <c>"cleared"</c>, <c>"uncleared"</c>, <c>"scheduled"</c>,
    /// <c>"needs_review"</c>.
    /// <c>"uncleared"</c> matches both <c>uncleared</c> and
    /// <c>reconciling</c> states (the register's "Uncleared" filter
    /// is everything not-yet-cleared). <c>"scheduled"</c> matches
    /// rows with <c>posted_at &gt; now()</c> regardless of status.
    /// <c>"needs_review"</c> matches the bank-feed review flag
    /// (<c>needs_review</c>, migration 037) — a separate dimension from
    /// the recon status, mirroring the register's "Needs review" tab.
    /// </summary>
    public string StatusFilter { get; init; } = "all";

    /// <summary>
    /// Selection-time anchor for <c>"all"</c> mode. The predicate
    /// only includes rows whose <c>created_at &lt;= SelectedAt</c>
    /// — newly inserted rows after this moment are excluded. The
    /// SPA captures this when the user clicks the select-all
    /// checkbox.
    /// </summary>
    public DateTime SelectedAt { get; init; }

    /// <summary>
    /// Header ids the user individually unchecked while in
    /// <c>"all"</c> mode. Capped at <see cref="SelectionLimits.MaxIds"/>.
    /// Ignored in <c>"explicit"</c> mode.
    /// </summary>
    public IReadOnlyList<Guid> ExcludeIds { get; init; } = Array.Empty<Guid>();

    // Structured / search filter (mig 164) for <c>"all"</c> mode — mirrors the
    // register's active filter so a select-all covers exactly what the register
    // shows (not the whole account). Ignored in <c>"explicit"</c> mode (those
    // ids already ARE what the user checked). Status is carried by
    // <see cref="StatusFilter"/>; these are the non-status dimensions.
    public string? Search { get; init; }
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    public decimal? AmountMin { get; init; }
    public decimal? AmountMax { get; init; }
    public Guid? SecurityId { get; init; }
    public string? Tag { get; init; }
    public Guid? CategoryId { get; init; }
}

/// <summary>
/// Response body for
/// <c>POST /api/ledgers/{ledgerId}/transactions/selection-summary</c>.
/// Drives the bulk-action footer's "N selected · Σ $X.XX" readout in
/// both selection modes — server is the source of truth so the SPA
/// doesn't need to keep parallel state and stays correct even when
/// some selected rows have been evicted from the windowed register.
/// </summary>
/// <param name="Count">Number of headers matching the selection.</param>
/// <param name="SumOnAccount">Signed sum of header amounts as observed
/// on the source account leg. For ledger-wide selections (no
/// <c>AccountId</c>) this is null — sum across mixed-currency accounts
/// is not well-defined.</param>
public sealed record SelectionSummary(int Count, decimal? SumOnAccount);

/// <summary>
/// Request body for
/// <c>POST /api/ledgers/{ledgerId}/transactions/bulk-recon-status</c>.
/// Applies <see cref="Status"/> to every header resolved by
/// <see cref="Selection"/> inside a single Postgres transaction.
/// </summary>
public sealed class BulkReconStatusRequest
{
    public SelectionRequest Selection { get; init; } = new();

    /// <summary>One of <c>uncleared</c>, <c>reconciling</c>,
    /// <c>cleared</c>. Same validation as the single-row endpoint.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// The account whose reconciliation state is being set (ADR-0082). Recon
    /// is per-account, so the status applies to each selected transaction's
    /// leg on this account — required for both explicit and all-mode
    /// selections (the register supplies the account it's showing).
    /// </summary>
    public Guid AccountId { get; init; }
}

/// <summary>
/// Response body for
/// <c>POST /api/ledgers/{ledgerId}/transactions/bulk-recon-status</c>.
/// Returns the count actually updated — equal to the predicate's
/// resolved count unless rows changed concurrently. Useful for the
/// SPA's "updated N rows" toast.
/// </summary>
public sealed record BulkReconStatusResponse(int Updated);

/// <summary>
/// Request body for
/// <c>POST /api/ledgers/{ledgerId}/transactions/bulk-delete</c>.
/// Resolves <see cref="Selection"/>, then applies the per-row
/// hard-delete-vs-soft-hide policy (manual entries with
/// <c>external_id IS NULL</c> are hard-deleted; everything else is
/// soft-hidden so re-source / re-sync doesn't resurrect them).
/// Both branches run in one Postgres transaction.
/// </summary>
public sealed class BulkDeleteRequest
{
    public SelectionRequest Selection { get; init; } = new();
}

/// <summary>
/// Response body for
/// <c>POST /api/ledgers/{ledgerId}/transactions/bulk-delete</c>.
/// </summary>
/// <param name="HardDeleted">Headers physically removed (manual
/// entries).</param>
/// <param name="SoftHidden">Headers flagged <c>is_hidden=true</c>
/// (feed / import rows).</param>
public sealed record BulkDeleteResponse(int HardDeleted, int SoftHidden);

/// <summary>
/// Request body for <c>POST /api/ledgers/{ledgerId}/transactions/bulk-unhide</c>
/// (ADR-0072 D2). The selection carries <c>StatusFilter = "hidden"</c> (the
/// Hidden view), so it scopes to soft-hidden rows.
/// </summary>
public sealed class BulkUnhideRequest
{
    public SelectionRequest Selection { get; init; } = new();
}

/// <summary>Response for bulk-unhide: number of headers un-hidden.</summary>
public sealed record BulkUnhideResponse(int Unhidden);

/// <summary>
/// Request body for
/// <c>POST /api/ledgers/{ledgerId}/transactions/{headerId}/move-account</c>
/// (ADR-0072 D3). Repoints the transaction's leg(s) on
/// <see cref="SourceAccountId"/> to <see cref="TargetAccountId"/>.
/// </summary>
public sealed class MoveAccountRequest
{
    public Guid SourceAccountId { get; init; }
    public Guid TargetAccountId { get; init; }
}

/// <summary>
/// Request body for
/// <c>POST /api/ledgers/{ledgerId}/transactions/bulk-move-account</c>
/// (ADR-0072 D3). The selection is account-scoped — its <c>AccountId</c> is the
/// source account.
/// </summary>
public sealed class BulkMoveAccountRequest
{
    public SelectionRequest Selection { get; init; } = new();
    public Guid TargetAccountId { get; init; }
}

/// <summary>Response for bulk-move-account: number of transactions moved.</summary>
public sealed record BulkMoveAccountResponse(int Moved);

/// <summary>
/// One similar-payee suggestion (slice 2c.6c — Tier 1 recall). Backs
/// <c>GET /api/ledgers/{ledgerId}/transactions/{headerId}/similar-payees</c>.
///
/// <para>Tier 1 anchors on <i>same online payee within the same
/// provider</i>: the server reads the current bank-feed row's raw
/// <c>txn_headers.payee</c> + <c>provider_key</c> and finds prior
/// approved rows from THE SAME PROVIDER where that exact raw value
/// matches. For each such prior row, the SPA gets back the user's
/// chosen <c>(payee, counterparty)</c> pair — clicking the suggestion
/// in the editor pre-fills both into the form so the user can
/// categorize a recurring bank charge in one click. Manual rows
/// (null provider_key) participate as neither anchor nor candidate.</para>
///
/// <para>The counterparty is whatever sits on the prior row's other
/// leg, relative to the anchor's money-side account: a category on an
/// ordinary expense, a real account when the user settles the charge
/// as a TRANSFER. Both are things the editor's AccountCategoryPicker
/// accepts, so both are recallable.</para>
///
/// <para>Aggregated server-side: grouped by
/// <c>(resolved_payee, counterparty_account_id)</c>, ordered by use
/// count then recency, capped at a small N. Only single-posting
/// prior rows participate — splits are excluded as Tier 1 candidates
/// (their multi-leg structure doesn't fit the one-chip = one-pair
/// shape; a future slice could surface "use prior split structure"
/// separately).</para>
/// </summary>
/// <param name="Payee">The resolved payee text the user chose on
/// prior rows (override.payee falling back to the raw bank
/// payee). What the suggestion offers to populate.</param>
/// <param name="CounterpartyAccountId">The other leg's account id on
/// the prior rows — a <c>category</c> account or, on a transfer, a
/// real one.</param>
/// <param name="UseCount">Number of prior rows where the user
/// chose this <c>(payee, counterparty)</c> pair. Suggestions sort by
/// this descending then by recency.</param>
/// <param name="LastUsedAt">The latest <c>posted_at</c> among the
/// matching prior rows. Tie-breaker.</param>
public sealed record SimilarPayeeDto(
    string Payee,
    Guid CounterpartyAccountId,
    string CounterpartyAccountName,
    int UseCount,
    DateTime LastUsedAt);

/// <summary>
/// One merge-candidate suggestion (slice 2c.6d). Backs
/// <c>GET /api/ledgers/{ledgerId}/transactions/{headerId}/merge-candidates</c>.
///
/// <para>The "Possible matches" panel in the editor renders a chip
/// per candidate. Clicking a chip pre-fills the editor with the
/// candidate's header-level fields (<see cref="Payee"/>,
/// <see cref="Memo"/>, <see cref="Tags"/>) and posting structure
/// (<see cref="Postings"/>) so the user can adopt the candidate
/// row's data wholesale and save in one PATCH. On submit, the same
/// PATCH carries <c>mergeFromHeaderId = HeaderId</c> so the server
/// stamps <c>is_merged_into</c> on the candidate row in the same
/// Postgres transaction as the target row's edits.</para>
///
/// <para>Match rule the server enforced: same ledger, posted
/// within ±7 days of the target, and the candidate's aggregated
/// amount on the target's source account exactly matches the
/// target's source-account amount. Candidate state must be
/// "settled" — <c>needs_review=false</c>, <c>is_merged_into IS
/// NULL</c>, <c>is_merge_winner=false</c>, and not hidden. Manual
/// and previously-accepted bank-fed rows both qualify; pending
/// feed rows and already-won rows do not.</para>
/// </summary>
public sealed record MergeCandidateDto(
    Guid HeaderId,
    string? Payee,
    string? Memo,
    DateTime PostedAt,
    /// <summary>Signed delta: <c>candidate.posted_at - target.posted_at</c>
    /// in whole days. Negative when the candidate is older than the
    /// target. UI shows this as "3d ago" / "in 2d" etc.</summary>
    int DaysDelta,
    IReadOnlyList<string> Tags,
    IReadOnlyList<MergeCandidatePostingDto> Postings);

/// <summary>
/// One leg of a merge candidate's posting list. Mirrors the shape
/// the editor uses for its own posting drafts so the SPA can copy
/// the candidate's postings into the editor's state directly.
/// </summary>
public sealed record MergeCandidatePostingDto(
    Guid CounterpartyAccountId,
    string CounterpartyAccountName,
    decimal Amount,
    string? LegMemo);
