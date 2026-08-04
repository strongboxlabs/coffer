namespace Coffer.Domain.Investment;

/// <summary>
/// Collapses the legs of one investment header (on one account) into a single
/// investment-event projection — the ONE server-side copy of the aggregation
/// that was previously duplicated in the SPA's <c>investmentAggregator.ts</c>
/// (ADR-0080). Both the register read and the MCP <c>activity</c> tool consume
/// it, so the "what is this investment event" question has a single answer.
/// </summary>
/// <remarks>
/// <para>Field contract is locked in ADR-0028 §2. Anchoring rules, in order:</para>
/// <list type="bullet">
///   <item><b>Amount</b> — sum of every leg's cash impact.</item>
///   <item><b>Balance</b> — the balance_after of the highest-<c>LegIndex</c> leg
///     (the leg the balance trigger processed last for this account).</item>
///   <item><b>Security · ticker · name · qty@price</b> — the <c>security</c>-role
///     leg that carries a quantity (the holdings side of the posting). Falls back
///     to any leg's security id/ticker/name for the qty=0 shapes (Div/DivXfr/Misc,
///     where MD pins the security on the cash leg); qty@price stay null there.</item>
///   <item><b>Category / transfer / fee slots</b> — the <c>income</c> / <c>transfer</c>
///     / <c>fee</c>-role leg respectively (MD guarantees at most one per role per
///     event, so no ordering tie-break). The Holdings-sibling counterparty is
///     structural noise and is skipped for slot classification.</item>
///   <item><b>Derived-Xfr fallback</b> — an ADR-0036 target-split leg carries
///     <c>PostingRole=null</c> but <c>DerivedAction='Xfr'</c>; treat it as the
///     transfer leg so the collapsed row still projects a counterparty.</item>
/// </list>
/// <para>Pure and order-tolerant except where LegIndex is the documented anchor;
/// a single-leg event is the degenerate case (this subsumes the SPA's separate
/// <c>normalizeSingleLeg</c> path).</para>
/// </remarks>
public static class InvestmentEventProjector
{
    // mig 108: derived_action = COALESCE(header.action, 'Xfr' when the leg's
    // counterparty sits on an asset-shaped account). Capitalized, distinct from
    // the lowercase LedgerActions header values, so it's a literal not a constant.
    private const string DerivedXfr = "Xfr";

    /// <summary>
    /// Project one header's legs (on one account) into a single event. Legs may
    /// arrive in any order; the highest-<c>LegIndex</c> leg anchors the balance.
    /// </summary>
    /// <param name="legs">The 1..N legs of the event on the account being read.
    /// Must be non-empty.</param>
    /// <param name="holdingsSiblingId">The brokerage's
    /// <c>accounts.holdings_account_id</c>. When null, Holdings-sibling stripping
    /// is skipped (the collapse still runs) — a defensive path for missing data.</param>
    public static InvestmentEventProjection ProjectEvent(
        IReadOnlyList<InvestmentEventLeg> legs,
        Guid? holdingsSiblingId)
    {
        ArgumentNullException.ThrowIfNull(legs);
        if (legs.Count == 0)
            throw new ArgumentException("An investment event must have at least one leg.", nameof(legs));

        var canonical = legs[0];

        decimal amountSum = 0;
        decimal? lastBalance = canonical.BalanceAfter;
        var highestLegIndex = canonical.LegIndex;
        var hasAnyOverride = false;
        InvestmentEventLeg? qtyPriceLeg = null;
        Guid? securityIdFallback = null;
        string? securityTickerFallback = null;
        string? securityNameFallback = null;
        InvestmentEventLeg? categoryLeg = null;
        InvestmentEventLeg? transferLeg = null;
        InvestmentEventLeg? feeLeg = null;

        foreach (var leg in legs)
        {
            amountSum += leg.Amount;
            if (leg.HasOverrides) hasAnyOverride = true;

            // Balance after the WHOLE event = balance_after on the highest
            // LegIndex leg. Order-independent semantic; the index pick is the
            // implementation (ADR-0028).
            if (leg.LegIndex >= highestLegIndex)
            {
                highestLegIndex = leg.LegIndex;
                lastBalance = leg.BalanceAfter;
            }

            // Security sourcing runs BEFORE the Holdings skip: a principal Buy's
            // security leg IS the Holdings-sibling counterparty, but it still
            // drives the ticker/qty display.
            if (qtyPriceLeg is null && leg.PostingRole == PostingRoles.Security && leg.Quantity is not null)
                qtyPriceLeg = leg;
            if (securityIdFallback is null && leg.SecurityId is not null)
            {
                securityIdFallback = leg.SecurityId;
                securityTickerFallback = leg.SecurityTicker;
                securityNameFallback = leg.SecurityName;
            }

            // Holdings-sibling legs are structural noise; skip counterparty
            // classification. A leg with no counterparty account can't fill a slot.
            if (holdingsSiblingId is not null && leg.CounterpartyAccountId == holdingsSiblingId) continue;
            if (leg.CounterpartyAccountId is null) continue;

            // Classify by posting_role — the marker IS the truth (ADR-0027).
            // MD guarantees at most one leg per role per event.
            if (leg.PostingRole == PostingRoles.Income) { categoryLeg ??= leg; continue; }
            if (leg.PostingRole == PostingRoles.Transfer) { transferLeg ??= leg; continue; }
            if (leg.PostingRole == PostingRoles.Fee) { feeLeg ??= leg; continue; }

            // ADR-0036 target split: PostingRole is null (trigger gates it on
            // header.action, null for cash-shape headers) but DerivedAction='Xfr'
            // marks an asset-shaped counterparty — treat as the transfer leg so
            // the read-only parent's "Show other side" has a counterparty.
            if (transferLeg is null && leg.DerivedAction == DerivedXfr) transferLeg = leg;
        }

        // Legacy primary counterparty: category (semantic primary) then transfer.
        var primaryLeg = categoryLeg ?? transferLeg;

        // Which leg fills the legacy counterparty ACCOUNT chip:
        //   * the category/transfer slot leg when present (both single + multi);
        //   * for a role-less SINGLE leg, the leg itself — the SPA's
        //     normalizeSingleLeg preserves a plain cash-shape row's counterparty
        //     (a categorized brokerage deposit), which bare aggregateLegs would
        //     blank. Skipped when that lone leg is the Holdings sibling (stripped).
        //   * multi-leg with no slot (Buy+Fee) → null, so the fee/security
        //     counterparty is never promoted to the primary chip (aggregateLegs).
        var isCanonicalHoldings = holdingsSiblingId is not null
            && canonical.CounterpartyAccountId == holdingsSiblingId;
        var counterpartyLeg = primaryLeg
            ?? (legs.Count == 1 && !isCanonicalHoldings ? canonical : null);

        // Prefer the qty-carrying security leg for full ticker/name + qty@price;
        // fall back to any security-id-carrying leg for ticker/name only.
        var securityId = qtyPriceLeg?.SecurityId ?? securityIdFallback;
        var securityTicker = qtyPriceLeg?.SecurityTicker ?? securityTickerFallback;
        var securityName = qtyPriceLeg?.SecurityName ?? securityNameFallback;

        return new InvestmentEventProjection(
            Amount: amountSum,
            BalanceAfter: lastBalance,
            HasOverrides: hasAnyOverride,
            CounterpartyId: counterpartyLeg?.CounterpartyId ?? canonical.CounterpartyId,
            SecurityId: securityId,
            SecurityTicker: securityTicker,
            SecurityName: securityName,
            Quantity: qtyPriceLeg?.Quantity,
            UnitPrice: qtyPriceLeg?.UnitPrice,
            CounterpartyAccountId: counterpartyLeg?.CounterpartyAccountId,
            CounterpartyAccountName: counterpartyLeg?.CounterpartyAccountName,
            CounterpartyAccountType: counterpartyLeg?.CounterpartyAccountType,
            CategoryAccountId: categoryLeg?.CounterpartyAccountId,
            CategoryAccountName: categoryLeg?.CounterpartyAccountName,
            CategoryAccountType: categoryLeg?.CounterpartyAccountType,
            TransferAccountId: transferLeg?.CounterpartyAccountId,
            TransferAccountName: transferLeg?.CounterpartyAccountName,
            TransferAccountType: transferLeg?.CounterpartyAccountType,
            // MD allows at most one fee posting per event; positive for display
            // (the sign is implied by "fee").
            FeeAmount: feeLeg is null ? null : Math.Abs(feeLeg.Amount),
            FeeCategoryId: feeLeg?.CounterpartyAccountId,
            FeeCategoryName: feeLeg?.CounterpartyAccountName);
    }
}
