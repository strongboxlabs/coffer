using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Investment returns (ADR-0063 §D5, Track-2 historical valuations). Both the
/// money-weighted (IRR) and the true time-weighted (TWR) figure value the
/// portfolio the same way at every boundary — brokerage cash
/// (<c>account_balance_as_of</c>) + split-adjusted holdings market value (the
/// migration-172 feeder). Covers the cash-inclusive basis (a contribution left as
/// cash must not read as a loss), the TWR chain across an intermediate flow
/// (time-weighted diverges from money-weighted), and the non-positive-base null
/// path. <c>ReturnsAsync</c> had no integration coverage before this.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvestmentReturnsTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentReturnsTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Returns_value_cash_inclusive_so_an_uninvested_contribution_is_not_a_loss()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");

        // Deposit $1,000 into the brokerage; it stays as cash (never invested).
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1000m, Utc(2024, 1, 10));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 4, 10));

        // Value = cash + securities, so the $1,000 is still there at the end and the
        // start value (before the contribution) is 0.
        Assert.Equal(0m, result.StartValue);
        Assert.Equal(1000m, result.EndValue);

        // The contribution was never invested → no gain, no loss. The old
        // securities-only basis would have reported roughly -100% here.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
        Assert.Null(result.TimeWeightedUnavailableReason);
        Assert.NotNull(result.MoneyWeightedReturn);
        Assert.True(Math.Abs(result.MoneyWeightedReturn!.Value) < 1e-6,
            $"expected ~0% IRR, got {result.MoneyWeightedReturn}");
    }

    [Fact]
    public async Task Returns_time_weighted_chains_across_an_intermediate_flow()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        // Year start: deposit $1,000, buy 100 @ $10 (portfolio $1,000).
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1000m, Utc(2024, 1, 1));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 10m, Utc(2024, 1, 1));
        // Price has doubled to $20: deposit another $2,000 and buy 100 more @ $20
        // (portfolio $4,000 = 200 sh, cash 0). Pre-deposit value is $2,000.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 2000m, Utc(2024, 4, 10));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 20m, Utc(2024, 4, 10));
        // Year end: a $22 feed close → 200 × $22 = $4,400.
        await ledger.AddSecurityPriceAsync(sec, 22m, Utc(2024, 10, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));   // exactly 365 days → annualization factor 1

        Assert.Equal(0m, result.StartValue);
        Assert.Equal(4400m, result.EndValue);

        // Sub-periods: $1,000 -> $2,000 (×2.0), then base $4,000 -> $4,400 (×1.1).
        // Cumulative 2.2 over one year → 120%/yr, independent of the flow's timing.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value - 1.2) < 1e-6,
            $"expected 120% TWR, got {result.TimeWeightedReturn}");
        Assert.Null(result.TimeWeightedUnavailableReason);

        // Money-weighted is lower: the larger $2,000 was invested only through the
        // smaller-return second sub-period — the IRR/TWR divergence TWR exists to show.
        Assert.NotNull(result.MoneyWeightedReturn);
        Assert.True(result.MoneyWeightedReturn!.Value > 0
                    && result.MoneyWeightedReturn.Value < result.TimeWeightedReturn.Value,
            $"expected 0 < IRR < TWR, got IRR {result.MoneyWeightedReturn}, TWR {result.TimeWeightedReturn}");
    }

    // ---- partial coverage: empty for part of the window ---------------------
    //
    // TWR used to be voided outright by any sub-period with a non-positive base,
    // which meant an account empty at EITHER end of the window reported nothing at
    // all. On a real ledger that was six accounts of nine — every rollover source
    // (empty after) and every rollover destination (empty before) — and each of
    // them has a perfectly good time-weighted return over the stretch it held
    // money. The chain now skips the dormant stretches and annualizes over the
    // covered time, which the result carries so a partial span can't be read as a
    // full one.

    [Fact]
    public async Task Returns_time_weighted_covers_only_the_stretch_the_account_held_money()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");

        // Deposit $1,000, withdraw all $1,000 seven weeks later, then sit empty to
        // the window close.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1000m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, -1000m, Utc(2024, 2, 20));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 4, 10));

        // The money was invested for 41 days and earned nothing on it: 0% TWR over
        // 41 days. The dead tail contributes no return and no time — it does not
        // void the answer, and it must not dilute it either.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
        Assert.Null(result.TimeWeightedUnavailableReason);

        Assert.Equal(Utc(2024, 1, 10), result.TimeWeightedCoveredFrom);
        Assert.Equal(Utc(2024, 2, 20), result.TimeWeightedCoveredTo);
        Assert.NotNull(result.TimeWeightedCoveredYears);
        Assert.Equal(41.0 / 365.0, result.TimeWeightedCoveredYears!.Value, 6);
        // Materially shorter than the reported window — the whole reason the span
        // travels with the rate.
        Assert.True(
            result.TimeWeightedCoveredYears!.Value
                < (result.EndDate - result.StartDate).TotalDays / 365.0 - 0.1,
            "covered span should be well short of the reported window");

        Assert.NotNull(result.MoneyWeightedReturn);
    }

    /// <summary>
    /// The destination half, and the shape that produced the "n/a" column on the
    /// real ledger: a window that opens BEFORE the account was funded. Since-
    /// inception hides this — it anchors at the first flow, so the account is never
    /// observed empty — but an explicit fromUtc does not, and every fixed reporting
    /// window (last 5 years, since 2021) is an explicit fromUtc.
    /// </summary>
    [Fact]
    public async Task Returns_account_scope_reports_twr_from_first_funding_when_the_window_opens_earlier()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old 401(k)");
        var destination = await ledger.AddInvestmentAccountAsync("Rollover IRA");
        var holdings = destination.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 6_000m, Utc(2024, 1, 1));
        // Rolled in mid-year and invested at $10; $12 by the close → +20%.
        await ledger.AddTransactionPairAsync(destination.Id, source.Id, 6_000m, Utc(2024, 7, 1));
        await ledger.AddInvestmentBuyAsync(destination.Id, holdings, sec, 600m, 10m, Utc(2024, 7, 1));
        await ledger.AddSecurityPriceAsync(sec, 12m, Utc(2024, 12, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: destination.Id,
            fromUtc: Utc(2024, 1, 1), toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal("account", result.Scope);
        Assert.Equal(0m, result.StartValue);
        Assert.Equal(7_200m, result.EndValue);

        // 183 days invested, +20% over them. Annualizing a half-year magnifies it to
        // ~44%/yr — correct, and exactly why the covered span must be reported with
        // it rather than left to be read as a full-year figure.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(
            Math.Abs(result.TimeWeightedReturn!.Value - (Math.Pow(1.2, 365.0 / 183.0) - 1.0)) < 1e-6,
            $"expected ~{Math.Pow(1.2, 365.0 / 183.0) - 1.0:F6}, got {result.TimeWeightedReturn}");
        Assert.Null(result.TimeWeightedUnavailableReason);

        Assert.Equal(Utc(2024, 7, 1), result.TimeWeightedCoveredFrom);
        Assert.Equal(Utc(2024, 12, 31), result.TimeWeightedCoveredTo);
        Assert.Equal(183.0 / 365.0, result.TimeWeightedCoveredYears!.Value, 6);
        // The window is a full year; the coverage is half of it.
        Assert.Equal(365.0, (result.EndDate - result.StartDate).TotalDays, 3);
    }

    /// <summary>
    /// The refusal that must survive the skip rule: an account that held nothing at
    /// any point in the window has no performance to measure, and reports a null
    /// with a reason rather than a manufactured 0%.
    /// </summary>
    [Fact]
    public async Task Returns_time_weighted_is_null_when_the_account_held_nothing_all_window()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");

        // Money in and straight back out on the same instant: never invested across
        // any sub-period.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1000m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, -1000m, Utc(2024, 1, 10));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 4, 10));

        Assert.Null(result.TimeWeightedReturn);
        Assert.False(string.IsNullOrEmpty(result.TimeWeightedUnavailableReason));
        Assert.Null(result.TimeWeightedCoveredYears);
    }

    // ---- the per-account roster ---------------------------------------------
    //
    // A ledger-scope report could not say which accounts it covered, so a caller
    // had to guess — and guessing by current balance drops exactly the accounts a
    // rollover emptied. The roster answers it, and is held to one invariant: its
    // rows sum to the report they sit under.

    [Fact]
    public async Task Returns_ledger_scope_rosters_every_brokerage_including_the_emptied_ones()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old 401(k)");
        var destination = await ledger.AddInvestmentAccountAsync("Rollover IRA");

        // Funded before the window opens, then wholly rolled across inside it. The
        // source ends at zero and is invisible to any balance-based account picker.
        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2023, 6, 1));
        await ledger.AddTransactionPairAsync(destination.Id, source.Id, 10_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null,
            fromUtc: Utc(2024, 1, 1), toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal("ledger", result.Scope);
        Assert.NotNull(result.Accounts);

        var rows = result.Accounts!.ToDictionary(r => r.AccountId);
        Assert.Equal(2, rows.Count);

        // The emptied source is present, named, and carries the $10,000 it held when
        // the window opened — the value a balance-based picker would have lost.
        Assert.Equal(10_000m, rows[source.Id].StartValue);
        Assert.Equal(0m, rows[source.Id].EndValue);
        Assert.Equal("Old 401(k)", rows[source.Id].AccountName);

        Assert.Equal(0m, rows[destination.Id].StartValue);
        Assert.Equal(10_000m, rows[destination.Id].EndValue);
    }

    /// <summary>
    /// The roster's inclusion rule is "worth something at either end, or money
    /// moved" — deliberately NOT "non-zero balance now", which is the rule that
    /// loses rollover sources. An account that held nothing throughout and saw
    /// nothing move is the one case with nothing to say, and dropping it changes
    /// no column's total.
    /// </summary>
    [Fact]
    public async Task Returns_roster_omits_an_account_that_was_empty_and_untouched_throughout()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var live = await ledger.AddInvestmentAccountAsync("Live Brokerage");
        var emptied = await ledger.AddInvestmentAccountAsync("Emptied Brokerage");
        var dormant = await ledger.AddInvestmentAccountAsync("Never Used");

        await ledger.AddTransactionPairAsync(live.Id, bank.Id, 5_000m, Utc(2024, 2, 1));
        // Funded and drained inside the window: zero at both ends, but it moved.
        await ledger.AddTransactionPairAsync(emptied.Id, bank.Id, 3_000m, Utc(2024, 3, 1));
        await ledger.AddTransactionPairAsync(emptied.Id, bank.Id, -3_000m, Utc(2024, 9, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null,
            fromUtc: Utc(2024, 1, 1), toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.NotNull(result.Accounts);
        var ids = result.Accounts!.Select(r => r.AccountId).ToHashSet();

        Assert.Contains(live.Id, ids);
        // Zero at both ends, but $3,000 moved through it — it stays.
        Assert.Contains(emptied.Id, ids);
        Assert.DoesNotContain(dormant.Id, ids);

        // Dropping it cannot disturb the invariant.
        Assert.Equal(result.StartValue, result.Accounts!.Sum(r => r.StartValue));
        Assert.Equal(result.EndValue, result.Accounts!.Sum(r => r.EndValue));
    }

    [Fact]
    public async Task Returns_ledger_scope_roster_rows_sum_to_the_reported_totals()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var cash = await ledger.AddInvestmentAccountAsync("Cash Brokerage");
        var invested = await ledger.AddInvestmentAccountAsync("Invested Brokerage");
        var holdings = invested.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        // A mix that exercises both halves of the valuation: one account holding
        // plain cash, one holding securities on its sibling. Securities must be
        // credited to the BROKERAGE, not to the shadow sibling they post to.
        await ledger.AddTransactionPairAsync(cash.Id, bank.Id, 2_000m, Utc(2024, 2, 1));
        await ledger.AddTransactionPairAsync(invested.Id, bank.Id, 5_000m, Utc(2024, 2, 1));
        await ledger.AddInvestmentBuyAsync(invested.Id, holdings, sec, 500m, 10m, Utc(2024, 2, 1));
        await ledger.AddSecurityPriceAsync(sec, 14m, Utc(2024, 11, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null,
            fromUtc: Utc(2024, 1, 1), toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.NotNull(result.Accounts);
        Assert.Equal(result.StartValue, result.Accounts!.Sum(r => r.StartValue));
        Assert.Equal(result.EndValue, result.Accounts!.Sum(r => r.EndValue));

        // $2,000 cash + 500 shares at $14.
        Assert.Equal(9_000m, result.EndValue);
        var investedRow = result.Accounts!.Single(r => r.AccountId == invested.Id);
        Assert.Equal(7_000m, investedRow.EndValue);
    }

    /// <summary>
    /// The identity has to hold when a flow lands ON the window's start instant,
    /// which is not exotic: since-inception anchors the window at the first flow
    /// date, so it is the normal case. The report's start value is defined as the
    /// value BEFORE that flow, so each row must be too, or every since-inception
    /// report ships rows that visibly fail to add up.
    /// </summary>
    [Fact]
    public async Task Returns_roster_rows_sum_when_a_flow_lands_on_the_window_start()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var first = await ledger.AddInvestmentAccountAsync("Brokerage One");
        var second = await ledger.AddInvestmentAccountAsync("Brokerage Two");

        await ledger.AddTransactionPairAsync(first.Id, bank.Id, 3_000m, Utc(2024, 1, 15));
        await ledger.AddTransactionPairAsync(second.Id, bank.Id, 1_000m, Utc(2024, 6, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        // Since-inception: startDate IS 2024-01-15, the instant of the first flow.
        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(Utc(2024, 1, 15), result.StartDate);
        Assert.Equal(0m, result.StartValue);

        Assert.NotNull(result.Accounts);
        Assert.Equal(result.StartValue, result.Accounts!.Sum(r => r.StartValue));
        Assert.Equal(result.EndValue, result.Accounts!.Sum(r => r.EndValue));
        // Specifically: the funded account opens at 0, not at the $3,000 that
        // arrived on that instant.
        Assert.Equal(0m, result.Accounts!.Single(r => r.AccountId == first.Id).StartValue);
    }

    [Fact]
    public async Task Returns_account_scope_has_no_roster()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        // A one-row roster restating the report it sits in would be noise.
        Assert.Null(result.Accounts);
    }

    // ---- net contributions, un-netted ---------------------------------------
    //
    // A net figure invites a single-event story: -653,611 is equally consistent
    // with one withdrawal of that size and with 688,759 out against 35,148 in, and
    // a reader holding one salient event will bind the number to it. A real report
    // did exactly that. The parts are already classified when the net is summed,
    // so they travel with it.

    /// <summary>
    /// The reported scenario, reconstructed at the shape that produced it: a
    /// retirement account rolled out mid-window while employer contributions kept
    /// arriving and small expenses kept leaving. The net matches NO single event —
    /// which is precisely why it must not be described as one.
    /// </summary>
    [Fact]
    public async Task Returns_splits_net_contributions_into_gross_halves_by_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var plan = await ledger.AddInvestmentAccountAsync("Employer Plan");
        var ira = await ledger.AddInvestmentAccountAsync("Rollover IRA");
        var employer = await ledger.AddCategoryAsync("Employer Contribution", kind: "income");
        var fees = await ledger.AddCategoryAsync("Plan Expenses", kind: "expense");

        await ledger.AddTransactionPairAsync(plan.Id, bank.Id, 100_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(
            plan.Id, employer.Id, 5_000m, Utc(2024, 3, 1), postingRole: "transfer");
        await ledger.AddTransactionPairAsync(
            plan.Id, fees.Id, -500m, Utc(2024, 4, 1), postingRole: "transfer");
        await ledger.AddTransactionPairAsync(ira.Id, plan.Id, 90_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: plan.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        // The identity, stated as an addition so no caller has to reason about
        // which way to subtract.
        Assert.Equal(14_500m, result.NetContributions);
        Assert.Equal(105_000m, result.ContributionsIn);
        Assert.Equal(-90_500m, result.ContributionsOut);
        Assert.Equal(result.NetContributions, result.ContributionsIn + result.ContributionsOut);

        var bySource = result.ContributionsBySource.ToDictionary(s => s.Source);
        Assert.Equal(100_000m, bySource["external_accounts"].In);
        Assert.Equal(0m, bySource["external_accounts"].Out);
        Assert.Equal(5_000m, bySource["category_transfers"].In);
        Assert.Equal(-500m, bySource["category_transfers"].Out);
        Assert.Equal(0m, bySource["other_investment_accounts"].In);
        Assert.Equal(-90_000m, bySource["other_investment_accounts"].Out);

        // Every source's halves add to the whole, so a caller can drill in without
        // the parts drifting from the total.
        Assert.Equal(
            result.NetContributions,
            result.ContributionsBySource.Sum(s => s.In + s.Out));

        // The point of the exercise: the rollover is 90,000 and the net is 14,500.
        // Nothing in the response now lets those be conflated.
        Assert.NotEqual(-90_000m, result.NetContributions);
    }

    [Fact]
    public async Task Returns_ledger_scope_omits_an_internal_rollover_from_the_breakdown()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var plan = await ledger.AddInvestmentAccountAsync("Employer Plan");
        var ira = await ledger.AddInvestmentAccountAsync("Rollover IRA");

        await ledger.AddTransactionPairAsync(plan.Id, bank.Id, 100_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(ira.Id, plan.Id, 90_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        // Both ends are inside the perimeter, so the rollover is not a flow at all —
        // it must not appear as a source with offsetting halves either.
        Assert.Equal(100_000m, result.NetContributions);
        Assert.Equal(100_000m, result.ContributionsIn);
        Assert.Equal(0m, result.ContributionsOut);
        Assert.DoesNotContain(
            result.ContributionsBySource, s => s.Source == "other_investment_accounts");
    }

    [Fact]
    public async Task Returns_attributes_an_in_kind_transfer_to_its_own_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old IRA");
        var dest = await ledger.AddInvestmentAccountAsync("New IRA");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddInvestmentBuyAsync(
            source.Id, source.HoldingsAccountId!.Value, sec, 100m, 100m, Utc(2024, 1, 1));
        await ledger.AddTransferSharesAsync(
            source.Id, source.HoldingsAccountId!.Value,
            dest.Id, dest.HoldingsAccountId!.Value,
            sec, 100m, 100m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: dest.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        // Securities arriving with no cash leg anywhere are a contribution, and one
        // a reader would never find in a cash-transfer listing — so it gets named
        // rather than folded into the external total.
        var inKind = Assert.Single(
            result.ContributionsBySource, s => s.Source == "in_kind_transfers");
        Assert.Equal(10_000m, inKind.In);
        Assert.Equal(0m, inKind.Out);
        Assert.Equal(result.NetContributions, result.ContributionsIn + result.ContributionsOut);
    }

    // ---- scope-relative external flows -----------------------------------
    //
    // A transfer between two brokerages is INTERNAL to the ledger but EXTERNAL to
    // either account on its own. Classifying by counterparty account type made
    // every 'investment' counterparty internal at both scopes, so the balance step
    // landed in the return: the source reported the rollover as a catastrophic
    // loss and the destination reported it as spectacular growth. These fix the
    // rule at "outside this report's perimeter" and pin both scopes.

    /// <summary>
    /// Rolling money OUT to another brokerage is a withdrawal from the source
    /// account's own return. $10,000 in, $6,000 rolled out at mid-year, nothing
    /// gained or lost: 0% TWR. Under the type-based rule the $6,000 step-down was
    /// pure performance and this read about -60%/yr.
    /// </summary>
    [Fact]
    public async Task Returns_account_scope_counts_a_rollover_out_to_another_brokerage_as_a_withdrawal()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old 401(k)");
        var destination = await ledger.AddInvestmentAccountAsync("Rollover IRA");

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(destination.Id, source.Id, 6_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: source.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));   // 365 days from the first flow

        Assert.Equal("account", result.Scope);
        Assert.Equal(0m, result.StartValue);
        Assert.Equal(4_000m, result.EndValue);
        // $10,000 contributed less $6,000 rolled out.
        Assert.Equal(4_000m, result.NetContributions);

        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
        Assert.Null(result.TimeWeightedUnavailableReason);
        Assert.NotNull(result.MoneyWeightedReturn);
        Assert.True(Math.Abs(result.MoneyWeightedReturn!.Value) < 1e-6,
            $"expected ~0% IRR, got {result.MoneyWeightedReturn}");
    }

    /// <summary>
    /// The mirror image: money rolled IN is a contribution to the destination, not
    /// growth. $1,000 seeded + $1,000 rolled in, nothing earned → 0% TWR and
    /// $2,000 of net contributions. Under the type-based rule this read ~+100%/yr
    /// with netContributions reporting only the $1,000 that came from a bank.
    /// </summary>
    [Fact]
    public async Task Returns_account_scope_counts_a_rollover_in_from_another_brokerage_as_a_contribution()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old 401(k)");
        var destination = await ledger.AddInvestmentAccountAsync("Rollover IRA");

        await ledger.AddTransactionPairAsync(destination.Id, bank.Id, 1_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 9_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(destination.Id, source.Id, 1_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: destination.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(0m, result.StartValue);
        Assert.Equal(2_000m, result.EndValue);
        Assert.Equal(2_000m, result.NetContributions);

        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
        Assert.Null(result.TimeWeightedUnavailableReason);
    }

    /// <summary>
    /// Regression guard on the half that was already right: across the WHOLE
    /// ledger the same rollover nets to zero and must not register as a flow.
    /// Net contributions stay at the $10,000 that entered from the bank.
    /// </summary>
    [Fact]
    public async Task Returns_ledger_scope_still_treats_a_rollover_between_brokerages_as_internal()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old 401(k)");
        var destination = await ledger.AddInvestmentAccountAsync("Rollover IRA");

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(destination.Id, source.Id, 6_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal("ledger", result.Scope);
        // The $6,000 moved between two in-perimeter accounts — not a flow.
        Assert.Equal(10_000m, result.NetContributions);
        Assert.Equal(10_000m, result.EndValue);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// The perimeter includes each scoped brokerage's holdings sibling, so a
    /// trade's cash leg — which faces that sibling — is never a contribution.
    /// Without this the buy would read as a $1,000 withdrawal and the return
    /// would be nonsense. $1,000 buys 100 @ $10; the close at $12 is the only
    /// thing that moves → 20%/yr over exactly one year.
    /// </summary>
    [Fact]
    public async Task Returns_does_not_count_an_in_brokerage_trade_as_an_external_flow()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 1));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 10m, Utc(2024, 1, 1));
        await ledger.AddSecurityPriceAsync(sec, 12m, Utc(2024, 10, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        foreach (var (scopeName, scopedId) in
                 new[] { ("account", (Guid?)brokerage.Id), ("ledger", null) })
        {
            var result = await repository.ReturnsAsync(
                ledger.LedgerId, accountId: scopedId, fromUtc: null, toUtc: null,
                nowUtc: Utc(2024, 12, 31));

            Assert.Equal(1_000m, result.NetContributions);
            Assert.Equal(1_200m, result.EndValue);
            Assert.NotNull(result.TimeWeightedReturn);
            Assert.True(Math.Abs(result.TimeWeightedReturn!.Value - 0.2) < 1e-6,
                $"[{scopeName}] expected 20% TWR, got {result.TimeWeightedReturn}");
        }
    }

    /// <summary>
    /// A brokerage that has never held a position (holdings_account_id NULL — an
    /// emptied rollover stub, a CD, a sweep account) is still a brokerage: it holds
    /// cash and takes transfers. Scoping brokerages by "has a holdings sibling"
    /// dropped it from the perimeter entirely, so at ACCOUNT scope the transfer out
    /// to it must still count as a withdrawal.
    /// </summary>
    [Fact]
    public async Task Returns_account_scope_counts_a_transfer_to_a_sibling_less_brokerage_as_a_withdrawal()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Brokerage");
        var cashOnly = await ledger.AddInvestmentAccountAsync(
            "Cash-Only IRA", withHoldingsSibling: false);
        Assert.Null(cashOnly.HoldingsAccountId);

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(cashOnly.Id, source.Id, 6_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: source.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(4_000m, result.EndValue);
        Assert.Equal(4_000m, result.NetContributions);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// The same sibling-less brokerage at LEDGER scope. Two things had to be true
    /// and neither was: the transfer is internal (not a flow), AND the money is
    /// still in the portfolio afterwards. Excluding sibling-less accounts from the
    /// scope made the $6,000 leave the valuation with no flow to explain it — a
    /// phantom loss on top of the misclassification.
    /// </summary>
    [Fact]
    public async Task Returns_ledger_scope_keeps_a_sibling_less_brokerage_inside_the_perimeter()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Brokerage");
        var cashOnly = await ledger.AddInvestmentAccountAsync(
            "Cash-Only IRA", withHoldingsSibling: false);

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(cashOnly.Id, source.Id, 6_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(10_000m, result.NetContributions);
        // $4,000 left in the source + $6,000 sitting in the cash-only account.
        Assert.Equal(10_000m, result.EndValue);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// A brokerage account scope only sees flows on ITS OWN legs — a rollover
    /// between two other brokerages is invisible to it, at any amount.
    /// </summary>
    [Fact]
    public async Task Returns_account_scope_ignores_a_rollover_between_two_other_brokerages()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var watched = await ledger.AddInvestmentAccountAsync("Watched");
        var other1 = await ledger.AddInvestmentAccountAsync("Other One");
        var other2 = await ledger.AddInvestmentAccountAsync("Other Two");

        await ledger.AddTransactionPairAsync(watched.Id, bank.Id, 1_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(other1.Id, bank.Id, 50_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(other2.Id, other1.Id, 50_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: watched.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(1_000m, result.NetContributions);
        Assert.Equal(1_000m, result.EndValue);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// Unchanged behavior guard: a bank counterparty is outside the perimeter at
    /// BOTH scopes, so a withdrawal to checking is external either way. The
    /// perimeter rewrite must not have moved this.
    /// </summary>
    [Fact]
    public async Task Returns_counts_a_bank_withdrawal_as_external_at_both_scopes()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, -6_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        foreach (var (scopeName, scopedId) in
                 new[] { ("account", (Guid?)brokerage.Id), ("ledger", null) })
        {
            var result = await repository.ReturnsAsync(
                ledger.LedgerId, accountId: scopedId, fromUtc: null, toUtc: null,
                nowUtc: Utc(2024, 12, 31));

            Assert.Equal(4_000m, result.NetContributions);
            Assert.Equal(4_000m, result.EndValue);
            Assert.NotNull(result.TimeWeightedReturn);
            Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
                $"[{scopeName}] expected ~0% TWR, got {result.TimeWeightedReturn}");
        }
    }

    // ---- category counterparties: posting_role decides -------------------
    //
    // Money both ARRIVES from a category (an employer retirement contribution) and
    // is GENERATED by one (a dividend). From outside the two are identical — a
    // brokerage cash leg facing an income category — so treating the counterparty
    // TYPE as the answer is wrong whichever way it is set. Excluding every
    // category made an employer contribution read as investment skill; including
    // them would have reclassified this ledger's entire dividend and interest
    // history as contributed money, which is the far more expensive mistake.
    //
    // ADR-0027 already separates them: posting_role is the marker and the truth.

    /// <summary>
    /// A cash dividend carries posting role <c>income</c> — the portfolio generated
    /// it, so it is return, not contributed money. $1,000 in, $100 dividend paid to
    /// cash → 10%/yr with contributions still $1,000.
    /// </summary>
    [Fact]
    public async Task Returns_does_not_count_a_cash_dividend_as_a_contribution()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var income = await ledger.AddCategoryAsync("Dividends", kind: "income");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(
            brokerage.Id, income.Id, 100m, Utc(2024, 7, 1), postingRole: "income");

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(1_000m, result.NetContributions);
        Assert.Equal(1_100m, result.EndValue);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value - 0.1) < 1e-6,
            $"expected 10% TWR, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// An employer retirement contribution faces the same kind of income category
    /// as a dividend, but carries posting role <c>transfer</c> — it is outside
    /// money arriving, not earnings. $1,000 seeded, $100 contributed, nothing
    /// gained: 0% and contributions of $1,100. Counting it as return is what let
    /// $35,148 of employer money read as investment skill on a real account.
    /// </summary>
    [Fact]
    public async Task Returns_counts_an_employer_contribution_as_an_external_flow()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var employer = await ledger.AddCategoryAsync(
            "Employer Discretionary Contribution", kind: "income");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(
            brokerage.Id, employer.Id, 100m, Utc(2024, 7, 1), postingRole: "transfer");

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(1_100m, result.NetContributions);
        Assert.Equal(1_100m, result.EndValue);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR — nothing was earned, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// A fee leg (posting role <c>fee</c>) reduces the return rather than leaving
    /// the portfolio — the net-of-fees basis every performance convention uses.
    /// Treating it as a withdrawal would remove the cost from the calculation and
    /// flatter the return.
    /// </summary>
    [Fact]
    public async Task Returns_treats_a_fee_as_return_reducing_not_a_withdrawal()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var fees = await ledger.AddCategoryAsync("Investment Fees", kind: "expense");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(
            brokerage.Id, fees.Id, -100m, Utc(2024, 7, 1), postingRole: "fee");

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(1_000m, result.NetContributions);
        Assert.Equal(900m, result.EndValue);
        // The fee is a 10% loss, not a withdrawal that leaves the return untouched.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value + 0.1) < 1e-6,
            $"expected -10% TWR, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// An investment expense recorded on an investment event also carries role
    /// <c>income</c> — ADR-0027 stamps both <c>inc</c> and <c>exp</c> splittypes
    /// that way and puts direction in the SIGN, not the role. So a negative
    /// income-role posting must still reduce the return rather than read as a
    /// withdrawal.
    /// </summary>
    [Fact]
    public async Task Returns_treats_a_negative_income_role_posting_as_return_reducing()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var expense = await ledger.AddCategoryAsync("Account Maintenance", kind: "expense");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(
            brokerage.Id, expense.Id, -100m, Utc(2024, 7, 1), postingRole: "income");

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(1_000m, result.NetContributions);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value + 0.1) < 1e-6,
            $"expected -10% TWR, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// The symmetric half: money leaving to an EXPENSE category as a
    /// <c>transfer</c> is a withdrawal, not a loss. Direction does not change the
    /// rule — the posting role does. Scoping the fix to income categories alone
    /// would have left the engine saying money entering via a category crosses the
    /// boundary while money leaving the same way does not.
    /// </summary>
    [Fact]
    public async Task Returns_counts_a_transfer_out_to_a_category_as_a_withdrawal()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var expense = await ledger.AddCategoryAsync("Tuition", kind: "expense");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(
            brokerage.Id, expense.Id, -100m, Utc(2024, 7, 1), postingRole: "transfer");

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        // $1,000 in less $100 out — and no gain or loss along the way.
        Assert.Equal(900m, result.NetContributions);
        Assert.Equal(900m, result.EndValue);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// Hidden and merged events stay excluded from flows under the perimeter rule —
    /// a hidden rollover must not become a phantom withdrawal.
    /// </summary>
    [Fact]
    public async Task Returns_ignores_a_hidden_rollover_leg()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old 401(k)");
        var destination = await ledger.AddInvestmentAccountAsync("Rollover IRA");

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        var (rolloverLegId, _) = await ledger.AddTransactionPairAsync(
            destination.Id, source.Id, 6_000m, Utc(2024, 7, 1));
        await ledger.HideTransactionAsync(rolloverLegId);
        // Mig 103's is_hidden filter only applies on the next recompute run.
        await ledger.RecomputeBalancesAsync(new[] { source.Id, destination.Id });

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: source.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        // The hidden event contributes neither a flow nor a balance change.
        Assert.Equal(10_000m, result.NetContributions);
        Assert.Equal(10_000m, result.EndValue);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// An account with no external cash flow still has a measurable history. Funded
    /// from a category and invested in-brokerage — neither of which is a flow — it
    /// has no first-flow date to anchor since-inception to. Anchoring on the first
    /// ACTIVITY instead gives a real window, and the price appreciation inside it
    /// is measured properly: $1,000 of stock at the start, $1,200 at the end, one
    /// year, 20%/yr. Falling back to the window end collapsed this to zero length,
    /// where nothing is computable and the IRR solver invented 4950%/yr.
    /// </summary>
    [Fact]
    public async Task Returns_since_inception_anchors_on_first_activity_when_there_is_no_external_flow()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var income = await ledger.AddCategoryAsync("Employer Contribution", kind: "income");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(brokerage.Id, income.Id, 1_000m, Utc(2024, 1, 1));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 10m, Utc(2024, 1, 1));
        await ledger.AddSecurityPriceAsync(sec, 12m, Utc(2024, 10, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));   // 365 days from the first activity

        Assert.Equal(Utc(2024, 1, 1), result.StartDate);
        Assert.NotEqual(result.StartDate, result.EndDate);
        // Money that arrived without a cash flow sits in the START value — it is
        // opening capital here, not return. (Whether a CATEGORY inflow should have
        // been a flow at all is the separate investment-income question.)
        Assert.Equal(1_000m, result.StartValue);
        Assert.Equal(1_200m, result.EndValue);
        Assert.Equal(0m, result.NetContributions);

        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value - 0.2) < 1e-6,
            $"expected 20% TWR, got {result.TimeWeightedReturn}");
        Assert.Null(result.TimeWeightedUnavailableReason);
        Assert.NotNull(result.MoneyWeightedReturn);
        Assert.True(Math.Abs(result.MoneyWeightedReturn!.Value - 0.2) < 1e-3,
            $"expected ~20% IRR, got {result.MoneyWeightedReturn}");
        Assert.Null(result.MoneyWeightedUnavailableReason);
    }

    // ---- opened_on as an inception anchor ---------------------------------
    //
    // An account CAN be opened with a balance, and then its Start Date is the
    // honest inception — the money was demonstrably there from that date, even
    // though no transaction says so. Anchoring on the first transaction instead
    // annualizes the whole gain over a shorter window and overstates the return.
    //
    // But opened_on only means that when the opening balance is NON-ZERO. Most
    // Moneydance ledgers leave every opening balance at 0 and carry all history
    // as transactions; there, opened_on is merely a creation date, the portfolio
    // was empty then, and anchoring on it would hand TWR a zero invested base and
    // turn a working figure into null. Both halves are pinned below.

    /// <summary>
    /// $10,000 opening balance as of 2023-01-01, invested a year later, worth
    /// $12,100 at the end of 2024. The gain is 21% over TWO years — about 10%/yr.
    /// Anchoring on the first transaction would measure it over one year and
    /// report roughly 21%/yr.
    /// </summary>
    [Fact]
    public async Task Returns_anchors_since_inception_on_a_funded_opened_on()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync(
            "Opened With Cash",
            openingBalance: 10_000m,
            openedOn: new DateOnly(2023, 1, 1));
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        // No external flow at all — the opening balance is simply invested.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 100m, Utc(2024, 1, 1));
        await ledger.AddSecurityPriceAsync(sec, 121m, Utc(2024, 12, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2025, 1, 1));

        Assert.Equal(Utc(2023, 1, 1), result.StartDate);
        Assert.Equal(10_000m, result.StartValue);
        Assert.Equal(12_100m, result.EndValue);
        Assert.Equal(0m, result.NetContributions);

        // 1.21 compounded over 731 days → ~10%/yr, not the ~21%/yr a one-year
        // window would report.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value - 0.10) < 5e-3,
            $"expected ~10%/yr TWR over two years, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// The guard that makes the non-zero test load-bearing. Same shape, but the
    /// account opens EMPTY on 2023-01-01 and is funded by a transfer in 2024.
    /// Anchoring on opened_on here would start the window with an invested base of
    /// zero, which TWR cannot chain from — a working return would become null.
    /// The anchor must stay on the flow.
    /// </summary>
    [Fact]
    public async Task Returns_ignores_opened_on_when_the_opening_balance_is_zero()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync(
            "Opened Empty",
            openingBalance: 0m,
            openedOn: new DateOnly(2023, 1, 1));
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 100m, Utc(2024, 1, 1));
        await ledger.AddSecurityPriceAsync(sec, 121m, Utc(2024, 12, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2025, 1, 1));

        Assert.Equal(Utc(2024, 1, 1), result.StartDate);
        Assert.Equal(0m, result.StartValue);
        Assert.Equal(10_000m, result.NetContributions);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.Null(result.TimeWeightedUnavailableReason);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value - 0.21) < 5e-3,
            $"expected ~21%/yr over one year, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// A Start Date LATER than the first flow must never win — the anchor may only
    /// move earlier. Moving it forward would drop real history and reclassify
    /// contributed money as opening capital.
    /// </summary>
    [Fact]
    public async Task Returns_never_lets_opened_on_truncate_earlier_history()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync(
            "Late Start Date",
            openingBalance: 5_000m,
            openedOn: new DateOnly(2024, 7, 1));   // after the flow below

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2025, 1, 1));

        Assert.Equal(Utc(2024, 1, 1), result.StartDate);
        Assert.Equal(1_000m, result.NetContributions);
    }

    /// <summary>
    /// The one case that legitimately has no window: an account nothing has ever
    /// happened to. There is no activity to anchor on, so start and end collapse —
    /// and both figures must say so rather than produce one.
    /// </summary>
    [Fact]
    public async Task Returns_reports_no_rate_for_an_account_with_no_activity_at_all()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Untouched");

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(result.StartDate, result.EndDate);
        Assert.Equal(0m, result.StartValue);
        Assert.Equal(0m, result.EndValue);
        Assert.Equal(0m, result.NetContributions);
        Assert.Null(result.MoneyWeightedReturn);
        Assert.Null(result.TimeWeightedReturn);
        // Both blanks must explain themselves — a null with no reason is the loose
        // end that let 49.50005 pass for an answer in the first place.
        Assert.False(string.IsNullOrEmpty(result.MoneyWeightedUnavailableReason));
        Assert.False(string.IsNullOrEmpty(result.TimeWeightedUnavailableReason));
    }

    /// <summary>
    /// An in-scope flow anchors since-inception even when earlier, non-flow
    /// activity exists — the fallback must not override a real first flow, or a
    /// contribution would be reclassified as opening capital.
    /// </summary>
    [Fact]
    public async Task Returns_since_inception_still_prefers_the_first_external_flow()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var income = await ledger.AddCategoryAsync("Dividends", kind: "income");

        // Category posting FIRST, then a real bank contribution.
        await ledger.AddTransactionPairAsync(brokerage.Id, income.Id, 100m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 900m, Utc(2024, 6, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: brokerage.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        // The bank flow wins the anchor, not the earlier category posting.
        Assert.Equal(Utc(2024, 6, 1), result.StartDate);
        Assert.Equal(900m, result.NetContributions);
        Assert.Equal(1_000m, result.EndValue);
    }

    /// <summary>
    /// The pairing invariant, across every shape the suite builds: a null rate
    /// always carries a reason and a non-null rate never does — for BOTH figures.
    /// Asserted over a matrix rather than one fixture, because the two sides are
    /// computed by separate code paths that previously disagreed about whether an
    /// unexplained blank was acceptable.
    /// </summary>
    [Fact]
    public async Task Returns_always_pairs_a_null_rate_with_a_reason_and_never_the_reverse()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var income = await ledger.AddCategoryAsync("Dividends", kind: "income");
        var source = await ledger.AddInvestmentAccountAsync("Old 401(k)");
        var destination = await ledger.AddInvestmentAccountAsync("Rollover IRA");
        var flowless = await ledger.AddInvestmentAccountAsync("Flowless");
        var emptied = await ledger.AddInvestmentAccountAsync("Emptied");

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(destination.Id, source.Id, 6_000m, Utc(2024, 7, 1));
        await ledger.AddTransactionPairAsync(flowless.Id, income.Id, 1_000m, Utc(2024, 1, 1));
        // Funded then fully withdrawn — the non-positive-base path.
        await ledger.AddTransactionPairAsync(emptied.Id, bank.Id, 5_000m, Utc(2024, 1, 10));
        await ledger.AddTransactionPairAsync(emptied.Id, bank.Id, -5_000m, Utc(2024, 2, 20));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var scopes = new (string Label, Guid? AccountId)[]
        {
            ("ledger", null),
            ("source", source.Id),
            ("destination", destination.Id),
            ("flowless", flowless.Id),
            ("emptied", emptied.Id),
        };

        foreach (var (label, scopedId) in scopes)
        {
            var result = await repository.ReturnsAsync(
                ledger.LedgerId, accountId: scopedId, fromUtc: null, toUtc: null,
                nowUtc: Utc(2024, 12, 31));

            Assert.Equal(
                result.MoneyWeightedReturn is null,
                !string.IsNullOrEmpty(result.MoneyWeightedUnavailableReason));
            Assert.Equal(
                result.TimeWeightedReturn is null,
                !string.IsNullOrEmpty(result.TimeWeightedUnavailableReason));
        }
    }

    /// <summary>
    /// An explicit window (not since-inception) puts the rollover inside the
    /// window with a non-zero opening value — the shape the 5-year report uses.
    /// Start $10,000, roll $6,000 out mid-window, end $4,000 → 0%, not -60%.
    /// </summary>
    [Fact]
    public async Task Returns_account_scope_handles_a_rollover_inside_an_explicit_window()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old 401(k)");
        var destination = await ledger.AddInvestmentAccountAsync("Rollover IRA");

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2023, 6, 1));
        await ledger.AddTransactionPairAsync(destination.Id, source.Id, 6_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: source.Id,
            fromUtc: Utc(2024, 1, 1), toUtc: Utc(2024, 12, 31),
            nowUtc: Utc(2025, 6, 1));

        Assert.Equal(10_000m, result.StartValue);
        Assert.Equal(4_000m, result.EndValue);
        Assert.Equal(-6_000m, result.NetContributions);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
    }

    // ---- in-kind share transfers -----------------------------------------
    //
    // An in-kind rollover moves securities between brokerages with no cash leg.
    // Every leg of the header faces an account inside its OWN brokerage, so the
    // counterparty perimeter test sees nothing crossing — the destination booked
    // the arrival as performance and the source booked the departure as a loss.
    // On a real ledger one transfer put an account at +258%/yr and its
    // counterpart at -10.9%/yr at the same time.
    //
    // Detection is header-level: the header is a flow when its legs SPAN the
    // perimeter. These pin all three outcomes that rule has to produce.

    /// <summary>
    /// Destination side: $10,000 of stock arrives in-kind and nothing is earned,
    /// so contributions are $10,000 and the return is 0% — not the infinite gain
    /// an uncounted arrival produces.
    /// </summary>
    [Fact]
    public async Task Returns_counts_an_in_kind_transfer_in_as_a_contribution()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old IRA");
        var dest = await ledger.AddInvestmentAccountAsync("New IRA");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        // Seed the source so it genuinely holds the shares before they move.
        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddInvestmentBuyAsync(
            source.Id, source.HoldingsAccountId!.Value, sec, 100m, 100m, Utc(2024, 1, 1));
        await ledger.AddTransferSharesAsync(
            source.Id, source.HoldingsAccountId!.Value,
            dest.Id, dest.HoldingsAccountId!.Value,
            sec, 100m, 100m, Utc(2024, 7, 1));
        await ledger.AddSecurityPriceAsync(sec, 100m, Utc(2024, 10, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: dest.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal(10_000m, result.NetContributions);
        Assert.Equal(10_000m, result.EndValue);
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {result.TimeWeightedReturn}");
    }

    /// <summary>
    /// Source side, the mirror image: the same shares leaving are a withdrawal,
    /// not a loss. $10,000 contributed then transferred out nets to zero.
    /// </summary>
    [Fact]
    public async Task Returns_counts_an_in_kind_transfer_out_as_a_withdrawal()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old IRA");
        var dest = await ledger.AddInvestmentAccountAsync("New IRA");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddInvestmentBuyAsync(
            source.Id, source.HoldingsAccountId!.Value, sec, 100m, 100m, Utc(2024, 1, 1));
        await ledger.AddTransferSharesAsync(
            source.Id, source.HoldingsAccountId!.Value,
            dest.Id, dest.HoldingsAccountId!.Value,
            sec, 100m, 100m, Utc(2024, 7, 1));
        await ledger.AddSecurityPriceAsync(sec, 100m, Utc(2024, 10, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: source.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        // $10,000 in from the bank, $10,000 out in kind.
        Assert.Equal(0m, result.NetContributions);
        Assert.Equal(0m, result.EndValue);
    }

    /// <summary>
    /// Ledger scope: both sides are inside the perimeter, so the transfer nets to
    /// zero and stays internal — the same symmetry the cash rollover rule has.
    /// Contributions remain the $10,000 that entered from the bank.
    /// </summary>
    [Fact]
    public async Task Returns_ledger_scope_treats_an_in_kind_transfer_as_internal()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old IRA");
        var dest = await ledger.AddInvestmentAccountAsync("New IRA");
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddInvestmentBuyAsync(
            source.Id, source.HoldingsAccountId!.Value, sec, 100m, 100m, Utc(2024, 1, 1));
        await ledger.AddTransferSharesAsync(
            source.Id, source.HoldingsAccountId!.Value,
            dest.Id, dest.HoldingsAccountId!.Value,
            sec, 100m, 100m, Utc(2024, 7, 1));
        await ledger.AddSecurityPriceAsync(sec, 110m, Utc(2024, 10, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal("ledger", result.Scope);
        Assert.Equal(10_000m, result.NetContributions);
        Assert.Equal(11_000m, result.EndValue);
        // The 10% price move is the only return; moving shares between two
        // in-scope accounts is not.
        Assert.NotNull(result.TimeWeightedReturn);
        Assert.True(Math.Abs(result.TimeWeightedReturn!.Value - 0.1) < 1e-6,
            $"expected 10% TWR, got {result.TimeWeightedReturn}");
    }
    // ---- sizing a request before spending it ---------------------------------
    //
    // The cost of a returns call is the valuation loop, and the loop runs once per
    // instant money crossed the scope's boundary. A caller could previously only
    // discover that count by waiting for the call — up to a minute — or by reading
    // it out of a refusal message. The estimate answers it from the SAME scope
    // resolution the real call uses, so the two can never disagree about what
    // counts.

    [Fact]
    public async Task Returns_cost_estimate_counts_the_instants_the_report_would_value()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 500m, Utc(2024, 4, 1));
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 500m, Utc(2024, 7, 1));
        // Trades are NOT boundaries: they face the holdings sibling, which is
        // inside the perimeter. An account trading daily adds nothing to the count.
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 10m, 100m, Utc(2024, 2, 1));
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 10m, 100m, Utc(2024, 3, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var estimate = await repository.ReturnsCostEstimateAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        Assert.Equal("ledger", estimate.Scope);
        Assert.Equal(3, estimate.FlowInstants);

        // The window it reports is the one the real call resolves — same anchor
        // rule, because it is the same code.
        var actual = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));
        Assert.Equal(actual.StartDate, estimate.StartDate);
        Assert.Equal(actual.EndDate, estimate.EndDate);
        Assert.NotNull(actual.TimeWeightedReturn);   // as the estimate promised
    }

    [Fact]
    public async Task Returns_cost_estimate_is_scope_relative()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var source = await ledger.AddInvestmentAccountAsync("Old 401(k)");
        var destination = await ledger.AddInvestmentAccountAsync("Rollover IRA");

        await ledger.AddTransactionPairAsync(source.Id, bank.Id, 10_000m, Utc(2024, 1, 1));
        await ledger.AddTransactionPairAsync(destination.Id, source.Id, 6_000m, Utc(2024, 7, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var ledgerScope = await repository.ReturnsCostEstimateAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));
        var accountScope = await repository.ReturnsCostEstimateAsync(
            ledger.LedgerId, accountId: source.Id, fromUtc: null, toUtc: null,
            nowUtc: Utc(2024, 12, 31));

        // The rollover is internal to the ledger and external to the source, so it
        // is a boundary at one scope and not the other. A count that ignored scope
        // would mis-size exactly the calls most likely to be expensive.
        Assert.Equal(1, ledgerScope.FlowInstants);
        Assert.Equal(2, accountScope.FlowInstants);
    }

    /// <summary>
    /// There is no boundary ceiling any more. This seeds well past the 400 that
    /// MaxReturnsBoundaries used to refuse at and asserts a time-weighted figure
    /// comes back — the whole point of migrations 200 and 201.
    /// </summary>
    [Fact]
    public async Task Returns_computes_time_weighted_past_the_old_boundary_ceiling()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var sec = await ledger.AddSecurityAsync("Growth", "GRW");

        // 500 distinct flow instants — over the old cap of 400, which would have
        // returned a null TWR with a cost-limit reason.
        const int flowCount = 500;
        var start = Utc(2024, 1, 1);
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10_000m, start);
        await ledger.AddInvestmentBuyAsync(brokerage.Id, holdings, sec, 100m, 100m, start);
        for (var i = 1; i < flowCount; i++)
            await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 10m, start.AddDays(i));
        await ledger.AddSecurityPriceAsync(sec, 110m, start.AddDays(flowCount + 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var estimate = await repository.ReturnsCostEstimateAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: start.AddDays(flowCount + 30));
        Assert.Equal(flowCount, estimate.FlowInstants);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null, fromUtc: null, toUtc: null,
            nowUtc: start.AddDays(flowCount + 30));

        Assert.NotNull(result.TimeWeightedReturn);
        Assert.Null(result.TimeWeightedUnavailableReason);
        Assert.NotNull(result.TimeWeightedCoveredDays);
    }

    [Fact]
    public async Task Returns_reports_covered_days_beside_covered_years()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");

        // Funded well after the window opens: covered span is a fraction of it.
        await ledger.AddTransactionPairAsync(brokerage.Id, bank.Id, 1_000m, Utc(2024, 10, 1));

        await using var db = _fixture.NewDbContext();
        var repository = new InvestmentReportingRepository(db);

        var result = await repository.ReturnsAsync(
            ledger.LedgerId, accountId: null,
            fromUtc: Utc(2024, 1, 1), toUtc: null, nowUtc: Utc(2024, 12, 31));

        // Days, not just years. A report reading 0.28 years as "ten weeks" when it
        // was 101 days is the conversion this removes — and with an interior gap
        // the caller cannot derive days from CoveredTo − CoveredFrom either, since
        // covered time is the SUM of the invested stretches.
        Assert.NotNull(result.TimeWeightedCoveredDays);
        Assert.Equal(91, result.TimeWeightedCoveredDays);   // 2024-10-01 → 2024-12-31
        Assert.Equal(
            (int)Math.Round(result.TimeWeightedCoveredYears!.Value * 365.0),
            result.TimeWeightedCoveredDays!.Value);
        Assert.True(
            result.TimeWeightedCoveredDays < (result.EndDate - result.StartDate).TotalDays,
            "covered days must be shorter than the requested window here");
    }
}
