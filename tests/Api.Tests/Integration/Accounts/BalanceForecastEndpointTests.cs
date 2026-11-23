using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Integration.Transactions;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// <c>GET /accounts/balance-forecast</c>: each account's balance over a short window,
/// from the register's own current balance, its committed rows and the reminders not
/// yet posted.
/// </summary>
/// <remarks>
/// <para>Every case sends the caller's today, as the SPA does, so nothing depends on the
/// wall clock, and every row and reminder is written through the API (the one fixture
/// that is not is a reminder template with no lines, which only the Moneydance importer
/// builds).</para>
/// <para>The reconciliation case is the ship condition: day 0 must be the register's
/// current balance even with an overdue reminder waiting, which is placed on day 1.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class BalanceForecastEndpointTests : DatabaseTest
{
    public BalanceForecastEndpointTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>The caller's today for every case.</summary>
    private static readonly DateOnly Today = new(2026, 6, 15);

    // ---- the ship condition -------------------------------------------------------

    [Fact]
    public async Task Day_zero_is_the_registers_current_balance_with_an_overdue_occurrence_present()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 1_000m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Rows the register counts today, one of them dated today, and one it calls
        // Scheduled.
        await PostAsync(client, ledger, account.Id, category.Id, -40m, Today.AddDays(-20));
        await PostAsync(client, ledger, account.Id, category.Id, -15m, Today);
        await PostAsync(client, ledger, account.Id, category.Id, -60m, Today.AddDays(5));
        // A monthly reminder whose occurrence before today nobody posted; its next one is
        // past the window.
        var due = Today.AddDays(-10);
        await CreateReminderAsync(client, ledger, account.Id, category.Id, -25m, due);

        var forecast = await ForecastAsync(client, ledger, days: 10);

        var row = Assert.Single(forecast.Accounts);
        Assert.Equal(account.Id, row.AccountId);
        Assert.Equal(Today, row.Points[0].Date);
        Assert.Equal(row.Today, row.Points[0].Balance);
        // The register's own figure, read two ways: its running balance on the last row
        // it does not call Scheduled, and the sidebar's current balance.
        Assert.Equal(await RegisterCurrentBalanceAsync(client, ledger, account.Id), row.Today);
        Assert.Equal(await SidebarBalanceAsync(client, ledger, account.Id), row.Today);
        Assert.Equal(945m, row.Today);

        // The overdue occurrence is in the projection, from day 1, and counted.
        Assert.Equal(1, forecast.OverdueOnDay1);
        Assert.Equal(row.Today - 25m, row.Points[1].Balance);
        Assert.Equal(row.Today - 25m - 60m, row.Points[5].Balance);
        Assert.Equal(row.Today - 25m - 60m, row.Points[^1].Balance);
    }

    // ---- committed rows -------------------------------------------------------------

    [Fact]
    public async Task Committed_rows_move_the_projection_on_their_own_dates()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 500m);
        var category = await ledger.AddCategoryAsync("Category A");
        var income = await ledger.AddCategoryAsync("Category B", "income");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await PostAsync(client, ledger, account.Id, category.Id, -5m, Today);
        // Two rows on one day, the day's balance the one the second leaves.
        await PostAsync(client, ledger, account.Id, category.Id, -30m, Today.AddDays(2));
        await PostAsync(client, ledger, account.Id, income.Id, 200m, Today.AddDays(2));
        await PostAsync(client, ledger, account.Id, category.Id, -50m, Today.AddDays(7));
        // On the window's last day, and the day after it.
        await PostAsync(client, ledger, account.Id, category.Id, -3m, Today.AddDays(10));
        await PostAsync(client, ledger, account.Id, category.Id, -1000m, Today.AddDays(11));
        await InScopeAsync(client, ledger, account.Id, category.Id);

        var forecast = await ForecastAsync(client, ledger, days: 10);

        var row = Assert.Single(forecast.Accounts);
        Assert.Equal(
            [495m, 495m, 665m, 665m, 665m, 665m, 665m, 615m, 615m, 615m, 612m],
            row.Points.Select(p => p.Balance));
        Assert.Equal(Enumerable.Range(0, 11).Select(Today.AddDays), row.Points.Select(p => p.Date));
        Assert.Equal(0, forecast.OverdueOnDay1);
        Assert.Empty(forecast.Excluded);
    }

    [Fact]
    public async Task Hidden_merged_and_template_headers_in_the_window_do_not_move_the_projection()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 800m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A reminder whose template is dated in the window and whose only occurrence in
        // it is skipped: its template header sits there, and nothing is due.
        var templateDay = Today.AddDays(4);
        var reminderId = await CreateReminderAsync(client, ledger, account.Id, category.Id, -70m, templateDay);
        var skip = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/skip", new SkipReminderRequest { OccurrenceDate = templateDay });
        Assert.True(skip.IsSuccessStatusCode, await skip.Content.ReadAsStringAsync());

        // Two imported rows dated ahead: one deleted, which hides an imported row, and
        // one folded into a row entered by hand on the same day.
        await ImportAsync(client, ledger, account.Id,
            ("FORECAST-HIDE", Today.AddDays(6), -45m),
            ("FORECAST-MERGE", Today.AddDays(8), -21m));
        var imported = await ImportedHeadersAsync(ledger);
        var hidden = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{imported[Today.AddDays(6)]}");
        Assert.True(hidden.IsSuccessStatusCode, await hidden.Content.ReadAsStringAsync());
        var winner = await PostAsync(client, ledger, account.Id, category.Id, -20m, Today.AddDays(8));
        var merged = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{imported[Today.AddDays(8)]}/merge",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());

        await using (var db = Db.NewDbContext())
        {
            // The premise: each of the three headers is in the window, in its state.
            var states = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId)
                .Select(h => new { h.PostedAt, h.IsHidden, h.IsMergedInto, h.IsRecurringTemplate })
                .ToListAsync();
            Assert.Contains(states, h => h.IsRecurringTemplate && DateOnly.FromDateTime(h.PostedAt) == templateDay);
            Assert.Contains(states, h => h.IsHidden && DateOnly.FromDateTime(h.PostedAt) == Today.AddDays(6));
            Assert.Contains(states, h => h.IsMergedInto == winner && DateOnly.FromDateTime(h.PostedAt) == Today.AddDays(8));
        }

        var forecast = await ForecastAsync(client, ledger, days: 20);

        var row = Assert.Single(forecast.Accounts);
        Assert.Equal(800m, row.Today);
        // Only the row entered by hand moves it.
        Assert.All(row.Points.Take(8), p => Assert.Equal(800m, p.Balance));
        Assert.All(row.Points.Skip(8), p => Assert.Equal(780m, p.Balance));
        Assert.Empty(forecast.Excluded);
    }

    // ---- reminders not yet posted -----------------------------------------------------

    [Fact]
    public async Task An_occurrence_dated_today_is_placed_on_day_one_and_is_not_overdue()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 300m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await CreateReminderAsync(client, ledger, account.Id, category.Id, -40m, Today);

        var forecast = await ForecastAsync(client, ledger, days: 20);

        var row = Assert.Single(forecast.Accounts);
        Assert.Equal(300m, row.Points[0].Balance);
        Assert.All(row.Points.Skip(1), p => Assert.Equal(260m, p.Balance));
        Assert.Equal(0, forecast.OverdueOnDay1);
        Assert.False(forecast.IncludesEstimates);
    }

    [Fact]
    public async Task Occurrences_dated_before_today_are_placed_on_day_one_and_counted_as_overdue()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 300m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        // Weekly from two weeks back: two occurrences before today, one today, one ahead.
        await CreateReminderAsync(client, ledger, account.Id, category.Id, -10m, Today.AddDays(-14), rrule: "FREQ=WEEKLY");

        var forecast = await ForecastAsync(client, ledger, days: 10);

        var row = Assert.Single(forecast.Accounts);
        Assert.Equal(300m, row.Points[0].Balance);
        Assert.Equal(270m, row.Points[1].Balance);
        Assert.Equal(270m, row.Points[6].Balance);
        Assert.Equal(260m, row.Points[7].Balance);
        Assert.Equal(2, forecast.OverdueOnDay1);
    }

    [Fact]
    public async Task A_loan_places_its_computed_payment_on_the_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Account A", openingBalance: 5_000m);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var loan = await CreateLoanAsync(client, ledger, "USD");
        var reminderId = await SetUpLoanPaymentAsync(client, ledger, loan, checking.Id, Today.AddDays(5));

        // The payment the agenda shows, which the template's placeholder lines are not.
        var occurrence = Assert.Single(await UpcomingAsync(client, ledger, Today, Today.AddDays(20)),
            o => o.ReminderId == reminderId && o.Kind == "reminder");
        Assert.Equal(Today.AddDays(5), occurrence.Date);
        Assert.True(occurrence.Amount < -300m, $"expected the full payment, got {occurrence.Amount}");

        var forecast = await ForecastAsync(client, ledger, days: 20);

        var row = Assert.Single(forecast.Accounts);
        Assert.Equal(checking.Id, row.AccountId);
        Assert.All(row.Points.Take(5), p => Assert.Equal(5_000m, p.Balance));
        Assert.All(row.Points.Skip(5), p => Assert.Equal(5_000m + occurrence.Amount, p.Balance));
        Assert.Empty(forecast.Excluded);
        Assert.False(forecast.IncludesEstimates);
    }

    [Fact]
    public async Task A_loan_whose_escrow_line_lands_on_another_reminders_source_is_listed_not_placed()
    {
        // A loan account cannot be a reminder's source (a reminder is paid from a
        // bank-shape account), but its escrow line can go to one that is: then a line of
        // the loan's split, priced per payment, lands on an account in scope.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Account A", openingBalance: 5_000m);
        var escrow = await ledger.AddBankAccountAsync("Account B", openingBalance: 300m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var loan = await CreateLoanAsync(client, ledger, "USD", escrowAccountId: escrow.Id);
        var reminderId = await SetUpLoanPaymentAsync(client, ledger, loan, checking.Id, Today.AddDays(5));
        await InScopeAsync(client, ledger, escrow.Id, category.Id);

        var forecast = await ForecastAsync(client, ledger, days: 20);

        Assert.Equal([checking.Id, escrow.Id], forecast.Accounts.Select(a => a.AccountId));
        Assert.All(forecast.Accounts, a => Assert.All(a.Points, p => Assert.Equal(a.Today, p.Balance)));
        var excluded = Assert.Single(forecast.Excluded);
        Assert.Equal(reminderId, excluded.ReminderId);
        Assert.Equal(BalanceForecastRepository.Reasons.OverrideNotPlaceable, excluded.Reason);
    }

    /// <summary>
    /// A managed loan series whose paying account is in another currency than the loan's
    /// can never post (ADR-0114 D13), so Cash ahead still lists it as cross-currency. Setup
    /// refuses one, so the series is set up in one currency and the paying account's
    /// currency is changed after.
    /// </summary>
    [Fact]
    public async Task A_loan_in_another_currency_than_its_source_is_listed_not_placed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Account A", openingBalance: 5_000m);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var loan = await CreateLoanAsync(client, ledger, "USD");
        var reminderId = await SetUpLoanPaymentAsync(client, ledger, loan, checking.Id, Today.AddDays(5));
        await SetCurrencyAsync(client, ledger, checking.Id, "EUR");

        var forecast = await ForecastAsync(client, ledger, days: 20);

        var row = Assert.Single(forecast.Accounts);
        Assert.All(row.Points, p => Assert.Equal(5_000m, p.Balance));
        var excluded = Assert.Single(forecast.Excluded);
        Assert.Equal(reminderId, excluded.ReminderId);
        Assert.Equal(BalanceForecastRepository.Reasons.CrossCurrency, excluded.Reason);
    }

    /// <summary>
    /// A managed loan series paid from an account in the loan's currency whose real escrow
    /// account is in another one can never post either (ADR-0114 D13), so it is listed as
    /// cross-currency too, although the source and the loan agree. The escrow account's
    /// currency is changed after setup, which refuses such a series.
    /// </summary>
    [Fact]
    public async Task A_loan_whose_real_escrow_account_is_in_another_currency_is_listed_not_placed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Account A", openingBalance: 5_000m);
        var escrow = await ledger.AddBankAccountAsync("Account B", openingBalance: 300m);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var loan = await CreateLoanAsync(client, ledger, "USD", escrowAccountId: escrow.Id);
        var reminderId = await SetUpLoanPaymentAsync(client, ledger, loan, checking.Id, Today.AddDays(5));
        await SetCurrencyAsync(client, ledger, escrow.Id, "EUR");

        var forecast = await ForecastAsync(client, ledger, days: 20);

        var row = Assert.Single(forecast.Accounts);
        Assert.Equal(checking.Id, row.AccountId);
        Assert.All(row.Points, p => Assert.Equal(5_000m, p.Balance));
        var excluded = Assert.Single(forecast.Excluded);
        Assert.Equal(reminderId, excluded.ReminderId);
        Assert.Equal(BalanceForecastRepository.Reasons.CrossCurrency, excluded.Reason);
    }

    /// <summary>
    /// A real escrow account in another currency than the loan's that is itself another
    /// reminder's source, and so in scope, still lists the series as cross-currency: it can
    /// never post, which is truer than a split this read does not price (ADR-0114 D13).
    /// The same shape in one currency is listed as not placeable
    /// (<see cref="A_loan_whose_escrow_line_lands_on_another_reminders_source_is_listed_not_placed"/>).
    /// </summary>
    [Fact]
    public async Task A_loan_whose_in_scope_real_escrow_account_is_in_another_currency_is_listed_as_cross_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Account A", openingBalance: 5_000m);
        var escrow = await ledger.AddBankAccountAsync("Account B", openingBalance: 300m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var loan = await CreateLoanAsync(client, ledger, "USD", escrowAccountId: escrow.Id);
        var reminderId = await SetUpLoanPaymentAsync(client, ledger, loan, checking.Id, Today.AddDays(5));
        await InScopeAsync(client, ledger, escrow.Id, category.Id);
        await SetCurrencyAsync(client, ledger, escrow.Id, "EUR");

        var forecast = await ForecastAsync(client, ledger, days: 20);

        Assert.Equal([checking.Id, escrow.Id], forecast.Accounts.Select(a => a.AccountId));
        var excluded = Assert.Single(forecast.Excluded);
        Assert.Equal(reminderId, excluded.ReminderId);
        Assert.Equal(BalanceForecastRepository.Reasons.CrossCurrency, excluded.Reason);
    }

    private static async Task SetCurrencyAsync(HttpClient client, SyntheticLedger ledger, Guid accountId, string currency)
    {
        var response = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{accountId}")
        {
            Content = JsonContent.Create(new UpdateAccountRequest { CurrencyCode = currency }),
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_estimate_is_placed_on_the_source_and_negated_on_an_in_scope_other_line()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddBankAccountAsync("Account A", openingBalance: 2_000m);
        var other = await ledger.AddBankAccountAsync("Account B", openingBalance: 100m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var reminderId = await EstimatingTransferAsync(client, ledger, source.Id, other.Id);

        var occurrence = Assert.Single(await UpcomingAsync(client, ledger, Today, Today.AddDays(20)),
            o => o.ReminderId == reminderId && o.Kind == "reminder");
        var estimate = occurrence.Estimate!.Amount!.Value;
        // The premise: the estimate is not the template's amount.
        Assert.NotEqual(TemplateTransferAmount, estimate);

        // The other line out of scope: the source alone moves.
        var bySource = await ForecastAsync(client, ledger, days: 20);
        var onSource = Assert.Single(bySource.Accounts);
        Assert.Equal(source.Id, onSource.AccountId);
        Assert.Equal(onSource.Today, onSource.Points[2].Balance);
        Assert.Equal(onSource.Today + estimate, onSource.Points[3].Balance);
        Assert.True(bySource.IncludesEstimates);

        // The other account becomes a reminder source, and so in scope: it takes the
        // negation. A later outflow puts its lowest after the negation's day, so that
        // figure rests on the estimate too.
        await InScopeAsync(client, ledger, other.Id, category.Id);
        await PostAsync(client, ledger, other.Id, category.Id, -500m, Today.AddDays(5));
        var both = await ForecastAsync(client, ledger, days: 20);
        Assert.Equal([source.Id, other.Id], both.Accounts.Select(a => a.AccountId));
        var onOther = both.Accounts[1];
        Assert.Equal(onOther.Today, onOther.Points[2].Balance);
        Assert.Equal(onOther.Today - estimate, onOther.Points[3].Balance);
        Assert.Equal(new BalanceForecastLowestDto(onOther.Today - estimate - 500m, Today.AddDays(5), Estimated: true), onOther.Lowest);
        Assert.Equal(both.Accounts[0].Today + estimate, both.Accounts[0].Points[3].Balance);
        Assert.True(both.IncludesEstimates);
        Assert.Empty(both.Excluded);
    }

    /// <summary>The server's UTC today, which the latest rate is read for (ADR-0114 D9),
    /// whatever the caller's today.</summary>
    private static DateOnly ServerToday => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

    /// <summary>Types one unit of <paramref name="from"/> in <paramref name="to"/> through
    /// the hand-entry route a person uses. Typed for the server's UTC yesterday it is the
    /// latest rate, its day extending the pair's coverage (ADR-0114 D9).</summary>
    private static async Task TypeRateAsync(
        HttpClient client, SyntheticLedger ledger, string from, string to, DateOnly day, decimal rate)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/fx-rates",
            new AddFxRateRequest { FromCurrency = from, ToCurrency = to, Date = $"{day:yyyy-MM-dd}", Rate = rate });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A fixed reminder between currencies places each template line as the post
    /// writes it: the amount on the source, the other line's own amount on the other
    /// account (ADR-0114 D13).</summary>
    [Fact]
    public async Task A_fixed_reminder_between_currencies_places_the_other_lines_own_amount_on_the_other_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddBankAccountAsync("Account A", openingBalance: 2_000m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var other = await CreateAccountAsync(client, ledger, "Account B", "bank", "EUR", 100m);
        // The second amount is not the first negated, so a mirror would show.
        await CreateReminderAsync(client, ledger, source.Id, other, -100m, Today.AddDays(3), counterpartyAmount: 85m);
        await InScopeAsync(client, ledger, other, category.Id);

        var forecast = await ForecastAsync(client, ledger, days: 20);

        Assert.Empty(forecast.Excluded);
        Assert.Equal([source.Id, other], forecast.Accounts.Select(a => a.AccountId));
        var onSource = forecast.Accounts[0];
        var onOther = forecast.Accounts[1];
        Assert.Equal(-100m, onSource.Points[3].Balance - onSource.Points[2].Balance);
        Assert.Equal(85m, onOther.Points[3].Balance - onOther.Points[2].Balance);
        Assert.False(forecast.IncludesEstimates);
    }

    /// <summary>An estimating reminder between currencies places the source estimate on the
    /// source and that estimate at the latest derived rate on the other account, for every
    /// occurrence, an overdue one included (ADR-0114 D13, ADR-0109 D8).</summary>
    /// <remarks>Only the latest rate is typed, so the overdue occurrence's own due date has
    /// no rate: what it places is the latest rate's figure, as a forecast day converts, not
    /// its own day's.</remarks>
    [Fact]
    public async Task An_estimate_between_currencies_places_the_other_side_at_the_latest_derived_rate_on_every_occurrence()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddBankAccountAsync("Account A", openingBalance: 2_000m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var other = await CreateAccountAsync(client, ledger, "Account B", "bank", "EUR", 100m);
        // Two of the three months posted, averaging to the estimate the class's other
        // cases use; the last month before the next occurrence stays overdue.
        var reminderId = await EstimatingTransferAsync(
            client, ledger, source.Id, other, counterpartyRatio: 0.9m, leaveLastOverdue: true);
        await InScopeAsync(client, ledger, other, category.Id);
        // One unit of the other account's currency in the reporting currency.
        await TypeRateAsync(client, ledger, "EUR", "USD", ServerToday.AddDays(-1), 1.25m);

        var forecast = await ForecastAsync(client, ledger, days: 20);

        Assert.Empty(forecast.Excluded);
        var onSource = forecast.Accounts.Single(a => a.AccountId == source.Id);
        var onOther = forecast.Accounts.Single(a => a.AccountId == other);
        // The overdue occurrence lands on day 1, the next one on day 3.
        Assert.Equal(EstimatedAmount, onSource.Points[1].Balance - onSource.Points[0].Balance);
        Assert.Equal(EstimatedAmount, onSource.Points[3].Balance - onSource.Points[2].Balance);
        // The estimate negated, at one over the stored rate, rounded at the other
        // account's places.
        var otherSide = Math.Round(-EstimatedAmount / 1.25m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(otherSide, onOther.Points[1].Balance - onOther.Points[0].Balance);
        Assert.Equal(otherSide, onOther.Points[3].Balance - onOther.Points[2].Balance);
        Assert.Equal(1, forecast.OverdueOnDay1);
        Assert.True(forecast.IncludesEstimates);

        // The premise: the overdue occurrence's own due date has no rate, so the agenda
        // gives it no estimate; the forecast reads the latest rate for it all the same.
        var overdue = Assert.Single(await UpcomingAsync(client, ledger, Today.AddDays(-40), Today),
            o => o.ReminderId == reminderId && o.Kind == "reminder");
        Assert.Equal("rate-unavailable", overdue.Estimate!.UnavailableReason);
    }

    /// <summary>With the latest derived rate missing, an estimating reminder between
    /// currencies is one excluded row, naming each missing stored leg, and nothing of it is
    /// placed (ADR-0114 D13).</summary>
    [Fact]
    public async Task An_estimate_between_currencies_with_no_latest_rate_is_listed_naming_the_missing_leg()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddBankAccountAsync("Account A", openingBalance: 2_000m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var other = await CreateAccountAsync(client, ledger, "Account B", "bank", "EUR", 100m);
        var reminderId = await EstimatingTransferAsync(client, ledger, source.Id, other, counterpartyRatio: 0.9m);
        await InScopeAsync(client, ledger, other, category.Id);

        var forecast = await ForecastAsync(client, ledger, days: 20);

        Assert.Equal([source.Id, other], forecast.Accounts.Select(a => a.AccountId));
        Assert.All(forecast.Accounts, a => Assert.All(a.Points, p => Assert.Equal(a.Today, p.Balance)));
        var excluded = Assert.Single(forecast.Excluded);
        Assert.Equal(reminderId, excluded.ReminderId);
        Assert.Equal(BalanceForecastRepository.Reasons.RateUnavailable, excluded.Reason);
        Assert.Equal(new RateUnavailable("EUR", "USD", ServerToday), Assert.Single(excluded.RateUnavailable!));
        Assert.False(forecast.IncludesEstimates);
    }

    [Fact]
    public async Task An_estimate_marks_the_lowest_of_the_account_it_lands_on_and_the_combined_lowest_after_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var estimating = await ledger.AddBankAccountAsync("Account A", openingBalance: 2_000m);
        var plain = await ledger.AddBankAccountAsync("Account B", openingBalance: 1_000m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        // An estimate on day 3, to a category, so it lands on one account only.
        await EstimatingTransferAsync(client, ledger, estimating.Id, category.Id);
        // The other account's lowest, and so the combined one, comes later, from a
        // committed row.
        await InScopeAsync(client, ledger, plain.Id, category.Id);
        await PostAsync(client, ledger, plain.Id, category.Id, -800m, Today.AddDays(5));

        var forecast = await ForecastAsync(client, ledger, days: 20);

        Assert.Equal([estimating.Id, plain.Id], forecast.Accounts.Select(a => a.AccountId));
        var onEstimating = forecast.Accounts[0];
        var onPlain = forecast.Accounts[1];
        Assert.Equal(new BalanceForecastLowestDto(onEstimating.Today + EstimatedAmount, Today.AddDays(3), Estimated: true), onEstimating.Lowest);
        Assert.Equal(new BalanceForecastLowestDto(onPlain.Today - 800m, Today.AddDays(5), Estimated: false), onPlain.Lowest);
        var combined = forecast.Combined!;
        Assert.Equal(new BalanceForecastLowestDto(combined.Today + EstimatedAmount - 800m, Today.AddDays(5), Estimated: true), combined.Lowest);
        Assert.True(forecast.IncludesEstimates);
    }

    [Fact]
    public async Task An_estimate_landing_after_the_lowest_does_not_mark_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 2_000m);
        var category = await ledger.AddCategoryAsync("Category A");
        var income = await ledger.AddCategoryAsync("Category B", "income");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await EstimatingTransferAsync(client, ledger, account.Id, category.Id);
        // The lowest falls on day 1, before the estimate lands on day 3.
        await PostAsync(client, ledger, account.Id, category.Id, -1_500m, Today.AddDays(1));
        await PostAsync(client, ledger, account.Id, income.Id, 1_500m, Today.AddDays(2));

        var forecast = await ForecastAsync(client, ledger, days: 20);

        var row = Assert.Single(forecast.Accounts);
        Assert.Equal(row.Today + EstimatedAmount, row.Points[3].Balance);
        Assert.Equal(new BalanceForecastLowestDto(row.Today - 1_500m, Today.AddDays(1), Estimated: false), row.Lowest);
        Assert.Equal(new BalanceForecastLowestDto(row.Today - 1_500m, Today.AddDays(1), Estimated: false), forecast.Combined!.Lowest);
        Assert.True(forecast.IncludesEstimates);
    }

    [Fact]
    public async Task An_estimating_reminder_with_no_history_places_the_amount_the_bills_widget_shows()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddBankAccountAsync("Account A", openingBalance: 400m);
        var other = await ledger.AddBankAccountAsync("Account B", openingBalance: 100m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        // Estimating from the start, so nothing has posted to estimate from yet, and a
        // transfer, so its other line lands on an account in scope too.
        var reminderId = await CreateReminderAsync(
            client, ledger, source.Id, other.Id, -35m, Today.AddDays(3), estimateSampleCount: 3);
        await InScopeAsync(client, ledger, other.Id, category.Id);

        // What the agenda gives the Bills widget for the occurrence: the amount entered,
        // with no estimate yet.
        var occurrence = Assert.Single(await UpcomingAsync(client, ledger, Today, Today.AddDays(20)),
            o => o.ReminderId == reminderId && o.Kind == "reminder");
        Assert.Null(occurrence.Estimate!.Amount);
        Assert.Equal(-35m, occurrence.Amount);

        var forecast = await ForecastAsync(client, ledger, days: 20);

        Assert.Empty(forecast.Excluded);
        Assert.Equal([source.Id, other.Id], forecast.Accounts.Select(a => a.AccountId));
        var onSource = forecast.Accounts[0];
        var onOther = forecast.Accounts[1];
        // The source takes exactly that amount on the occurrence's day, and the other line
        // the amount entered on it.
        Assert.Equal(occurrence.Amount, onSource.Points[3].Balance - onSource.Points[2].Balance);
        Assert.Equal(-occurrence.Amount, onOther.Points[3].Balance - onOther.Points[2].Balance);
        // The series is estimated, so what the amount reaches rests on an estimate.
        Assert.Equal(new BalanceForecastLowestDto(400m + occurrence.Amount, Today.AddDays(3), Estimated: true), onSource.Lowest);
        Assert.True(forecast.IncludesEstimates);
    }

    [Fact]
    public async Task A_reminder_whose_template_has_no_lines_is_listed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 400m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await CreateReminderAsync(client, ledger, account.Id, category.Id, -35m, Today.AddDays(3));

        // A Moneydance reminder with no field values: a template without lines and with
        // no source. Only the importer builds that, hence SQL.
        var templateId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            await TxnHeaderSeed.InsertLeglessHeaderAsync(db, templateId, ledger.LedgerId,
                $"mdreminder:{seriesId:N}", "Payee A", null, Today.AddDays(-40).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                isTemplate: true);
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{seriesId:N}",
                Rrule = $"FREQ=MONTHLY;BYMONTHDAY={Today.AddDays(7).Day}", TemplateHeaderId = templateId,
                StartDate = Today.AddDays(-40), NextDueDate = Today.AddDays(7), IsActive = true,
                Origin = "moneydance_import",
            });
            await db.SaveChangesAsync();
        }

        var forecast = await ForecastAsync(client, ledger, days: 20);

        var row = Assert.Single(forecast.Accounts);
        Assert.Equal(365m, row.Points[^1].Balance);
        var excluded = Assert.Single(forecast.Excluded);
        Assert.Equal(seriesId, excluded.ReminderId);
        Assert.Equal(BalanceForecastRepository.Reasons.NoLines, excluded.Reason);
    }

    [Fact]
    public async Task An_investment_reminder_places_its_cash_line_and_a_brokerage_starts_from_its_cash()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", openingBalance: 10_000m);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync("Security A", ticker: "SECA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Cash in, and a position bought before today, so the brokerage holds both.
        await PostAsync(client, ledger, bank.Id, brokerage.Id, -3_000m, Today.AddDays(-30));
        var buy = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            BuyRequest(brokerage.Id, security, Today.AddDays(-20), shares: 10m, price: 100m));
        Assert.Equal(HttpStatusCode.Created, buy.StatusCode);
        // A monthly buy, the brokerage its source.
        var reminder = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = $"FREQ=MONTHLY;BYMONTHDAY={Today.AddDays(4).Day}",
                StartDate = Today.AddDays(4),
                Transaction = BuyRequest(brokerage.Id, security, Today.AddDays(4), shares: 2m, price: 50m),
            });
        Assert.Equal(HttpStatusCode.Created, reminder.StatusCode);

        var forecast = await ForecastAsync(client, ledger, days: 20);

        // The brokerage alone: its holdings sibling, where the shares sit, is never in scope.
        var row = Assert.Single(forecast.Accounts);
        Assert.Equal(brokerage.Id, row.AccountId);
        // Day 0 is the brokerage's cash, the register's figure, not cash plus positions.
        Assert.Equal(2_000m, row.Today);
        Assert.Equal(await RegisterCurrentBalanceAsync(client, ledger, brokerage.Id), row.Today);
        Assert.NotEqual(await SidebarBalanceAsync(client, ledger, brokerage.Id), row.Today);
        // The buy's cash line, and nothing for the shares it adds.
        Assert.Equal(2_000m, row.Points[3].Balance);
        Assert.Equal(1_900m, row.Points[4].Balance);
        Assert.Empty(forecast.Excluded);
    }

    // ---- shape of the answer ----------------------------------------------------------

    [Fact]
    public async Task Lowest_is_the_first_day_the_lowest_balance_is_reached()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 100m);
        var rising = await ledger.AddBankAccountAsync("Account B", openingBalance: 100m);
        var category = await ledger.AddCategoryAsync("Category A");
        var income = await ledger.AddCategoryAsync("Category B", "income");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await InScopeAsync(client, ledger, account.Id, category.Id);
        await InScopeAsync(client, ledger, rising.Id, category.Id);

        // Below zero on one day, back up, and down to the same figure again later.
        await PostAsync(client, ledger, account.Id, category.Id, -150m, Today.AddDays(3));
        await PostAsync(client, ledger, account.Id, income.Id, 150m, Today.AddDays(5));
        await PostAsync(client, ledger, account.Id, category.Id, -150m, Today.AddDays(9));
        // A balance that only rises.
        await PostAsync(client, ledger, rising.Id, income.Id, 10m, Today.AddDays(2));

        var forecast = await ForecastAsync(client, ledger, days: 12);

        Assert.Equal([account.Id, rising.Id], forecast.Accounts.Select(a => a.AccountId));
        Assert.Equal(new BalanceForecastLowestDto(-50m, Today.AddDays(3), Estimated: false), forecast.Accounts[0].Lowest);
        // A balance that only rises is lowest today.
        Assert.Equal(new BalanceForecastLowestDto(100m, Today, Estimated: false), forecast.Accounts[1].Lowest);
    }

    [Fact]
    public async Task Days_is_clamped_to_the_window_the_server_allows()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 100m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await InScopeAsync(client, ledger, account.Id, category.Id);

        async Task<int> PointsAsync(int? days) =>
            Assert.Single((await ForecastAsync(client, ledger, days: days)).Accounts).Points.Count;

        Assert.Equal(BalanceForecastRepository.DefaultDays + 1, await PointsAsync(null));
        Assert.Equal(2, await PointsAsync(0));
        Assert.Equal(2, await PointsAsync(-5));
        Assert.Equal(15, await PointsAsync(14));
        Assert.Equal(BalanceForecastRepository.MaxDays + 1, await PointsAsync(BalanceForecastRepository.MaxDays));
        Assert.Equal(BalanceForecastRepository.MaxDays + 1, await PointsAsync(10_000));
    }

    [Fact]
    public async Task The_scope_is_every_reminder_source_inactive_ones_too_and_nothing_else()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var first = await ledger.AddBankAccountAsync("Account A", openingBalance: 100m);
        var second = await ledger.AddAccountAsync("Account B", "credit_card", openingBalance: -50m);
        var idle = await ledger.AddBankAccountAsync("Account C", openingBalance: 900m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await CreateReminderAsync(client, ledger, first.Id, category.Id, -10m, Today.AddDays(2));
        await CreateReminderAsync(client, ledger, second.Id, category.Id, -20m, Today.AddDays(3));
        // A source closed since its reminder was set up is still one.
        var closed = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{second.Id}/active", new PatchAccountActiveRequest { Active = false });
        Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync());

        // Every source, by name; the account no reminder is paid from is not projected,
        // nor is the category the reminders pay.
        var all = await ForecastAsync(client, ledger, days: 10);
        Assert.Equal([first.Id, second.Id], all.Accounts.Select(a => a.AccountId));
        Assert.DoesNotContain(all.Accounts, a => a.AccountId == idle.Id || a.AccountId == category.Id);
        Assert.Equal(-70m, all.Accounts[1].Points[^1].Balance);
        var combined = all.Combined!;
        Assert.Equal("USD", combined.CurrencyCode);
        Assert.Equal(50m, combined.Today);
        Assert.Equal(all.Accounts.Sum(a => a.Points[^1].Balance), combined.Points[^1].Balance);
        Assert.Equal(20m, combined.Points[^1].Balance);
        Assert.Equal(new BalanceForecastLowestDto(20m, Today.AddDays(3), Estimated: false), combined.Lowest);
    }

    [Fact]
    public async Task Combined_is_null_when_the_accounts_in_scope_do_not_share_one_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var dollars = await ledger.AddBankAccountAsync("Account A", openingBalance: 100m);
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var euros = await CreateAccountAsync(client, ledger, "Account B", "bank", "EUR", 100m);
        await CreateReminderAsync(client, ledger, dollars.Id, category.Id, -10m, Today.AddDays(2));
        await CreateReminderAsync(client, ledger, euros, category.Id, -10m, Today.AddDays(2));

        var forecast = await ForecastAsync(client, ledger, days: 10);

        Assert.Equal(2, forecast.Accounts.Count);
        Assert.Equal(["EUR", "USD"], forecast.Accounts.Select(a => a.CurrencyCode).Order(StringComparer.Ordinal));
        Assert.Null(forecast.Combined);
        // Each account keeps its own projection.
        Assert.All(forecast.Accounts, a => Assert.Equal(90m, a.Points[^1].Balance));
    }

    [Fact]
    public async Task A_viewer_reads_the_forecast_and_another_ledgers_user_does_not()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var account = await ledger.AddBankAccountAsync("Account A", openingBalance: 100m);
        var category = await ledger.AddCategoryAsync("Category A");
        var viewerId = await ledger.AddMemberAsync("viewer");
        var stranger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using (var owner = await AuthedClientAsync(factory, ledger))
            await InScopeAsync(owner, ledger, account.Id, category.Id);
        using var viewer = await MemberClientAsync(factory, viewerId);
        using var strangerClient = await AuthedClientAsync(factory, stranger);

        var response = await viewer.GetAsync(ForecastUrl(ledger, 10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(account.Id, Assert.Single((await response.Content.ReadFromJsonAsync<BalanceForecastDto>())!.Accounts).AccountId);

        await AssertProblemAsync(await strangerClient.GetAsync(ForecastUrl(ledger, 10)), "ledger-not-visible");
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>The template amount of <see cref="EstimatingTransferAsync"/>'s reminder.</summary>
    private const decimal TemplateTransferAmount = -100m;

    /// <summary>What <see cref="EstimatingTransferAsync"/>'s posted occurrences average to.</summary>
    private const decimal EstimatedAmount = -110m;

    /// <summary>
    /// A monthly reminder from <paramref name="source"/> to <paramref name="other"/> (an
    /// account or a category) that estimates its amount from its three posted
    /// occurrences, the last before today; its next is on day 3.
    /// </summary>
    /// <param name="counterpartyRatio">For an <paramref name="other"/> account in another
    /// currency: each posting's second amount is its amount negated at this ratio, as a
    /// person types it.</param>
    /// <param name="leaveLastOverdue">Post only the first two months, at amounts with the
    /// same mean, so the month before the next occurrence stays overdue (a post marks the
    /// slots before it skipped, never the ones after).</param>
    private async Task<Guid> EstimatingTransferAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, Guid other,
        decimal? counterpartyRatio = null, bool leaveLastOverdue = false)
    {
        var next = Today.AddDays(3);
        var id = await CreateReminderAsync(
            client, ledger, source, other, TemplateTransferAmount, next.AddMonths(-3),
            counterpartyAmount: -TemplateTransferAmount * counterpartyRatio);
        // Posted at amounts whose mean is not the template's.
        var posted = leaveLastOverdue
            ? new[] { (3, -90m), (2, -130m) }
            : new[] { (3, -90m), (2, -110m), (1, -130m) };
        foreach (var (monthsBack, amount) in posted)
        {
            var fired = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{id}/fire/bank",
                new FireBankReminderRequest
                {
                    OccurrenceDate = next.AddMonths(-monthsBack),
                    SourceAccountId = source,
                    Payee = "Payee A",
                    Postings =
                    [
                        new TransactionPosting
                        {
                            CounterpartyAccountId = other, Amount = amount, CounterpartyAmount = -amount * counterpartyRatio,
                        },
                    ],
                });
            Assert.True(fired.IsSuccessStatusCode, await fired.Content.ReadAsStringAsync());
        }
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{id}", new EditReminderRequest { EstimateSampleCount = 3 });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());
        return id;
    }

    private static CreateInvestmentTransactionRequest BuyRequest(
        Guid brokerage, Guid security, DateOnly on, decimal shares, decimal price) => new()
        {
            BrokerageAccountId = brokerage,
            PostedAt = on.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Action = "buy",
            SecurityId = security,
            Shares = shares,
            Price = price,
        };

    /// <summary>A loan account with complete terms. Its escrow line goes to
    /// <paramref name="escrowAccountId"/>, else to a category of its own.</summary>
    private static async Task<Guid> CreateLoanAsync(
        HttpClient client, SyntheticLedger ledger, string currency, Guid? escrowAccountId = null)
    {
        var interest = await ledger.AddCategoryAsync("Category L1");
        var escrow = escrowAccountId ?? (await ledger.AddCategoryAsync("Category L2")).Id;
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts", new CreateAccountRequest
        {
            Name = "Account L",
            AccountType = "loan",
            CurrencyCode = currency,
            OpeningBalance = -100_000m,
            LoanTerms = new LoanTermsDto
            {
                OriginalPrincipal = 100_000m, AnnualInterestRate = 4.00m, PaymentCount = 360, PaymentsPerYear = 12,
                EscrowAmount = 100m, InterestAccountId = interest.Id, EscrowAccountId = escrow,
                PaymentIsComputed = true,
            },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AccountSummary>())!.Id;
    }

    private static async Task<Guid> SetUpLoanPaymentAsync(
        HttpClient client, SyntheticLedger ledger, Guid loan, Guid source, DateOnly start)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{loan}/payment-reminder", new SetupPaymentReminderRequest(source, start));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reminderId").GetGuid();
    }

    private static async Task<Guid> CreateAccountAsync(
        HttpClient client, SyntheticLedger ledger, string name, string type, string currency, decimal opening)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts", new CreateAccountRequest
        {
            Name = name, AccountType = type, CurrencyCode = currency, OpeningBalance = opening,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AccountSummary>())!.Id;
    }

    /// <summary>A monthly reminder on <paramref name="start"/>'s day of the month unless
    /// <paramref name="rrule"/> says otherwise, paid from <paramref name="source"/>.</summary>
    private static async Task<Guid> CreateReminderAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, Guid counterparty, decimal amount, DateOnly start,
        string? rrule = null, int? estimateSampleCount = null, decimal? counterpartyAmount = null)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = rrule ?? $"FREQ=MONTHLY;BYMONTHDAY={start.Day}",
            StartDate = start,
            Payee = "Payee A",
            SourceAccountId = source,
            EstimateSampleCount = estimateSampleCount,
            Postings =
            [
                new TransactionPosting
                {
                    CounterpartyAccountId = counterparty, Amount = amount, CounterpartyAmount = counterpartyAmount,
                },
            ],
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    private static async Task<Guid> PostAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, Guid counterparty, decimal amount, DateOnly on)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions", new CreateTransactionRequest
        {
            PostedAt = on.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Payee = "Payee A",
            SourceAccountId = source,
            Postings = [new TransactionPosting { CounterpartyAccountId = counterparty, Amount = amount }],
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    /// <summary>Import <paramref name="rows"/> onto <paramref name="accountId"/> from an
    /// OFX statement, as a person imports a bank's file.</summary>
    private static async Task ImportAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, params (string FitId, DateOnly On, decimal Amount)[] rows)
    {
        const string AccountNumber = "FORECAST-1";
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(TransactionEntriesEndpointTests.OfxBankStatement(
            AccountNumber, [.. rows.Select(r => (r.FitId, r.On, r.Amount, "PAYEE A"))])));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(file, "file", "statement.qfx");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent($"{TransactionEntriesEndpointTests.BankNumber}:{AccountNumber}"), "providerAccountId");
        var response = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import", content);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The imported headers of the ledger, by date.</summary>
    private async Task<Dictionary<DateOnly, Guid>> ImportedHeadersAsync(SyntheticLedger ledger)
    {
        await using var db = Db.NewDbContext();
        return (await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId && h.ExternalId != null && !h.IsRecurringTemplate)
                .Select(h => new { h.Id, h.PostedAt })
                .ToListAsync())
            .ToDictionary(h => DateOnly.FromDateTime(h.PostedAt), h => h.Id);
    }

    private static async Task<IReadOnlyList<UpcomingOccurrence>> UpcomingAsync(
        HttpClient client, SyntheticLedger ledger, DateOnly from, DateOnly to) =>
        (await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
            $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"))!;

    private static string ForecastUrl(SyntheticLedger ledger, int? days = null) =>
        $"/api/ledgers/{ledger.LedgerId}/accounts/balance-forecast?today={Today:yyyy-MM-dd}"
        + (days is { } d ? $"&days={d}" : "");

    private static async Task<BalanceForecastDto> ForecastAsync(HttpClient client, SyntheticLedger ledger, int? days = null)
    {
        var response = await client.GetAsync(ForecastUrl(ledger, days));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BalanceForecastDto>())!;
    }

    /// <summary>
    /// Put <paramref name="accountId"/> in the forecast's scope without moving it: a
    /// reminder paid from it whose template and first occurrence lie past the widest
    /// window.
    /// </summary>
    private static Task<Guid> InScopeAsync(HttpClient client, SyntheticLedger ledger, Guid accountId, Guid category) =>
        CreateReminderAsync(client, ledger, accountId, category, -1m, Today.AddDays(BalanceForecastRepository.MaxDays + 30));

    /// <summary>The register's running balance on the last row it does not call
    /// Scheduled for <see cref="Today"/>, else the account's opening balance.</summary>
    private async Task<decimal> RegisterCurrentBalanceAsync(HttpClient client, SyntheticLedger ledger, Guid accountId)
    {
        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountId}&today={Today:yyyy-MM-dd}&limit=100"))!;
        var rows = new List<RegisterRowDto>();
        foreach (var entry in page.Entries)
        {
            if (entry.Kind == RegisterEntryDto.KindTxn && entry.Txn is not null) rows.Add(entry.Txn);
            else if (entry.Legs is not null) rows.AddRange(entry.Legs);
        }
        var counted = rows.Where(r => DateOnly.FromDateTime(r.PostedAt) <= Today).ToList();
        if (counted.Count > 0)
        {
            // The cases keep one row per day, so the latest day names one row.
            var latest = counted.Max(r => r.PostedAt);
            return Assert.Single(counted, r => r.PostedAt == latest).BalanceAfter!.Value;
        }
        await using var db = Db.NewDbContext();
        return await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.OpeningBalance).SingleAsync();
    }

    private static async Task<decimal> SidebarBalanceAsync(HttpClient client, SyntheticLedger ledger, Guid accountId) =>
        (await client.GetFromJsonAsync<List<AccountBalanceDto>>(
            $"/api/ledgers/{ledger.LedgerId}/accounts/balances?today={Today:yyyy-MM-dd}"))!
        .Single(b => b.AccountId == accountId).Balance;

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    /// <summary>A signed-in client for a second member of the ledger.</summary>
    private async Task<HttpClient> MemberClientAsync(ApiFactory factory, Guid userId)
    {
        var (plaintext, hash) = Coffer.Api.Auth.Webauthn.SessionService.GenerateCookieValue();
        await using (var db = Db.NewDbContext())
        {
            db.AuthSessions.Add(new SessionRow
            {
                Id = Guid.NewGuid(), UserId = userId, SessionHash = hash,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={plaintext}");
        return client;
    }
}
