namespace Coffer.Domain.Investment;

/// <summary>
/// Posting-shape builders for investment transactions. Pure functions
/// that emit <see cref="InvestmentLegSpec"/> pairs — no persistence,
/// no IO. Callers translate to their concrete leg row / entity type
/// before insert.
/// <para>
/// The four builders cover every shape an ADR-0027 investment txn
/// can take, paired with the splittype each consumes:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Builder</term><description>MD splittype it represents</description>
///   </listheader>
///   <item><term><see cref="BuildSecPair"/></term>
///     <description><c>sec</c> — brokerage cash ↔ Holdings sibling</description></item>
///   <item><term><see cref="BuildCategoryPair"/></term>
///     <description><c>inc</c>, <c>exp</c>, or <c>fee</c> — brokerage cash ↔ category account</description></item>
///   <item><term><see cref="BuildXferPair"/></term>
///     <description><c>xfr</c> — brokerage cash ↔ external account</description></item>
/// </list>
/// <para>
/// Plus <see cref="BuildHoldingsImpact"/> for the share-position side
/// effect that share-acquiring / share-disposing actions emit.
/// </para>
/// </summary>
public static class InvestmentPostings
{
    /// <summary>
    /// Build a sec posting: brokerage cash side ↔ Holdings sibling
    /// side. Holdings-side leg carries <c>security_id</c>, quantity,
    /// unit_price; cash-side leg has those NULL. Both legs stamp
    /// <c>posting_role='security'</c>.
    /// </summary>
    /// <param name="brokerageAccountId">The user-visible brokerage
    ///   account (cash side).</param>
    /// <param name="holdingsAccountId">The system-managed Holdings
    ///   sibling sub-account (per ADR-0019).</param>
    /// <param name="securityId">The security being acquired/disposed.</param>
    /// <param name="cashAmount">Signed cash impact on the brokerage
    ///   (negative on buy/buyx, positive on sell/sellx, 0 on
    ///   self-referential xfrs and zero-qty basis adjustments).</param>
    /// <param name="holdingsAmount">Signed counter-amount on the
    ///   Holdings side (typically -<paramref name="cashAmount"/>;
    ///   diverges on self-ref xfrs where the brokerage cash is zeroed
    ///   but the share-side delta is preserved).</param>
    /// <param name="quantity">Signed share delta on the holdings
    ///   side (positive for acquire, negative for dispose).</param>
    /// <param name="unitPrice">Per-share price (always positive — sign
    ///   lives in <paramref name="quantity"/> and amount).</param>
    public static InvestmentPosting BuildSecPair(
        Guid brokerageAccountId,
        Guid holdingsAccountId,
        Guid securityId,
        decimal cashAmount,
        decimal holdingsAmount,
        decimal quantity,
        decimal unitPrice)
    {
        var cash = new InvestmentLegSpec(
            AccountId:    brokerageAccountId,
            Amount:       cashAmount,
            PostingRole:  PostingRoles.Security,
            SecurityId:   null,
            Quantity:     null,
            UnitPrice:    null);

        var holdings = new InvestmentLegSpec(
            AccountId:    holdingsAccountId,
            Amount:       holdingsAmount,
            PostingRole:  PostingRoles.Security,
            SecurityId:   securityId,
            Quantity:     quantity,
            UnitPrice:    unitPrice);

        return new InvestmentPosting(cash, holdings);
    }

    /// <summary>
    /// Build a category posting: brokerage cash side ↔ category
    /// account side. <see cref="PostingRoles.Income"/> for inc/exp
    /// splittypes (the "main category" leg of div/divr/divx/misc),
    /// <see cref="PostingRoles.Fee"/> for the optional fee splittype.
    /// Direction (income vs expense) on Misc lives in
    /// <paramref name="cashAmount"/>'s sign, not in the role.
    /// </summary>
    /// <param name="securityId">When non-null, stamped on the cash
    ///   side so per-security register queries include this row
    ///   (Div / DivXfr / Misc emit a <c>sec</c> split with qty=0
    ///   purely to link a security id — that id rides through to
    ///   the cash leg here).</param>
    public static InvestmentPosting BuildCategoryPair(
        Guid brokerageAccountId,
        Guid categoryAccountId,
        decimal cashAmount,
        decimal categoryAmount,
        string postingRole,
        Guid? securityId = null,
        string? legMemo = null)
    {
        if (postingRole is not (PostingRoles.Income or PostingRoles.Fee))
            throw new ArgumentException(
                $"BuildCategoryPair requires posting_role 'income' or 'fee'; got '{postingRole}'.",
                nameof(postingRole));

        var cash = new InvestmentLegSpec(
            AccountId:    brokerageAccountId,
            Amount:       cashAmount,
            PostingRole:  postingRole,
            SecurityId:   securityId,
            Quantity:     null,
            UnitPrice:    null,
            LegMemo:      legMemo);

        var category = new InvestmentLegSpec(
            AccountId:    categoryAccountId,
            Amount:       categoryAmount,
            PostingRole:  postingRole,
            SecurityId:   null,
            Quantity:     null,
            UnitPrice:    null,
            LegMemo:      legMemo);

        return new InvestmentPosting(cash, category);
    }

    /// <summary>
    /// Build an xfr posting: brokerage cash side ↔ external account
    /// side. Both legs stamp <c>posting_role='transfer'</c>.
    /// </summary>
    public static InvestmentPosting BuildXferPair(
        Guid brokerageAccountId,
        Guid otherAccountId,
        decimal brokerageAmount,
        decimal otherAmount,
        string? legMemo = null)
    {
        var cash = new InvestmentLegSpec(
            AccountId:    brokerageAccountId,
            Amount:       brokerageAmount,
            PostingRole:  PostingRoles.Transfer,
            LegMemo:      legMemo);

        var other = new InvestmentLegSpec(
            AccountId:    otherAccountId,
            Amount:       otherAmount,
            PostingRole:  PostingRoles.Transfer,
            LegMemo:      legMemo);

        return new InvestmentPosting(cash, other);
    }

    /// <summary>
    /// Compute the holdings/lot side effect for an action, given the
    /// resolved security quantity, share price, and total commission
    /// from any fee legs. Returns <c>null</c> for actions that don't
    /// touch holdings (div_cash, divx, transfer, misc).
    /// </summary>
    /// <remarks>
    /// Acquiring actions (buy/buyx/divr) return a delta with
    /// <see cref="InvestmentHoldingsImpact.NewLot"/> set; the lot's
    /// unit cost is <c>(secPrice + totalCommission) / quantity</c>
    /// (placeholder — recompute overrides per
    /// <c>is_trade_commission</c>). Disposing actions (sell/sellx)
    /// return a delta with <see cref="InvestmentHoldingsImpact.NewLot"/>
    /// = <c>null</c>; FIFO lot consumption runs in the recompute
    /// function on save.
    /// </remarks>
    public static InvestmentHoldingsImpact? BuildHoldingsImpact(
        string action,
        Guid holdingsAccountId,
        Guid securityId,
        decimal quantity,
        decimal sharePrice,
        decimal totalCommission,
        DateTimeOffset asOf)
    {
        if (!LedgerActions.TouchesHoldings(action)) return null;

        if (LedgerActions.AcquiresShares(action))
        {
            var costBasis = sharePrice + totalCommission;
            var newLot = new InvestmentLotSpec(
                Quantity:   quantity,
                UnitCost:   quantity != 0m ? costBasis / quantity : 0m,
                AcquiredAt: asOf);

            return new InvestmentHoldingsImpact(
                HoldingsAccountId: holdingsAccountId,
                SecurityId:        securityId,
                QuantityDelta:     quantity,
                CostBasisDelta:    costBasis,
                AsOf:              asOf,
                NewLot:            newLot);
        }

        // Disposing: holdings quantity decreases; cost-basis
        // reduction left to the recompute function's FIFO pass
        // (ADR-0018 rule 4 / migration 054).
        return new InvestmentHoldingsImpact(
            HoldingsAccountId: holdingsAccountId,
            SecurityId:        securityId,
            QuantityDelta:     quantity,
            CostBasisDelta:    0m,
            AsOf:              asOf,
            NewLot:            null);
    }
}
