using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// Pure investment-event aggregation (ADR-0080): a header's legs → one event
/// projection. Ported from the SPA's <c>investmentAggregator.test.ts</c> — the
/// single server-side copy that both the register read and MCP consume. The
/// account ids are short structural sentinels so the assertions read like the
/// ADR-0028 rules they exercise.
/// </summary>
public sealed class InvestmentEventProjectorTests
{
    private static readonly Guid Brokerage       = new("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid HoldingsSibling = new("00000000-0000-0000-0000-0000000000bb");
    private static readonly Guid InvestmentFees  = new("00000000-0000-0000-0000-0000000000cc");
    private static readonly Guid DividendIncome  = new("00000000-0000-0000-0000-0000000000dd");
    private static readonly Guid ExternalBank    = new("00000000-0000-0000-0000-0000000000ee");
    private static readonly Guid Miscellaneous   = new("00000000-0000-0000-0000-0000000000ff");
    private static readonly Guid CounterpartyLeg = new("00000000-0000-0000-0000-000000000099");

    private static InvestmentEventLeg Leg(
        int legIndex = 0,
        decimal amount = 0,
        decimal? balanceAfter = 1000,
        bool hasOverrides = false,
        string? postingRole = null,
        string? derivedAction = null,
        Guid? securityId = null,
        string? securityTicker = null,
        string? securityName = null,
        decimal? quantity = null,
        decimal? unitPrice = null,
        Guid? counterpartyAccountId = null,
        string? counterpartyAccountName = null,
        string? counterpartyAccountType = null)
        => new(
            Id: Guid.Empty,
            LegIndex: legIndex,
            Amount: amount,
            BalanceAfter: balanceAfter,
            HasOverrides: hasOverrides,
            PostingRole: postingRole,
            DerivedAction: derivedAction,
            CounterpartyId: CounterpartyLeg,
            SecurityId: securityId,
            SecurityTicker: securityTicker,
            SecurityName: securityName,
            Quantity: quantity,
            UnitPrice: unitPrice,
            CounterpartyAccountId: counterpartyAccountId,
            CounterpartyAccountName: counterpartyAccountName,
            CounterpartyAccountType: counterpartyAccountType);

    // ---- single-leg -----------------------------------------------------

    [Fact]
    public void Strips_holdings_sibling_counterparty_on_solo_buy()
    {
        var buy = Leg(
            amount: -20000, postingRole: PostingRoles.Security,
            securityId: Guid.NewGuid(), securityTicker: "IDXA", quantity: 100, unitPrice: 200,
            counterpartyAccountId: HoldingsSibling,
            counterpartyAccountName: "Brokerage Holdings", counterpartyAccountType: "investment");

        var e = InvestmentEventProjector.ProjectEvent(new[] { buy }, HoldingsSibling);

        Assert.Null(e.CounterpartyAccountId);
        Assert.Null(e.CounterpartyAccountName);
        Assert.Null(e.CounterpartyAccountType);
        Assert.Equal(-20000m, e.Amount);
        Assert.Equal("IDXA", e.SecurityTicker);
    }

    [Fact]
    public void Projects_single_income_role_into_category_slot()
    {
        var div = Leg(
            amount: 150, postingRole: PostingRoles.Income,
            counterpartyAccountId: DividendIncome,
            counterpartyAccountName: "Dividend Income", counterpartyAccountType: "category");

        var e = InvestmentEventProjector.ProjectEvent(new[] { div }, HoldingsSibling);

        // Legacy counterparty stays populated for back-compat + the new slot.
        Assert.Equal("Dividend Income", e.CounterpartyAccountName);
        Assert.Equal("Dividend Income", e.CategoryAccountName);
        Assert.Equal("category", e.CategoryAccountType);
        Assert.Null(e.TransferAccountName);
    }

    [Fact]
    public void Projects_single_transfer_role_into_transfer_slot()
    {
        var xfer = Leg(
            amount: -500, postingRole: PostingRoles.Transfer,
            counterpartyAccountId: ExternalBank,
            counterpartyAccountName: "Checking", counterpartyAccountType: "bank");

        var e = InvestmentEventProjector.ProjectEvent(new[] { xfer }, HoldingsSibling);

        Assert.Equal("Checking", e.TransferAccountName);
        Assert.Equal("bank", e.TransferAccountType);
        Assert.Null(e.CategoryAccountName);
    }

    [Fact]
    public void Projects_single_misc_expense_into_category_slot_sign_preserved()
    {
        // MD stamps postingRole='income' on both inc + exp splittypes; the
        // expense direction lives in the amount sign, not the role.
        var miscExp = Leg(
            amount: -25, postingRole: PostingRoles.Income,
            counterpartyAccountId: InvestmentFees,
            counterpartyAccountName: "Investment Fees", counterpartyAccountType: "category");

        var e = InvestmentEventProjector.ProjectEvent(new[] { miscExp }, HoldingsSibling);

        Assert.Equal("Investment Fees", e.CategoryAccountName);
        Assert.Null(e.TransferAccountName);
        Assert.Equal(-25m, e.Amount);
    }

    [Fact]
    public void Preserves_role_less_non_holdings_counterparty_on_a_single_leg()
    {
        // A plain cash-shape brokerage row (action null → postingRole null)
        // categorized to an income account. normalizeSingleLeg preserved this
        // counterparty; the unified projector must too (bare aggregateLegs blanks it).
        var plain = Leg(
            amount: 300,
            counterpartyAccountId: DividendIncome,
            counterpartyAccountName: "Dividend Income", counterpartyAccountType: "category");

        var e = InvestmentEventProjector.ProjectEvent(new[] { plain }, HoldingsSibling);

        Assert.Equal(DividendIncome, e.CounterpartyAccountId);
        Assert.Equal("Dividend Income", e.CounterpartyAccountName);
        // No role → neither slot is filled.
        Assert.Null(e.CategoryAccountName);
        Assert.Null(e.TransferAccountName);
    }

    // ---- multi-leg ------------------------------------------------------

    [Fact]
    public void Collapses_buy_plus_fee_into_one_event_with_fee_subtitle()
    {
        var principal = Leg(
            legIndex: 0, amount: -20000, balanceAfter: 30000, postingRole: PostingRoles.Security,
            securityId: Guid.NewGuid(), securityTicker: "IDXA", securityName: "Index Fund A",
            quantity: 100, unitPrice: 200,
            counterpartyAccountId: HoldingsSibling,
            counterpartyAccountName: "Brokerage Holdings", counterpartyAccountType: "investment");
        var fee = Leg(
            legIndex: 1, amount: -15, balanceAfter: 29985, postingRole: PostingRoles.Fee,
            counterpartyAccountId: InvestmentFees,
            counterpartyAccountName: "Investment Fees", counterpartyAccountType: "category");

        var e = InvestmentEventProjector.ProjectEvent(new[] { principal, fee }, HoldingsSibling);

        Assert.Equal(-20015m, e.Amount);          // summed
        Assert.Equal(29985m, e.BalanceAfter);     // highest leg_index
        Assert.Equal(15m, e.FeeAmount);           // positive for display
        Assert.Equal("IDXA", e.SecurityTicker);   // from principal
        Assert.Equal(100m, e.Quantity);
        Assert.Equal(200m, e.UnitPrice);
        // Fee is NOT promoted to the primary chip.
        Assert.Null(e.CounterpartyAccountName);
        Assert.Equal("Investment Fees", e.FeeCategoryName);
    }

    [Fact]
    public void Collapses_sell_plus_fee_positive_principal_negative_fee()
    {
        var principal = Leg(
            legIndex: 0, amount: 4500, balanceAfter: 34500, postingRole: PostingRoles.Security,
            securityId: Guid.NewGuid(), securityTicker: "IDXA", quantity: -20, unitPrice: 225,
            counterpartyAccountId: HoldingsSibling, counterpartyAccountName: "Brokerage Holdings",
            counterpartyAccountType: "investment");
        var fee = Leg(
            legIndex: 1, amount: -5, balanceAfter: 34495, postingRole: PostingRoles.Fee,
            counterpartyAccountId: InvestmentFees, counterpartyAccountName: "Investment Fees",
            counterpartyAccountType: "category");

        var e = InvestmentEventProjector.ProjectEvent(new[] { principal, fee }, HoldingsSibling);

        Assert.Equal(4495m, e.Amount);
        Assert.Equal(5m, e.FeeAmount);
        Assert.Equal(34495m, e.BalanceAfter);
        Assert.Equal("IDXA", e.SecurityTicker);
    }

    [Fact]
    public void Collapses_div_reinvest_with_no_fee_subtitle()
    {
        var divLeg = Leg(
            legIndex: 0, amount: 50, postingRole: PostingRoles.Income,
            counterpartyAccountId: DividendIncome, counterpartyAccountName: "Dividend Income",
            counterpartyAccountType: "category");
        var buyLeg = Leg(
            legIndex: 1, amount: -50, balanceAfter: 30000, postingRole: PostingRoles.Security,
            securityId: Guid.NewGuid(), securityTicker: "MMFA", securityName: "Money Market Fund A",
            quantity: 50, unitPrice: 1,
            counterpartyAccountId: HoldingsSibling, counterpartyAccountName: "Brokerage Holdings",
            counterpartyAccountType: "investment");

        var e = InvestmentEventProjector.ProjectEvent(new[] { divLeg, buyLeg }, HoldingsSibling);

        Assert.Equal(0m, e.Amount);
        Assert.Null(e.FeeAmount);
        Assert.Null(e.FeeCategoryName);
        Assert.Equal("Dividend Income", e.CounterpartyAccountName);
        Assert.Equal("MMFA", e.SecurityTicker);   // from the leg that carries it
        Assert.Equal(50m, e.Quantity);
    }

    [Fact]
    public void Collapses_three_leg_div_xfr_plus_fee()
    {
        var incLeg = Leg(
            legIndex: 0, amount: 200, postingRole: PostingRoles.Income,
            counterpartyAccountId: DividendIncome, counterpartyAccountName: "Dividend Income",
            counterpartyAccountType: "category");
        var xferLeg = Leg(
            legIndex: 1, amount: -195, balanceAfter: 29800, postingRole: PostingRoles.Transfer,
            counterpartyAccountId: ExternalBank, counterpartyAccountName: "Checking",
            counterpartyAccountType: "bank");
        var feeLeg = Leg(
            legIndex: 2, amount: -5, balanceAfter: 29795, postingRole: PostingRoles.Fee,
            counterpartyAccountId: InvestmentFees, counterpartyAccountName: "Investment Fees",
            counterpartyAccountType: "category");

        var e = InvestmentEventProjector.ProjectEvent(new[] { incLeg, xferLeg, feeLeg }, HoldingsSibling);

        Assert.Equal(0m, e.Amount);               // 200 - 195 - 5
        Assert.Equal(5m, e.FeeAmount);
        Assert.Equal(29795m, e.BalanceAfter);
        Assert.Equal("Dividend Income", e.CategoryAccountName);
        Assert.Equal("Checking", e.TransferAccountName);
        Assert.Equal("Investment Fees", e.FeeCategoryName);
        Assert.Equal("Dividend Income", e.CounterpartyAccountName);   // legacy = category
    }

    [Fact]
    public void Collapses_misc_exp_plus_fee_main_on_income_role_actual_fee_on_fee_role()
    {
        var mainExp = Leg(
            legIndex: 0, amount: -20.05m, postingRole: PostingRoles.Income,
            counterpartyAccountId: Miscellaneous, counterpartyAccountName: "Miscellaneous",
            counterpartyAccountType: "category");
        var fee = Leg(
            legIndex: 1, amount: -0.20m, balanceAfter: 28500, postingRole: PostingRoles.Fee,
            counterpartyAccountId: InvestmentFees, counterpartyAccountName: "Investment Fees",
            counterpartyAccountType: "category");

        var e = InvestmentEventProjector.ProjectEvent(new[] { mainExp, fee }, HoldingsSibling);

        Assert.Equal(-20.25m, e.Amount);
        Assert.Equal("Miscellaneous", e.CategoryAccountName);   // main expense
        Assert.Equal("Investment Fees", e.FeeCategoryName);      // separate fee
        Assert.Equal(0.20m, e.FeeAmount);
    }

    [Fact]
    public void Falls_back_to_leg_security_id_on_qty_zero_rows()
    {
        // MD pins a security on the qty=0 cash leg of an income pair so the row
        // joins per-security queries; the ticker/name must surface, qty stays null.
        var incLeg = Leg(
            legIndex: 0, amount: 30.57m, postingRole: PostingRoles.Income,
            securityId: Guid.NewGuid(), securityTicker: "ETFA", securityName: "Index ETF A",
            quantity: null, unitPrice: null,
            counterpartyAccountId: DividendIncome, counterpartyAccountName: "Interest Received",
            counterpartyAccountType: "category");

        var e = InvestmentEventProjector.ProjectEvent(new[] { incLeg }, HoldingsSibling);

        Assert.Equal("ETFA", e.SecurityTicker);
        Assert.Equal("Index ETF A", e.SecurityName);
        Assert.Null(e.Quantity);
        Assert.Null(e.UnitPrice);
    }

    // ---- derived-Xfr target split (parent aggregate) --------------------

    [Fact]
    public void Projects_derived_xfr_counterparty_on_the_split_parent_aggregate()
    {
        // A paycheck's 401(k) target legs carry postingRole=null + derivedAction='Xfr'.
        // Without the derived-Xfr fallback the parent's "Show other side" would have
        // no counterparty. Both the legacy chip AND the transfer slot must resolve.
        var deferral = Leg(
            legIndex: 0, amount: 1137.48m, derivedAction: "Xfr",
            counterpartyAccountId: ExternalBank, counterpartyAccountName: "Paycheck",
            counterpartyAccountType: "bank");
        var match = Leg(
            legIndex: 1, amount: 299.34m, derivedAction: "Xfr",
            counterpartyAccountId: ExternalBank, counterpartyAccountName: "Paycheck",
            counterpartyAccountType: "bank");

        var e = InvestmentEventProjector.ProjectEvent(new[] { deferral, match }, HoldingsSibling);

        Assert.Equal(ExternalBank, e.CounterpartyAccountId);
        Assert.Equal(ExternalBank, e.TransferAccountId);
        Assert.Equal(1436.82m, e.Amount);
    }

    // ---- no-holdings fallback -------------------------------------------

    [Fact]
    public void Collapses_group_even_when_brokerage_has_no_holdings_sibling()
    {
        var a = Leg(legIndex: 0, amount: -100);
        var b = Leg(legIndex: 1, amount: -10);

        var e = InvestmentEventProjector.ProjectEvent(new[] { a, b }, holdingsSiblingId: null);

        Assert.Equal(-110m, e.Amount);
    }

    [Fact]
    public void Preserves_counterparty_on_single_leg_when_holdings_id_is_null()
    {
        // No holdings id → no stripping is possible; the counterparty must survive.
        var single = Leg(
            counterpartyAccountId: HoldingsSibling, counterpartyAccountName: "Brokerage Holdings",
            counterpartyAccountType: "investment");

        var e = InvestmentEventProjector.ProjectEvent(new[] { single }, holdingsSiblingId: null);

        Assert.Equal(HoldingsSibling, e.CounterpartyAccountId);
        Assert.Equal("Brokerage Holdings", e.CounterpartyAccountName);
    }

    // ---- guards ---------------------------------------------------------

    [Fact]
    public void Throws_on_empty_legs()
    {
        Assert.Throws<ArgumentException>(() =>
            InvestmentEventProjector.ProjectEvent(Array.Empty<InvestmentEventLeg>(), HoldingsSibling));
    }
}
