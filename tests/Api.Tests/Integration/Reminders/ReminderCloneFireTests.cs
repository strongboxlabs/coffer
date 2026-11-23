using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// The verbatim-clone fire: what auto-post writes for an occurrence, and the only
/// caller it has.
/// </summary>
/// <remarks>
/// The API serves only the UI, and the UI posts an occurrence through the adjusted fire
/// routes, so the clone has no route. It writes unattended, which is why it refuses
/// every template it cannot post as it stands rather than writing something nobody
/// checked.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderCloneFireTests : DatabaseTest
{
    public ReminderCloneFireTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static async Task<Guid> NewBankSeriesAsync(HttpClient client, SyntheticLedger ledger, Guid bankId, Guid catId)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                Payee = "Payee A",
                SourceAccountId = bankId,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = catId, Amount = -40m } },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    private async Task<int> CommittedOccurrenceCountAsync(Guid seriesId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.RecurringTransactionId == seriesId && !h.IsRecurringTemplate);
    }

    [Fact]
    public async Task The_clone_materializes_a_committed_occurrence_that_hits_balances_and_is_idempotent()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("rent");

        var templateId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            // Owned by the bank, the source of its one posting (mig 263).
            var template = TxnHeaderRow.NewTemplate(templateId, ledger.LedgerId, bank.Id, externalId: "mdreminder:t1");
            template.Payee = "Rent";
            template.PostedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            template.TransactedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            db.TxnHeaders.Add(template);
            db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(), HeaderId = templateId, LedgerId = ledger.LedgerId,
                AccountId = bank.Id, PostingIndex = 0, Amount = -1500m,
            });
            db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(), HeaderId = templateId, LedgerId = ledger.LedgerId,
                AccountId = cat.Id, PostingIndex = 0, Amount = 1500m,
            });
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{ledger.LedgerId}",
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", TemplateHeaderId = templateId,
                StartDate = new DateOnly(2026, 1, 1), IsActive = true, Origin = "moneydance_import",
            });
            await db.SaveChangesAsync();
        }
        // Written past every writer: store the presentation as the upgrade's backfill does.
        await ledger.RecomputeNotesAsync(templateId);

        var occurrence = new DateOnly(2026, 3, 1);
        var fired = await ReminderCloneFire.FireAsync(Db, ledger.LedgerId, seriesId, occurrence);
        Assert.Equal(RemindersRepository.FireOutcome.Ok, fired.Outcome);
        var committedId = fired.HeaderId!.Value;
        // The job leaves earlier un-acted occurrences open, so nothing is reported skipped.
        Assert.Equal(0, fired.SkippedEarlierCount);

        await using (var db = Db.NewDbContext())
        {
            // The committed occurrence is a LIVE header, stamped + dated.
            var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == committedId);
            Assert.False(header.IsRecurringTemplate);
            Assert.Equal(seriesId, header.RecurringTransactionId);
            Assert.Equal(occurrence, header.OccurrenceDate);
            Assert.Equal(occurrence, DateOnly.FromDateTime(header.PostedAt));

            // Legs cloned.
            Assert.Equal(2, await db.TxnLegs.AsNoTracking().CountAsync(l => l.HeaderId == committedId));

            // KEYSTONE inverse: the fired occurrence DOES get a balance row
            // (it's live now), unlike its template.
            var bal = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(b => b.HeaderId == committedId && b.AccountId == bank.Id);
            Assert.Equal(-1500m, bal.BalanceAfter);
            Assert.Equal(0, await db.TxnHeaderAccountBalances.AsNoTracking()
                .CountAsync(b => b.HeaderId == templateId));   // template still has none

            // The earlier Jan 1 and Feb 1 stay un-acted and un-skipped, so the cursor
            // stays on the earliest of them rather than passing them.
            Assert.Equal(0, await db.RecurringOccurrenceExceptions.AsNoTracking()
                .CountAsync(e => e.RecurringTransactionId == seriesId));
            var series = await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == seriesId);
            Assert.Equal(new DateOnly(2026, 1, 1), series.NextDueDate);
        }

        // Idempotent: firing the same occurrence again returns the same header.
        var again = await ReminderCloneFire.FireAsync(Db, ledger.LedgerId, seriesId, occurrence);
        Assert.Equal(RemindersRepository.FireOutcome.Ok, again.Outcome);
        Assert.Equal(committedId, again.HeaderId);
        Assert.Equal(1, await CommittedOccurrenceCountAsync(seriesId));
    }

    [Fact]
    public async Task The_clone_fire_has_no_route()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var cat = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewBankSeriesAsync(client, ledger, bank.Id, cat.Id);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire",
            new { occurrenceDate = "2026-01-01" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));

        // The routes the UI posts through are still served: the 404 above is the
        // route's absence, not the group's.
        var adjusted = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = new DateOnly(2026, 1, 1), SourceAccountId = bank.Id,
                Postings = [new TransactionPosting { CounterpartyAccountId = cat.Id, Amount = -40m }],
            });
        Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_clone_fire_refuses_a_template_using_an_inactive_account_and_writes_nothing(bool retireSource)
    {
        // The rule every by-hand post applies: a new transaction does not land on a
        // retired account. The job sets such a series aside before firing; this is the
        // write's own refusal, for an account retired after the job's scan.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var cat = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewBankSeriesAsync(client, ledger, bank.Id, cat.Id);
        var retired = retireSource ? bank.Id : cat.Id;
        var deactivate = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{retired}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);
        DateOnly? cursorBefore;
        await using (var db = Db.NewServiceDbContext())
            cursorBefore = await db.RecurringTransactions.AsNoTracking()
                .Where(r => r.Id == seriesId).Select(r => r.NextDueDate).SingleAsync();

        var result = await ReminderCloneFire.FireAsync(Db, ledger.LedgerId, seriesId, new DateOnly(2026, 1, 1));

        Assert.Equal(RemindersRepository.FireOutcome.AccountInactive, result.Outcome);
        Assert.Null(result.HeaderId);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));
        await using (var db = Db.NewServiceDbContext())
            Assert.Equal(cursorBefore, await db.RecurringTransactions.AsNoTracking()
                .Where(r => r.Id == seriesId).Select(r => r.NextDueDate).SingleAsync());
    }

    [Fact]
    public async Task The_clone_fire_refuses_an_investment_template_and_writes_nothing()
    {
        // An investment occurrence means holdings and lots, which only the investment
        // create builds; a cloned investment template would carry its legs without them.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var bank = await ledger.AddBankAccountAsync("Account A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1),
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = "transfer", TransferAccountId = bank.Id,
                    Amount = 75m, PostedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;

        var result = await ReminderCloneFire.FireAsync(Db, ledger.LedgerId, seriesId, new DateOnly(2026, 1, 1));

        Assert.Equal(RemindersRepository.FireOutcome.ShapeMismatch, result.Outcome);
        Assert.Null(result.HeaderId);
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));
    }

    // ---- transfers between currencies (ADR-0114 D13) ------------------------------------

    private async Task<IReadOnlyList<TxnLegRow>> LegsOfAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync();
    }

    private async Task<Guid> TemplateOfAsync(Guid seriesId)
    {
        await using var db = Db.NewDbContext();
        return (await db.RecurringTransactions.AsNoTracking().SingleAsync(r => r.Id == seriesId)).TemplateHeaderId!.Value;
    }

    private static decimal On(IReadOnlyList<TxnLegRow> legs, Guid accountId) =>
        Assert.Single(legs, l => l.AccountId == accountId).Amount;

    private static async Task<Guid> NewTransferSeriesAsync(
        HttpClient client, SyntheticLedger ledger, Guid sourceId, Guid otherId, decimal amount, decimal otherAmount)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                Payee = "Payee A",
                SourceAccountId = sourceId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = otherId, Amount = amount, CounterpartyAmount = otherAmount },
                },
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    [Fact]
    public async Task A_reminder_between_currencies_keeps_both_amounts_through_create_edit_and_the_clone_fire()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var seriesId = await NewTransferSeriesAsync(client, ledger, usd.Id, eur.Id, -200m, 183.4m);
        var template = await LegsOfAsync(await TemplateOfAsync(seriesId));
        Assert.Equal((-200m, 183.4m), (On(template, usd.Id), On(template, eur.Id)));

        // An edit that resends the line by id, with a new payee, keeps both.
        var sourceLeg = Assert.Single(template, l => l.AccountId == usd.Id);
        var edit = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}",
            new EditReminderRequest
            {
                Payee = "Payee B",
                Postings = new PatchReminderPostings
                {
                    SourceAccountId = usd.Id,
                    Items = [new TransactionPosting
                    {
                        LegId = sourceLeg.Id, CounterpartyAccountId = eur.Id, Amount = -200m, CounterpartyAmount = 183.4m,
                    }],
                },
            });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
        var edited = await LegsOfAsync(await TemplateOfAsync(seriesId));
        Assert.Equal((-200m, 183.4m), (On(edited, usd.Id), On(edited, eur.Id)));

        // The clone the auto-post job writes copies the legs as they stand.
        var committed = await ReminderCloneFire.PostedAsync(Db, ledger.LedgerId, seriesId, new DateOnly(2026, 2, 1));
        var occurrence = await LegsOfAsync(committed);
        Assert.Equal((-200m, 183.4m), (On(occurrence, usd.Id), On(occurrence, eur.Id)));
    }

    [Fact]
    public async Task The_adjusted_bank_fire_keeps_both_amounts_of_a_transfer_between_currencies()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewTransferSeriesAsync(client, ledger, usd.Id, eur.Id, -200m, 183.4m);

        // Posted with the second amount corrected in Post or skip.
        var fire = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = new DateOnly(2026, 1, 1),
                SourceAccountId = usd.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = eur.Id, Amount = -200m, CounterpartyAmount = 181.9m },
                },
            });
        Assert.True(fire.IsSuccessStatusCode, await fire.Content.ReadAsStringAsync());
        var headerId = (await fire.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId;

        var legs = await LegsOfAsync(headerId);
        Assert.Equal((-200m, 181.9m), (On(legs, usd.Id), On(legs, eur.Id)));
    }

    // A managed loan payment's priced lines are replaced by the computed split; a line it
    // does not price, here a transfer to an account in another currency added in Post or
    // skip, keeps both its amounts.
    [Fact]
    public async Task The_managed_loan_fire_keeps_both_amounts_of_a_line_the_split_does_not_price()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        var interest = await ledger.AddCategoryAsync("Category A", "expense");
        var escrow = await ledger.AddBankAccountAsync("Account C", currencyCode: "USD");
        var loanId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            db.Accounts.Add(new AccountRow
            {
                Id = loanId, LedgerId = ledger.LedgerId, Name = "Loan A",
                AccountType = "loan", CurrencyCode = "USD", OpeningBalance = -100000m, IsActive = true,
            });
            await db.SaveChangesAsync();
            db.LoanTerms.Add(new LoanTermsRow
            {
                AccountId = loanId, LedgerId = ledger.LedgerId,
                OriginalPrincipal = 120000m, AnnualInterestRate = 4.00m, Points = 0m,
                PaymentCount = 360, PaymentsPerYear = 12,
                EscrowAmount = 100m, InterestAccountId = interest.Id, EscrowAccountId = escrow.Id,
                PaymentIsComputed = true, FixedPayment = null,
            });
            await db.SaveChangesAsync();
        }
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var start = new DateOnly(2026, 1, 10);
        var setup = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{loanId}/payment-reminder",
            new SetupPaymentReminderRequest(checking.Id, start));
        setup.EnsureSuccessStatusCode();
        var reminderId = (await setup.Content.ReadFromJsonAsync<ReminderIdResponse>())!.ReminderId;

        var fire = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminderId}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = start,
                SourceAccountId = checking.Id,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = loanId, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = interest.Id, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = escrow.Id, Amount = -1m },
                    new TransactionPosting { CounterpartyAccountId = eur.Id, Amount = -50m, CounterpartyAmount = 45.85m },
                },
            });
        Assert.True(fire.IsSuccessStatusCode, await fire.Content.ReadAsStringAsync());
        var headerId = (await fire.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId;

        var legs = await LegsOfAsync(headerId);
        Assert.Equal(45.85m, On(legs, eur.Id));
        // The split still priced its own lines.
        Assert.Equal(100m, On(legs, escrow.Id));
        Assert.Single(legs, l => l.AccountId == checking.Id && l.PostingIndex == 3 && l.Amount == -50m);
    }

    // ---- a posting between currencies the leg-pair builder refuses (ADR-0114 D13) -------
    //
    // Each bank reminder write builds its legs through the builder, which refuses a posting
    // between currencies sent without its second amount. The route answers with that 422,
    // writes nothing, and leaves the ledger's auto-post schedule as it was.

    private const string SecondAmountRequired = "transaction-posting-counterparty-amount-required";

    private static async Task<string?> RefusedCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("code").GetString();
    }

    private async Task<bool> AutoPostScheduledAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.ScheduledJobs.AsNoTracking()
            .AnyAsync(j => j.LedgerId == ledgerId && j.JobType == Coffer.Api.Scheduling.JobTypes.ReminderAutoPost);
    }

    private async Task<int> SeriesCountAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.RecurringTransactions.AsNoTracking().CountAsync(r => r.LedgerId == ledgerId);
    }

    private async Task<int> HeaderCountAsync(Guid ledgerId)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == ledgerId);
    }

    [Fact]
    public async Task A_reminder_create_between_currencies_without_the_second_amount_is_refused_and_schedules_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = new DateOnly(2026, 1, 1),
                AutoCommitDaysBefore = 2,
                Payee = "Payee A",
                SourceAccountId = usd.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = eur.Id, Amount = -200m } },
            });

        Assert.Equal(SecondAmountRequired, await RefusedCodeAsync(response));
        Assert.Equal(0, await SeriesCountAsync(ledger.LedgerId));
        Assert.Equal(0, await HeaderCountAsync(ledger.LedgerId));
        Assert.False(await AutoPostScheduledAsync(ledger.LedgerId));
    }

    [Fact]
    public async Task A_reminder_edit_between_currencies_without_the_second_amount_is_refused_and_leaves_the_template()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewTransferSeriesAsync(client, ledger, usd.Id, eur.Id, -200m, 183.4m);
        var sourceLeg = Assert.Single(await LegsOfAsync(await TemplateOfAsync(seriesId)), l => l.AccountId == usd.Id);

        var response = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}",
            new EditReminderRequest
            {
                AutoCommitDaysBefore = 2,
                Payee = "Payee B",
                Postings = new PatchReminderPostings
                {
                    SourceAccountId = usd.Id,
                    Items = [new TransactionPosting { LegId = sourceLeg.Id, CounterpartyAccountId = eur.Id, Amount = -250m }],
                },
            });

        Assert.Equal(SecondAmountRequired, await RefusedCodeAsync(response));
        var templateId = await TemplateOfAsync(seriesId);
        var template = await LegsOfAsync(templateId);
        Assert.Equal((-200m, 183.4m), (On(template, usd.Id), On(template, eur.Id)));
        await using (var db = Db.NewServiceDbContext())
            Assert.Equal("Payee A", (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == templateId)).Payee);
        Assert.False(await AutoPostScheduledAsync(ledger.LedgerId));
    }

    [Fact]
    public async Task A_bank_fire_between_currencies_without_the_second_amount_is_refused_and_posts_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var seriesId = await NewTransferSeriesAsync(client, ledger, usd.Id, eur.Id, -200m, 183.4m);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/reminders/{seriesId}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = new DateOnly(2026, 1, 1),
                SourceAccountId = usd.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = eur.Id, Amount = -200m } },
            });

        Assert.Equal(SecondAmountRequired, await RefusedCodeAsync(response));
        Assert.Equal(0, await CommittedOccurrenceCountAsync(seriesId));
        Assert.False(await AutoPostScheduledAsync(ledger.LedgerId));
    }

    private sealed record ReminderIdResponse(Guid ReminderId);
}
