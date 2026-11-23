using System.Text.Json.Serialization;

namespace Coffer.Api.Contracts;

/// <summary>
/// Request body for
/// <c>POST /api/ledgers/{ledgerId}/investment-transactions</c>
/// (ADR-0029). One investment txn — multi-posting on the wire, but
/// the editor speaks the user-facing shape (action × fields), not
/// the per-leg shape. Server builds the legs from this DTO using
/// <c>Coffer.Domain.Investment.InvestmentPostings.Build*Pair</c>.
/// </summary>
/// <remarks>
/// Which fields are required is driven by <see cref="Action"/> — see
/// the action × field matrix in ADR-0029. Missing-required-field
/// rejections come back as structured 422 codes
/// (<c>investment-txn-{field}-required</c>).
/// <para>
/// Sign convention for the brokerage-cash side amount (cash impact):
/// negative = outflow, positive = inflow. Per ADR-0027:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="LedgerActions.Buy"/> /
///     <see cref="LedgerActions.Sell"/>: cash impact derived from
///     <see cref="Shares"/> × <see cref="Price"/> + fee.</description></item>
///   <item><description><see cref="LedgerActions.BuyXfr"/> /
///     <see cref="LedgerActions.SellXfr"/>: brokerage cash nets to
///     zero (sec + xfr legs cancel); the transfer leg carries the
///     amount.</description></item>
///   <item><description><see cref="LedgerActions.DividendCash"/>:
///     <see cref="Amount"/> is the dividend value direct from user
///     input (positive = cash arriving).</description></item>
///   <item><description><see cref="LedgerActions.DividendReinvest"/>:
///     brokerage cash nets to zero (inc + sec pairs cancel).</description></item>
///   <item><description><see cref="LedgerActions.DivXfr"/>:
///     brokerage cash nets to zero (inc + xfr + fee legs cancel).</description></item>
///   <item><description><see cref="LedgerActions.Transfer"/>:
///     <see cref="Amount"/> is the brokerage-side cash impact
///     (signed).</description></item>
///   <item><description><see cref="LedgerActions.Misc"/>:
///     <see cref="Amount"/> is the brokerage-side cash impact;
///     sign discriminates income (positive) vs expense
///     (negative).</description></item>
/// </list>
/// </remarks>
public sealed class CreateInvestmentTransactionRequest : IInvestmentWriteNotes
{
    private readonly string? _memo;

    /// <summary>The user-visible brokerage account this txn belongs
    /// to. Must be of <c>account_type='investment'</c> and live in
    /// the ledger named in the URL.</summary>
    public Guid BrokerageAccountId { get; init; }

    /// <summary>ISO-8601 UTC timestamp. Required.</summary>
    public DateTime PostedAt { get; init; }

    /// <summary>One of the catalog actions: <c>buy</c>, <c>buyx</c>,
    /// <c>sell</c>, <c>sellx</c>, <c>dividend_cash</c>,
    /// <c>dividend_reinvest</c>, <c>divx</c>, <c>transfer</c>,
    /// <c>misc</c> (ADR-0027), or <c>transfer_shares</c> — an in-kind
    /// share move between two investment accounts (ADR-0065).</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>Optional payee — same semantics as the bank
    /// transactions endpoint.</summary>
    public string? Payee { get; init; }

    /// <summary>The event's memo: one per event, which the server places on the
    /// brokerage leg of a one-posting event and on the header of a multi-posting one.
    /// Presence-tracked (<see cref="IInvestmentWriteNotes"/>): omitted leaves it, null
    /// or blank clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Memo
    {
        get => _memo;
        init { _memo = value; HasMemo = true; }
    }

    /// <summary>True when the body carried <c>memo</c>, whatever its value.</summary>
    [JsonIgnore] public bool HasMemo { get; private init; }

    /// <summary>Optional check# / external reference. On investment
    /// txns this can carry OFX values like <c>Auto</c> /
    /// <c>EXfr</c> / <c>Xfr</c>; manual entry can also leave it
    /// numeric or null.</summary>
    public string? CheckNumber { get; init; }

    /// <summary>Optional separate tax/transaction date when it
    /// differs from <see cref="PostedAt"/>.</summary>
    public DateTime? TransactedAt { get; init; }

    // ---------- Action-driven content ----------

    /// <summary>The security being traded / paying the dividend.
    /// Required on every action except <see cref="LedgerActions.Transfer"/>;
    /// optional on <see cref="LedgerActions.Misc"/> (e.g. dividend
    /// stamped as misc-income against a specific security for
    /// drill-in queries).</summary>
    public Guid? SecurityId { get; init; }

    /// <summary>Signed share delta. Required on
    /// buy / buyx / sell / sellx / dividend_reinvest. Positive on
    /// acquire, negative on dispose.</summary>
    public decimal? Shares { get; init; }

    /// <summary>Per-share price (positive). Required on
    /// buy / buyx / sell / sellx / dividend_reinvest.</summary>
    public decimal? Price { get; init; }

    /// <summary>The trade's money — the actual cash paid/received, in the
    /// brokerage's currency and at its places (ADR-0114 D15): a typed figure
    /// with more places is refused with <c>amount-places-exceed-currency</c>,
    /// never rounded. AUTHORITATIVE wherever an Amount is carried (ADR-0073):
    /// on buy / sell / buyx / sellx / dividend_reinvest it is the
    /// principal, and the per-share <c>unit_price</c> is DERIVED from it
    /// (<c>amount ÷ |shares|</c>), not the reverse — so the stored/shown
    /// price reconciles to the money exactly and no amount finer than the
    /// currency leaks onto a leg. On dividend_cash / transfer / misc it is the
    /// direct signed cash impact. When omitted on a share-trade action the
    /// server falls back to shares × price, rounded once at the brokerage's
    /// places, half away from zero.</summary>
    public decimal? Amount { get; init; }

    /// <summary>The income or expense category leg's counterparty.
    /// Required on dividend_cash / dividend_reinvest / divx / misc.
    /// On Misc, the user's choice + amount sign discriminates
    /// income vs expense; the category-account's kind doesn't
    /// have to match.</summary>
    public Guid? CategoryAccountId { get; init; }

    /// <summary>The destination account on transfer-shape actions.
    /// Required on buyx / sellx / divx / transfer.</summary>
    public Guid? TransferAccountId { get; init; }

    /// <summary>The transfer account's own leg, signed, in that account's
    /// currency, when <see cref="TransferAccountId"/> is a real account in
    /// another currency than the brokerage's (ADR-0114 D13, ADR-0029), on
    /// <see cref="LedgerActions.Transfer"/>, <see cref="LedgerActions.BuyXfr"/>,
    /// <see cref="LedgerActions.SellXfr"/> and <see cref="LedgerActions.DivXfr"/>;
    /// on the last three it pairs with the trade amount, not the fee.
    /// Required there, nonzero and opposite in sign to the brokerage's leg of
    /// the transfer pair (<c>investment-txn-transfer-amount-required</c>,
    /// <c>investment-txn-transfer-amounts-nonzero</c>,
    /// <c>investment-txn-transfer-amounts-sign</c>), and checked at the transfer
    /// account's places. Like the places rule, the nonzero and sign rules check
    /// only a pair the write changes: a pair resent equal to its two stored
    /// legs, on the same accounts, passes them. Dropped on a category or a
    /// same-currency transfer, whose leg mirrors the brokerage's.</summary>
    public decimal? TransferAmount { get; init; }

    /// <summary>Optional fee category counterparty. When set,
    /// <see cref="FeeAmount"/> must also be set.</summary>
    public Guid? FeeAccountId { get; init; }

    /// <summary>Positive fee amount, in the brokerage's currency and at its
    /// places (ADR-0114 D15). Required when <see cref="FeeAccountId"/> is set;
    /// rejected otherwise.</summary>
    public decimal? FeeAmount { get; init; }

    /// <summary>
    /// ADR-0031 Phase 3d.1: optional provider-mapping hint. When
    /// supplied alongside <see cref="SecurityId"/>, the endpoint
    /// upserts <c>provider_security_mappings (ledger, provider_key,
    /// provider_security_id) → security_id</c> so the next sync of
    /// the same ticker auto-resolves to the same security without
    /// prompting.
    /// </summary>
    public ProviderSecurityHint? ProviderSecurityHint { get; init; }

    /// <summary>
    /// The event's tags — the same dictionary as the bank create surface
    /// (<see cref="CreateTransactionRequest.Tags"/>): case-insensitive
    /// create-on-first-use, first casing wins. Omitted / null / <c>[]</c> all
    /// produce an untagged transaction.
    /// </summary>
    /// <remarks>
    /// One tag set per event, placed like <see cref="Memo"/>: on the brokerage
    /// leg of a one-posting event, on the header of a multi-posting one.
    /// </remarks>
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// Request body for
/// <c>POST /api/ledgers/{ledgerId}/in-kind-transfers/convert</c> (ADR-0065 D4):
/// the sell (sell/sellx) + buy (buy/buyx) headers — surfaced by the
/// <c>find_in_kind_transfer_candidates</c> detection — to replace with a single
/// in-kind <c>transfer_shares</c>. Reviewed against a brokerage statement first.
/// </summary>
public sealed class ConvertInKindTransferRequest
{
    public Guid SellHeaderId { get; init; }
    public Guid BuyHeaderId { get; init; }
}

/// <summary>
/// Request body for
/// <c>PATCH /api/ledgers/{ledgerId}/investment-transactions/{headerId}</c>.
/// Same shape as <see cref="CreateInvestmentTransactionRequest"/>;
/// every field is optional ("leave alone") at the wire level, but
/// the action × field matrix in ADR-0029 still applies post-patch
/// (the server validates the resulting header + legs as a whole
/// against the matrix).
/// </summary>
/// <remarks>
/// Postings are replaced wholesale on PATCH per the ADR-0025 reconcile
/// rule — there's no per-leg patch surface. Editing only a Buy's fee
/// still reshapes the entire legs list under the same header_id.
/// </remarks>
public sealed class PatchInvestmentTransactionRequest : IInvestmentWriteNotes
{
    private readonly string? _memo;

    /// <summary>
    /// When <c>true</c>, clears <c>needs_review</c> on this transaction in the
    /// same Postgres transaction as the edit — the same contract the bank PATCH
    /// has always had. Idempotent on an already-accepted row.
    /// </summary>
    /// <remarks>
    /// <para>Clearing the flag on every successful PATCH, on the reasoning that
    /// "the investment editor's only Save-pressed exit IS Accept", is true of the
    /// editor and false of the endpoint: any other client — MCP, a direct PATCH,
    /// a script fixing a typo across a ledger — would silently accept every row
    /// it touched, with no way to correct an imported row and leave it queued for
    /// review. The field makes acceptance explicit, as it is on the bank side.</para>
    ///
    /// <para>Investment conforms to bank here rather than the reverse. The
    /// alternative — extracting approve into its own endpoint on both sides —
    /// would undo a documented decision (the dedicated POST /approve route was
    /// deliberately collapsed into PATCH so the accept flow stays one
    /// round-trip) and would cost a second request on the commonest path.</para>
    ///
    /// <para>A MERGE still clears the flag regardless of this field: a
    /// folded-away row is resolved, not awaiting review, and leaving it queued
    /// keeps the sidebar's review dot lit on an account whose register has
    /// nothing to show. That is enforced server-side on both paths precisely so
    /// it cannot depend on a caller remembering.</para>
    /// </remarks>
    public bool? Approve { get; init; }

    public Guid? BrokerageAccountId { get; init; }
    public DateTime? PostedAt { get; init; }
    public string? Action { get; init; }
    public string? Payee { get; init; }

    /// <summary>
    /// The event's memo, PRESENCE-tracked unlike the other fields of this body: an
    /// omitted memo leaves it as it is, and null or blank clears it. The other
    /// fields describe the new shape wholesale, but the memo is not part of the
    /// shape, and clearing it whenever a client left it out lost it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Memo
    {
        get => _memo;
        init { _memo = value; HasMemo = true; }
    }

    /// <summary>True when the body carried <c>memo</c>, whatever its value.</summary>
    [JsonIgnore] public bool HasMemo { get; private init; }

    public string? CheckNumber { get; init; }
    public DateTime? TransactedAt { get; init; }
    public Guid? SecurityId { get; init; }
    public decimal? Shares { get; init; }
    public decimal? Price { get; init; }
    public decimal? Amount { get; init; }
    public Guid? CategoryAccountId { get; init; }
    public Guid? TransferAccountId { get; init; }
    /// <summary>The transfer account's own leg across currencies, as on create
    /// (<see cref="CreateInvestmentTransactionRequest.TransferAmount"/>). A PATCH
    /// that resends the stored leg unchanged passes the places check, and one
    /// that resends the transfer pair equal to its two stored legs, on the same
    /// accounts, passes the nonzero and sign checks too.</summary>
    public decimal? TransferAmount { get; init; }
    public Guid? FeeAccountId { get; init; }
    public decimal? FeeAmount { get; init; }
    /// <summary>
    /// ADR-0031 Phase 3d.1: optional provider-mapping hint. When
    /// supplied alongside <see cref="SecurityId"/>, the endpoint
    /// upserts <c>provider_security_mappings (ledger, provider_key,
    /// provider_security_id) → security_id</c> so the next sync of
    /// the same ticker auto-resolves to the same security without
    /// prompting. Idempotent — same security is a no-op; different
    /// security overwrites (user re-linked the ticker).
    /// </summary>
    public ProviderSecurityHint? ProviderSecurityHint { get; init; }

    /// <summary>
    /// Investment-side merge (mirrors the bank <c>PatchTransactionRequest
    /// .MergeFromHeaderId</c>). When set, the URL <c>headerId</c> is the
    /// LOSER: it folds into this candidate (the WINNER/survivor). The user
    /// picked the canonical row from the editor's "Merge candidates" strip;
    /// the fresh imported/synced row vanishes from the register (its
    /// <c>external_id</c> is preserved so future syncs dedup against it),
    /// and the winner keeps its own fields, adopting the loser's posted
    /// date. Only the merge route sets it, on a request carrying nothing
    /// else: no other field is read (the loser is tombstoned, not
    /// reshaped).
    /// </summary>
    /// <remarks>
    /// <para><b>Not settable from the wire.</b> Merging is a COMMAND and has its
    /// own route; this property survives only so that route can reuse the one
    /// repository method that performs the fold. <c>[JsonIgnore]</c> is what
    /// makes the extraction real rather than advisory — without it the old
    /// merge-by-PATCH shape keeps working, and a second way to do one thing is
    /// a second place for its invariants to drift.</para>
    /// </remarks>
    [JsonIgnore]
    public Guid? MergeFromHeaderId { get; init; }

    /// <summary>
    /// Replace the event's tag set in the same PATCH. When supplied, the event's
    /// pairings match exactly <see cref="Tags"/>, placed like the memo: on the
    /// brokerage leg of a one-posting event, on the header of a multi-posting one.
    /// <list type="bullet">
    ///   <item><c>null</c> / omitted → tags untouched.</item>
    ///   <item><c>[]</c> → all tags removed.</item>
    /// </list>
    /// Matching against the ledger dictionary is case-insensitive; the first
    /// user-supplied casing wins on insert.
    /// </summary>
    /// <remarks>
    /// Tags survive a reshape this PATCH performs: the header id is stable, the
    /// legs keep their identity where the new shape has room for them, and a
    /// change of posting count moves the event's set between the brokerage leg and
    /// the header. Editing a buy's fee does not disturb its tags.
    /// </remarks>
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// One merge candidate for an investment merge, returned by
/// <c>GET /api/ledgers/{ledgerId}/investment-transactions/{headerId}/merge-candidates</c>.
/// Rendered as a chip in the editor's "Merge candidates" strip; picking one
/// folds the edited row into this candidate on save, through the merge route,
/// and the candidate survives with its own fields. The chip reads like the candidate's row in
/// the edited brokerage's register (date · action · payee · ticker · shares ·
/// amount), so the figures it shows are that row's, taken from the register's
/// own reading of it, not from the candidate's legs.
/// </summary>
/// <param name="HeaderId">The candidate (winner-to-be) header id — sent back as
///   the merge route's <c>fromHeaderId</c>.</param>
/// <param name="PostedAt">Effective (override-aware) posted date.</param>
/// <param name="DayDelta">Signed day offset from the edited row (for the chip
///   subtitle: "3 days later").</param>
/// <param name="Action">Catalog action (buy / sell / buyx / …).</param>
/// <param name="SecurityTicker">The matched security's ticker.</param>
/// <param name="Shares">The register row's share quantity, with its sign.</param>
/// <param name="UnitPrice">Per-share price on the candidate.</param>
/// <param name="Amount">The register row's amount, with its sign.</param>
/// <param name="Payee">Effective payee, for disambiguation.</param>
/// <param name="DerivedAction">The register row's derived action: the
///   header's action, or <c>Xfr</c> for an action-less row whose other side
///   is an asset-shaped account. What the row's action chip shows.</param>
/// <param name="CounterpartyAccountId">The register row's counterparty
///   account (a cash transfer's other account).</param>
/// <param name="CounterpartyAccountName">That account's name.</param>
/// <param name="Origin">How the candidate came into the ledger, its register
///   row's <c>origin</c>: <c>manual</c>, <c>online_import</c> or
///   <c>file_import</c>. The chip's tooltip names the row a merge keeps by it:
///   the person's own entry, or a row imported earlier.</param>
public sealed record InvestmentMergeCandidateDto(
    Guid HeaderId,
    DateTime PostedAt,
    int DayDelta,
    string? Action,
    string? SecurityTicker,
    decimal? Shares,
    decimal? UnitPrice,
    decimal Amount,
    string? Payee,
    string? DerivedAction,
    Guid? CounterpartyAccountId,
    string? CounterpartyAccountName,
    string Origin);

/// <summary>
/// Optional payload field on the investment create + PATCH endpoints
/// (ADR-0031 Phase 3d.1). When present alongside a resolved
/// <c>SecurityId</c> on the request, the endpoint records the
/// (<see cref="ProviderKey"/>, <see cref="ProviderSecurityId"/>) →
/// <c>SecurityId</c> mapping so subsequent syncs of the same ticker
/// auto-resolve without re-prompting the user.
/// </summary>
public sealed record ProviderSecurityHint(
    string ProviderKey,
    string ProviderSecurityId);

/// <summary>
/// One open lot on a (brokerage, security), returned by
/// <c>GET /api/ledgers/{ledgerId}/accounts/{accountId}/securities/{securityId}/lots</c>.
/// Used by the editor's FIFO preview popover on Sell / SellX.
/// </summary>
/// <param name="LotId">Stable lot id (for future Edit-Lots / overrides).</param>
/// <param name="AcquiredAt">ISO-8601 UTC; ascending sort drives FIFO order.</param>
/// <param name="Quantity">Open quantity (always positive on an open lot).</param>
/// <param name="UnitCost">Per-share cost basis (placeholder at import time;
///   recompute may have refined this per <c>is_trade_commission</c>).</param>
public sealed record InvestmentLotDto(
    Guid LotId,
    DateTime AcquiredAt,
    decimal Quantity,
    decimal UnitCost);

/// <summary>
/// 201 Created response body for the investment-transactions POST
/// endpoint. Mirrors the bank endpoint's 201 shape — the editor
/// uses <see cref="HeaderId"/> for immediate follow-up reads.
/// </summary>
public sealed record CreateInvestmentTransactionResponse(Guid HeaderId);
