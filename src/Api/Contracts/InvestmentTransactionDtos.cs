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
public sealed class CreateInvestmentTransactionRequest
{
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

    /// <summary>Optional memo at the header level.</summary>
    public string? Memo { get; init; }

    /// <summary>Optional check# / external reference. On investment
    /// txns this commonly carries OFX values like <c>Auto</c> /
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

    /// <summary>The trade's money — the actual cash paid/received, at 2
    /// decimals. AUTHORITATIVE wherever an Amount is carried (ADR-0073):
    /// on buy / sell / buyx / sellx / dividend_reinvest it is the
    /// principal, and the per-share <c>unit_price</c> is DERIVED from it
    /// (<c>amount ÷ |shares|</c>), not the reverse — so the stored/shown
    /// price reconciles to the money exactly and no sub-cent amount leaks
    /// onto a leg. On dividend_cash / transfer / misc it is the direct
    /// signed cash impact. When omitted on a share-trade action the server
    /// falls back to <c>round(shares × price, 2)</c>.</summary>
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

    /// <summary>Optional fee category counterparty. When set,
    /// <see cref="FeeAmount"/> must also be set.</summary>
    public Guid? FeeAccountId { get; init; }

    /// <summary>Positive fee amount. Required when
    /// <see cref="FeeAccountId"/> is set; rejected otherwise.</summary>
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
/// rule — there's no per-leg patch surface. Editing a Buy's fee from
/// $0.89 to $1.00 reshapes the entire legs list under the same
/// header_id.
/// </remarks>
public sealed class PatchInvestmentTransactionRequest
{
    public Guid? BrokerageAccountId { get; init; }
    public DateTime? PostedAt { get; init; }
    public string? Action { get; init; }
    public string? Payee { get; init; }
    public string? Memo { get; init; }
    public string? CheckNumber { get; init; }
    public DateTime? TransactedAt { get; init; }
    public Guid? SecurityId { get; init; }
    public decimal? Shares { get; init; }
    public decimal? Price { get; init; }
    public decimal? Amount { get; init; }
    public Guid? CategoryAccountId { get; init; }
    public Guid? TransferAccountId { get; init; }
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
    /// .MergeFromHeaderId</c>). When set, the row being PATCHed (the URL
    /// <c>headerId</c>) is the LOSER: it folds into this candidate (the
    /// WINNER/survivor). The user picked the canonical row from the
    /// editor's "possible matches" panel; the fresh imported/synced row
    /// vanishes from the register (its <c>external_id</c> is preserved so
    /// future syncs dedup against it), and the winner adopts the loser's
    /// posted date. A merge-only PATCH carries just this field — no other
    /// fields are read (the loser is tombstoned, not reshaped).
    /// </summary>
    public Guid? MergeFromHeaderId { get; init; }
}

/// <summary>
/// One "possible match" for an investment merge, returned by
/// <c>GET /api/ledgers/{ledgerId}/investment-transactions/{headerId}/merge-candidates</c>.
/// Rendered as a chip in the editor's merge panel; picking one folds the
/// edited row into this candidate. Shaped for the chip's one-line summary
/// (date · action · ticker · shares · amount), unlike the bank
/// <c>MergeCandidateDto</c> which carries a posting list.
/// </summary>
/// <param name="HeaderId">The candidate (winner-to-be) header id — sent back as
///   <see cref="PatchInvestmentTransactionRequest.MergeFromHeaderId"/>.</param>
/// <param name="PostedAt">Effective (override-aware) posted date.</param>
/// <param name="DayDelta">Signed day offset from the edited row (for the chip
///   subtitle: "3 days later").</param>
/// <param name="Action">Catalog action (buy / sell / buyx / …).</param>
/// <param name="SecurityTicker">The matched security's ticker.</param>
/// <param name="Shares">Signed share quantity on the candidate's holdings leg.</param>
/// <param name="UnitPrice">Per-share price on the candidate.</param>
/// <param name="Amount">Signed holdings-leg amount (the trade principal).</param>
/// <param name="Payee">Effective payee, for disambiguation.</param>
public sealed record InvestmentMergeCandidateDto(
    Guid HeaderId,
    DateTime PostedAt,
    int DayDelta,
    string? Action,
    string? SecurityTicker,
    decimal? Shares,
    decimal? UnitPrice,
    decimal Amount,
    string? Payee);

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
