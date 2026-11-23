using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// A bank reminder's template lines are typed amounts, checked at their currency's places
/// (ADR-0114 D15), on create and on an edit, where a line resent equal to its stored figure
/// passes. A fire is a new write: whatever it would post is checked, by hand (Post or skip)
/// and by the verbatim clone auto-post writes, so a template figure stored before each
/// currency kept its own places is refused when it is posted, and nothing is written.
/// </summary>
/// <remarks>
/// A template line finer than its currency is seeded by rewriting a created template's two
/// legs, as the released writers, which held every currency to two places, could store it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderMinorUnitsTests : DatabaseTest
{
    public ReminderMinorUnitsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string PlacesCode = "amount-places-exceed-currency";
    private static readonly DateOnly Occurrence = new(2026, 1, 1);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static Task<HttpResponseMessage> CreateSeriesAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, Guid category, decimal amount) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = Occurrence,
            Payee = "Payee A",
            SourceAccountId = source,
            Postings = new[] { new TransactionPosting { CounterpartyAccountId = category, Amount = amount } },
        });

    /// <summary>The refusal's code, currency, places and field.</summary>
    private static async Task<(string? Code, string? Currency, int Places, string? Field)> RefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return (root.GetProperty("code").GetString(), root.GetProperty("currencyCode").GetString(),
            root.GetProperty("places").GetInt32(), root.GetProperty("field").GetString());
    }

    private async Task<int> SeriesCountAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.RecurringTransactions.AsNoTracking().CountAsync(r => r.LedgerId == ledgerId);
    }

    private async Task<int> OccurrenceCountAsync(Guid seriesId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.RecurringTransactionId == seriesId && !h.IsRecurringTemplate);
    }

    private async Task<IReadOnlyList<TxnLegRow>> TemplateLegsAsync(Guid seriesId)
    {
        await using var db = Db.NewServiceDbContext();
        var templateId = (await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == seriesId)).TemplateHeaderId!.Value;
        return await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == templateId).ToListAsync();
    }

    /// <summary>A whole-unit series whose one template line is stored with two places.</summary>
    private async Task<(SyntheticLedger Ledger, Guid Bank, Guid Category, Guid Series)> UnfitSeriesAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", currencyCode: "JPY");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var own = await ClientAsync(factory, ledger);
        var created = await CreateSeriesAsync(own, ledger, bank.Id, category.Id, -12m);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        await using var db = Db.NewServiceDbContext();
        var templateId = (await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == seriesId)).TemplateHeaderId!.Value;
        await db.TxnLegs.Where(l => l.HeaderId == templateId && l.AccountId == bank.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Amount, -12.34m));
        await db.TxnLegs.Where(l => l.HeaderId == templateId && l.AccountId == category.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Amount, 12.34m));
        return (ledger, bank.Id, category.Id, seriesId);
    }

    [Fact]
    public async Task A_template_line_finer_than_its_currency_is_refused_on_create_and_nothing_is_written()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", currencyCode: "JPY");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        Assert.Equal((PlacesCode, "JPY", 0, "postings[0].amount"),
            await RefusalAsync(await CreateSeriesAsync(client, ledger, bank.Id, category.Id, -1.5m)));
        Assert.Equal(0, await SeriesCountAsync(ledger.LedgerId));

        Assert.Equal(HttpStatusCode.Created, (await CreateSeriesAsync(client, ledger, bank.Id, category.Id, -2m)).StatusCode);
    }

    [Fact]
    public async Task An_edit_that_resends_a_stored_line_that_does_not_fit_passes_and_a_changed_line_is_refused()
    {
        var (ledger, bank, category, seriesId) = await UnfitSeriesAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var sourceLeg = Assert.Single(await TemplateLegsAsync(seriesId), l => l.AccountId == bank);

        Task<HttpResponseMessage> EditAsync(Guid legId, string payee, decimal amount) =>
            client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}", new EditReminderRequest
            {
                Payee = payee,
                Postings = new PatchReminderPostings
                {
                    SourceAccountId = bank,
                    Items = [new TransactionPosting { LegId = legId, CounterpartyAccountId = category, Amount = amount }],
                },
            });

        var resent = await EditAsync(sourceLeg.Id, "Payee B", -12.34m);
        Assert.True(resent.IsSuccessStatusCode, $"{(int)resent.StatusCode}: {await resent.Content.ReadAsStringAsync()}");
        // The save rebuilt the lines with new ids; the stored figure is kept.
        var rebuilt = Assert.Single(await TemplateLegsAsync(seriesId), l => l.AccountId == bank);
        Assert.Equal(-12.34m, rebuilt.Amount);

        Assert.Equal((PlacesCode, "JPY", 0, "postings.items[0].amount"),
            await RefusalAsync(await EditAsync(rebuilt.Id, "Payee C", -12.35m)));
        Assert.Equal(-12.34m, Assert.Single(await TemplateLegsAsync(seriesId), l => l.AccountId == bank).Amount);
    }

    [Fact]
    public async Task Post_or_skip_refuses_a_template_figure_that_does_not_fit_even_when_it_is_sent_unchanged()
    {
        var (ledger, bank, category, seriesId) = await UnfitSeriesAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        Task<HttpResponseMessage> FireAsync(decimal amount) =>
            client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire/bank", new FireBankReminderRequest
            {
                OccurrenceDate = Occurrence,
                SourceAccountId = bank,
                Payee = "Payee A",
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = category, Amount = amount } },
            });

        // A fire is a new write: the template's own figure is posted only if it fits.
        Assert.Equal((PlacesCode, "JPY", 0, "postings[0].amount"), await RefusalAsync(await FireAsync(-12.34m)));
        Assert.Equal(0, await OccurrenceCountAsync(seriesId));

        var posted = await FireAsync(-12m);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal(1, await OccurrenceCountAsync(seriesId));
    }

    [Fact]
    public async Task The_clone_auto_post_fires_refuses_a_template_figure_that_does_not_fit_and_posts_nothing()
    {
        var (ledger, _, _, seriesId) = await UnfitSeriesAsync();

        var result = await ReminderCloneFire.FireAsync(Db, ledger.LedgerId, seriesId, Occurrence);

        Assert.Equal(RemindersRepository.FireOutcome.TemplateAmountDoesNotFit, result.Outcome);
        Assert.Equal("JPY", result.UnfitCurrencyCode);
        Assert.Equal(0, await OccurrenceCountAsync(seriesId));
    }

    /// <summary>
    /// A whole-unit managed loan and its payment reminder: the loan, an interest category,
    /// a real escrow account and the paying account, all in the one currency, the loan's
    /// payment computed. The template lines fit; the loan split replaces them at fire.
    /// </summary>
    private async Task<(SyntheticLedger Ledger, Guid Loan, Guid Series)> WholeUnitLoanSeriesAsync(
        int? autoPostDays = null)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var paying = await ledger.AddBankAccountAsync("Account A", currencyCode: "JPY");
        var interest = await ledger.AddCategoryAsync("Category A", "expense");
        var escrow = await ledger.AddBankAccountAsync("Account B", currencyCode: "JPY");
        var loanId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            db.Accounts.Add(new AccountRow
            {
                Id = loanId, LedgerId = ledger.LedgerId, Name = "Loan A",
                AccountType = "loan", CurrencyCode = "JPY", OpeningBalance = -400000m, IsActive = true,
            });
            await db.SaveChangesAsync();
            db.LoanTerms.Add(new LoanTermsRow
            {
                AccountId = loanId, LedgerId = ledger.LedgerId,
                OriginalPrincipal = 500000m, AnnualInterestRate = 4.00m, Points = 0m,
                PaymentCount = 360, PaymentsPerYear = 12,
                EscrowAmount = 500m, InterestAccountId = interest.Id, EscrowAccountId = escrow.Id,
                PaymentIsComputed = true, FixedPayment = null,
            });
            await db.SaveChangesAsync();
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = Occurrence,
            AutoCommitDaysBefore = autoPostDays,
            Payee = "Payee A",
            SourceAccountId = paying.Id,
            Postings = new[]
            {
                new TransactionPosting { CounterpartyAccountId = loanId, Amount = -1m },
                new TransactionPosting { CounterpartyAccountId = interest.Id, Amount = -1m },
                new TransactionPosting { CounterpartyAccountId = escrow.Id, Amount = -500m },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        // A managed loan reminder, as the importer marks one.
        await using (var db = Db.NewDbContext())
        {
            await db.RecurringTransactions.Where(r => r.Id == seriesId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsLoanReminder, true));
        }
        return (ledger, loanId, seriesId);
    }

    [Fact]
    public async Task The_clone_fire_refuses_a_stored_loan_escrow_that_does_not_fit_and_posts_nothing()
    {
        var (ledger, loanId, seriesId) = await WholeUnitLoanSeriesAsync();

        // The anchor: with whole-unit terms the clone posts the loan split.
        await ReminderCloneFire.PostedAsync(Db, ledger.LedgerId, seriesId, Occurrence);
        Assert.Equal(1, await OccurrenceCountAsync(seriesId));

        // The escrow as the released loan-terms writer, which held every currency to two
        // places, could store it. The split copies it into the escrow line as it stands.
        await using (var db = Db.NewDbContext())
        {
            await db.LoanTerms.Where(t => t.AccountId == loanId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.EscrowAmount, 500.5m));
        }

        var result = await ReminderCloneFire.FireAsync(Db, ledger.LedgerId, seriesId, Occurrence.AddMonths(1));

        Assert.Equal(RemindersRepository.FireOutcome.TemplateAmountDoesNotFit, result.Outcome);
        Assert.Equal("JPY", result.UnfitCurrencyCode);
        Assert.Null(result.HeaderId);
        Assert.Equal(1, await OccurrenceCountAsync(seriesId));
    }

    [Fact]
    public async Task The_agenda_says_a_loan_series_whose_stored_escrow_does_not_fit_never_posts_by_itself()
    {
        // The loan split replaces every template line, so only the stored escrow can make
        // the fire refuse; the agenda reads it by the fire's own rule.
        var (ledger, loanId, seriesId) = await WholeUnitLoanSeriesAsync(autoPostDays: 3);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        async Task<string?> ReasonAsync() =>
            Assert.Single((await client.GetFromJsonAsync<List<UpcomingOccurrence>>(
                    $"/api/ledgers/{ledger.LedgerId}/reminders/upcoming?from=2026-01-01&to=2026-01-01"))!,
                o => o.ReminderId == seriesId).AutoPost!.ExcludedReason;

        // The anchor: whole-unit terms post by themselves.
        Assert.Null(await ReasonAsync());

        await using (var db = Db.NewDbContext())
        {
            await db.LoanTerms.Where(t => t.AccountId == loanId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.EscrowAmount, 500.5m));
        }
        Assert.Equal(Coffer.Api.Reminders.AutoPostExclusion.TemplateAmountDoesNotFit, await ReasonAsync());
    }

    /// <summary>A buy in <paramref name="brokerage"/> with the given amount and fee.</summary>
    private static CreateInvestmentTransactionRequest WholeUnitBuy(
        AccountRow brokerage, Guid security, Guid fees, decimal amount, decimal fee) => new()
    {
        BrokerageAccountId = brokerage.Id, Action = "buy",
        PostedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        SecurityId = security, Shares = 10m, Price = 100m, Amount = amount,
        FeeAccountId = fees, FeeAmount = fee,
    };

    // An investment reminder's refusal names the currency, its places and the field, as the
    // live investment create's does: the template create, the template edit and the fire
    // each reach the shared investment validation, and each says what it refused.
    [Fact]
    public async Task An_investment_reminder_refusal_names_the_currency_places_and_field_on_create_edit_and_fire()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "JPY");
        var security = await ledger.AddSecurityAsync("Security A", "SECA");
        var fees = (await ledger.AddCategoryAsync("Category A", "expense")).Id;
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        Task<HttpResponseMessage> CreateAsync(decimal amount, decimal fee) =>
            client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/investment",
                new CreateInvestmentReminderRequest
                {
                    Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                    StartDate = Occurrence,
                    Transaction = WholeUnitBuy(brokerage, security, fees, amount, fee),
                });

        Assert.Equal((PlacesCode, "JPY", 0, "feeAmount"), await RefusalAsync(await CreateAsync(1000m, 0.5m)));
        Assert.Equal(0, await SeriesCountAsync(ledger.LedgerId));

        var created = await CreateAsync(1000m, 5m);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        var edited = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/investment",
            new EditInvestmentReminderRequest { Transaction = WholeUnitBuy(brokerage, security, fees, 1000.5m, 5m) });
        Assert.Equal((PlacesCode, "JPY", 0, "amount"), await RefusalAsync(edited));

        var fired = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire/investment",
            new FireInvestmentReminderRequest
            {
                OccurrenceDate = Occurrence,
                Transaction = WholeUnitBuy(brokerage, security, fees, 1000.5m, 5m),
            });
        Assert.Equal((PlacesCode, "JPY", 0, "amount"), await RefusalAsync(fired));
        Assert.Equal(0, await OccurrenceCountAsync(seriesId));
    }

    /// <summary>A transfer line from a two-place account to a whole-unit one, which carries
    /// two amounts (ADR-0114 D13).</summary>
    private static TransactionPosting CrossCurrencyLine(Guid wholeUnit, decimal second) =>
        new() { CounterpartyAccountId = wholeUnit, Amount = -100m, CounterpartyAmount = second };

    private async Task<(SyntheticLedger Ledger, Guid Source, Guid WholeUnit)> CrossCurrencyLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var wholeUnit = await ledger.AddBankAccountAsync("Account B", currencyCode: "JPY");
        return (ledger, source.Id, wholeUnit.Id);
    }

    private static Task<HttpResponseMessage> CreateTransferSeriesAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, Guid wholeUnit, decimal second) =>
        client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = Occurrence,
            Payee = "Payee A",
            SourceAccountId = source,
            Postings = new[] { CrossCurrencyLine(wholeUnit, second) },
        });

    // A template's second amount is typed in the counterparty's currency and held to its
    // places, as the live create's is (ADR-0114 D15).
    [Fact]
    public async Task A_template_second_amount_finer_than_its_currency_is_refused_on_create_and_nothing_is_written()
    {
        var (ledger, source, wholeUnit) = await CrossCurrencyLedgerAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        Assert.Equal((PlacesCode, "JPY", 0, "postings[0].counterpartyAmount"),
            await RefusalAsync(await CreateTransferSeriesAsync(client, ledger, source, wholeUnit, 1.5m)));
        Assert.Equal(0, await SeriesCountAsync(ledger.LedgerId));

        Assert.Equal(HttpStatusCode.Created,
            (await CreateTransferSeriesAsync(client, ledger, source, wholeUnit, 15000m)).StatusCode);
    }

    [Fact]
    public async Task Post_or_skip_refuses_a_second_amount_finer_than_its_currency_and_posts_nothing()
    {
        var (ledger, source, wholeUnit) = await CrossCurrencyLedgerAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var created = await CreateTransferSeriesAsync(client, ledger, source, wholeUnit, 15000m);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        Task<HttpResponseMessage> FireAsync(decimal second) =>
            client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire/bank", new FireBankReminderRequest
            {
                OccurrenceDate = Occurrence,
                SourceAccountId = source,
                Payee = "Payee A",
                Postings = new[] { CrossCurrencyLine(wholeUnit, second) },
            });

        Assert.Equal((PlacesCode, "JPY", 0, "postings[0].counterpartyAmount"), await RefusalAsync(await FireAsync(1.5m)));
        Assert.Equal(0, await OccurrenceCountAsync(seriesId));

        Assert.Equal(HttpStatusCode.OK, (await FireAsync(15000m)).StatusCode);
        Assert.Equal(1, await OccurrenceCountAsync(seriesId));
    }

    [Fact]
    public async Task The_clone_fire_posts_a_template_whose_figures_fit()
    {
        // The anchor for the refusal above: the same series, its line whole.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A", currencyCode: "JPY");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var created = await CreateSeriesAsync(client, ledger, bank.Id, category.Id, -12m);
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        await ReminderCloneFire.PostedAsync(Db, ledger.LedgerId, seriesId, Occurrence);
        Assert.Equal(1, await OccurrenceCountAsync(seriesId));
    }
}
