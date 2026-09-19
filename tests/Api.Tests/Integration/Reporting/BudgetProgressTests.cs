using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Spend-versus-typical, the read-only half of budgeting.
///
/// <para>Nothing here re-tests the aggregation — <see cref="ReportingRepository"/>
/// owns that and has its own suite. What these pin is the arithmetic layered on
/// top, because every one of those rules is a decision that reads as an
/// implementation detail and is actually a product behaviour someone will judge
/// their spending by.</para>
///
/// <para>The fixtures deliberately seed at <b>00:30 UTC on the first</b> rather
/// than the sibling suite's midday. A month boundary seeded at 12:00 has twelve
/// hours of slack in either direction, so an off-by-one-month window passes
/// anyway; half an hour past midnight has none.</para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BudgetProgressTests
{
    private readonly PostgresFixture _fixture;

    public BudgetProgressTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime MonthStart(int year, int month) =>
        new(year, month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Half an hour into the first day — inside the month by the
    /// smallest margin the seed helper can express.</summary>
    private static DateTime JustInside(int year, int month) =>
        new(year, month, 1, 0, 30, 0, DateTimeKind.Utc);

    private BudgetProgressRepository Repo() =>
        new(new ReportingRepository(_fixture.NewDbContext()), _fixture.NewDbContext());

    [Fact]
    public async Task Typical_divides_by_the_window_not_by_months_that_had_spend()
    {
        // THE rule. A mattress bought once in three months is a third of a
        // mattress a month. Dividing by "months that returned a cell" would
        // make a single purchase a permanent monthly expectation, and the
        // category would then look under-budget for the rest of time.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var furniture = await ledger.AddCategoryAsync("Furniture", "expense");

        await ledger.AddTransactionPairAsync(furniture.Id, bank.Id, 900m, JustInside(2026, 6));

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 3);

        var row = Assert.Single(dto.Rows, r => r.CategoryId == furniture.Id);
        Assert.Equal(300m, row.Typical);   // 900 / 3, not 900 / 1
        Assert.Equal(0m, row.Actual);
    }

    [Fact]
    public async Task The_reported_month_is_excluded_from_its_own_average()
    {
        // Comparing a month against an average containing it drags the average
        // toward whatever happened and shrinks every deviation — the more
        // unusual the month, the more it hides itself.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense");

        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 300m, JustInside(2026, 6));
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 300m, JustInside(2026, 7));
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 300m, JustInside(2026, 8));
        // A wild September that must NOT raise its own baseline.
        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 3000m, JustInside(2026, 9));

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 3);

        var row = Assert.Single(dto.Rows, r => r.CategoryId == groceries.Id);
        Assert.Equal(3000m, row.Actual);
        Assert.Equal(300m, row.Typical);
    }

    [Fact]
    public async Task A_category_with_no_history_reports_null_typical_not_zero()
    {
        // Zero would render as infinitely over budget on its first month, which
        // is the most likely month for a brand-new category to be looked at.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var newCat = await ledger.AddCategoryAsync("Wedding", "expense");

        await ledger.AddTransactionPairAsync(newCat.Id, bank.Id, 500m, JustInside(2026, 9));

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 3);

        var row = Assert.Single(dto.Rows, r => r.CategoryId == newCat.Id);
        Assert.Equal(500m, row.Actual);
        Assert.Null(row.Typical);
    }

    [Fact]
    public async Task A_category_that_stopped_still_appears_with_zero_actual()
    {
        // The union of both sides. A category you used to spend on and did not
        // this month is exactly the row a guidepost user wants to notice; only
        // the empty intersection is uninteresting.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var gym = await ledger.AddCategoryAsync("Gym", "expense");

        await ledger.AddTransactionPairAsync(gym.Id, bank.Id, 60m, JustInside(2026, 7));
        await ledger.AddTransactionPairAsync(gym.Id, bank.Id, 60m, JustInside(2026, 8));

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 3);

        var row = Assert.Single(dto.Rows, r => r.CategoryId == gym.Id);
        Assert.Equal(0m, row.Actual);
        Assert.Equal(40m, row.Typical);   // 120 / 3
    }

    [Fact]
    public async Task The_window_boundary_is_half_open_at_both_ends()
    {
        // The whole reason the repository uses TimeBucket.None: the [from, to)
        // bounds define the window, with no SQL date-part label evaluated in
        // whatever timezone the database session happens to carry.
        //
        // Three spends, one in each of the three months either side of the
        // window edges. Only the middle one is inside a 1-month window for
        // September, and the September one is the actual.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var cat = await ledger.AddCategoryAsync("Utilities", "expense");

        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 10m, JustInside(2026, 7));  // outside
        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 80m, JustInside(2026, 8));  // the window
        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 25m, JustInside(2026, 9));  // the month

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 1);

        var row = Assert.Single(dto.Rows, r => r.CategoryId == cat.Id);
        Assert.Equal(25m, row.Actual);
        Assert.Equal(80m, row.Typical);
    }

    [Fact]
    public async Task Totals_come_from_the_result_total_not_from_summing_rollup_rows()
    {
        // With Rollup the parent row is a subtotal that already contains its
        // children, so summing the rows double-counts every descendant. A
        // parent/child pair is the smallest shape that catches it.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var food = await ledger.AddCategoryAsync("Food", "expense");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense", parentId: food.Id);

        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 100m, JustInside(2026, 9));
        await ledger.AddTransactionPairAsync(food.Id, bank.Id, 40m, JustInside(2026, 9));

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 3);

        // The parent's row IS a subtotal: 140. Summing rows would give 240.
        Assert.Equal(140m, Assert.Single(dto.Rows, r => r.CategoryId == food.Id).Actual);
        Assert.Equal(100m, Assert.Single(dto.Rows, r => r.CategoryId == groceries.Id).Actual);
        Assert.Equal(140m, dto.ActualTotal);
    }

    [Fact]
    public async Task Reports_the_month_it_was_asked_for_and_the_window_it_used()
    {
        // A derived number has to show its provenance, or a reader cannot judge
        // it. The screen renders both of these verbatim.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 6);

        Assert.Equal("2026-09", dto.Month);
        Assert.Equal(6, dto.WindowMonths);
    }

    [Fact]
    public async Task The_daily_series_is_LEAF_cells_so_the_client_can_sum_them()
    {
        // Rollup is off for the daily series on purpose, and this is the test
        // that says why. With rollup on, the parent cell already contains its
        // children's spend, so the moment the client sums categories to draw
        // the all-categories line it counts the child twice. Leaf cells sum
        // cleanly — and the row summaries above still use rollup, because a
        // TABLE wants subtotals and a LINE wants addends.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var food = await ledger.AddCategoryAsync("Food", "expense");
        var groceries = await ledger.AddCategoryAsync("Groceries", "expense", parentId: food.Id);

        await ledger.AddTransactionPairAsync(groceries.Id, bank.Id, 100m, JustInside(2026, 9));
        await ledger.AddTransactionPairAsync(food.Id, bank.Id, 40m, JustInside(2026, 9));

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 3);

        Assert.Equal(140m, dto.DailyActual.Sum(c => c.Amount));
        // The parent's own direct spend is its own cell, NOT a subtotal.
        Assert.Equal(40m, Assert.Single(dto.DailyActual, c => c.CategoryId == food.Id).Amount);
        Assert.Equal(100m, Assert.Single(dto.DailyActual, c => c.CategoryId == groceries.Id).Amount);
    }

    [Fact]
    public async Task The_daily_series_carries_the_day_each_amount_fell_on()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var cat = await ledger.AddCategoryAsync("Groceries", "expense");

        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 30m,
            new DateTime(2026, 9, 4, 9, 0, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 70m,
            new DateTime(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc));

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 3);

        var byDay = dto.DailyActual.ToDictionary(c => c.Day, c => c.Amount);
        Assert.Equal(30m, byDay[4]);
        Assert.Equal(70m, byDay[18]);
        Assert.Equal(2, byDay.Count);
    }

    [Fact]
    public async Task The_normal_profile_divides_by_the_window_like_typical_does()
    {
        // Same rule as `typical`, and it matters more here: the shaded band is
        // a SHAPE. Dividing by "months that had spend on that day" would make a
        // single big Tuesday a permanent feature of the profile.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var cat = await ledger.AddCategoryAsync("Rent", "expense");

        // Rent on the 1st of one month only, inside a 3-month window.
        await ledger.AddTransactionPairAsync(cat.Id, bank.Id, 900m, JustInside(2026, 7));

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 3);

        var cell = Assert.Single(dto.DailyNormal, c => c.CategoryId == cat.Id);
        Assert.Equal(1, cell.Day);
        Assert.Equal(300m, cell.Amount);   // 900 / 3
    }

    [Fact]
    public async Task Elapsed_days_stops_the_line_at_today_and_fills_a_past_month()
    {
        // A cumulative line drawn to the 30th when only 16 days have happened
        // reads as spending collapsing to zero mid-month.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        var past = await Repo().GetAsync(ledger.LedgerId, MonthStart(2020, 2), windowMonths: 3);
        Assert.Equal(29, past.DaysInMonth);      // 2020 was a leap year
        Assert.Equal(29, past.ElapsedDays);      // wholly in the past

        var future = await Repo().GetAsync(ledger.LedgerId, MonthStart(2099, 5), windowMonths: 3);
        Assert.Equal(31, future.DaysInMonth);
        Assert.Equal(0, future.ElapsedDays);     // has not started
    }

    [Fact]
    public async Task A_negative_trailing_total_is_no_normal_at_all()
    {
        // Found on real data: a category whose only recent activity was a
        // REFUND sums below zero over the window, and the first cut divided
        // that by three and called it a budget. The row then reported itself
        // over by the refund amount, in a month with no spending whatsoever —
        // the single "past normal" row on an otherwise empty screen.
        //
        // You cannot overspend a negative budget. Null is the honest answer.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var clothing = await ledger.AddCategoryAsync("Clothing", "expense");

        // A return: the category leg goes the other way.
        await ledger.AddTransactionPairAsync(bank.Id, clothing.Id, 27m, JustInside(2026, 7));

        var dto = await Repo().GetAsync(ledger.LedgerId, MonthStart(2026, 9), windowMonths: 3);

        var row = dto.Rows.SingleOrDefault(r => r.CategoryId == clothing.Id);
        if (row is not null) Assert.Null(row.Typical);
        Assert.Null(dto.TypicalTotal);
    }
}
