using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// A currency change is refused when stored figures carry more places than the new
/// currency allows, and the refusal names how many do not fit (ADR-0114 D15): <c>count</c>,
/// the transactions with a leg on the account that does not fit, a category line counted
/// with the leg it is paired with, never twice (ADR-0114 D15); and
/// <c>accountFigures</c>, the opening balance and the loan terms, named apart. Nothing is
/// written. Otherwise every figure is kept.
/// </summary>
/// <remarks>
/// Written through the API on synthetic ledgers. Three states come from writers the API
/// does not offer and are seeded as those writers stored them: an account kept in a code
/// that is no longer accepted, which works at two places; a category line that does not
/// mirror its real line, as the released Moneydance import kept one; and a posting between
/// two categories, which only that import writes. One such posting is also held for review,
/// as a promote-on-clear holds a merge winner, so it can be folded into another.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CurrencyChangeFitTests : DatabaseTest
{
    public CurrencyChangeFitTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string PlacesCode = "relabel-places-exceed-currency";
    private static readonly DateTime PostedAt = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task<Guid> ExpenseAsync(HttpClient client, SyntheticLedger ledger, Guid account, Guid category, decimal amount)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions", new CreateTransactionRequest
        {
            PostedAt = PostedAt,
            Payee = "Payee A",
            SourceAccountId = account,
            Postings = [new TransactionPosting { CounterpartyAccountId = category, Amount = amount }],
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("headerId").GetGuid();
    }

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, UpdateAccountRequest body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{accountId}")
        {
            Content = JsonContent.Create(body),
        });

    /// <summary>The refusal's count, account-level figures (comma-joined), currency and
    /// places.</summary>
    private static async Task<(int Count, string AccountFigures, string? Currency, int Places)> RefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(PlacesCode, root.GetProperty("code").GetString());
        return (root.GetProperty("count").GetInt32(),
            string.Join(",", root.GetProperty("accountFigures").EnumerateArray().Select(e => e.GetString())),
            root.GetProperty("currencyCode").GetString(), root.GetProperty("places").GetInt32());
    }

    private static void AssertOk(HttpResponseMessage response) =>
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}");

    private async Task<AccountRow> AccountAsync(Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
    }

    private async Task<IReadOnlyList<TxnLegRow>> LegsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync();
    }

    private static decimal On(IReadOnlyList<TxnLegRow> legs, Guid accountId) =>
        Assert.Single(legs, l => l.AccountId == accountId).Amount;

    [Fact]
    public async Task A_line_with_cents_and_its_category_mirror_count_as_one_transaction()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var headerId = await ExpenseAsync(client, ledger, usd.Id, category.Id, -12.34m);

        Assert.Equal((1, "", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, usd.Id, new UpdateAccountRequest { CurrencyCode = "JPY" })));
        Assert.Equal("USD", (await AccountAsync(usd.Id)).CurrencyCode);
        Assert.Equal((-12.34m, 12.34m), (On(await LegsAsync(headerId), usd.Id), On(await LegsAsync(headerId), category.Id)));
    }

    // A category line takes the real line's currency (ADR-0114 D13), so one that does not
    // mirror its real line is relabelled with it and judged at its places.
    [Fact]
    public async Task A_category_line_that_does_not_fit_counts_its_transaction_though_the_real_line_fits()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        var (_, categoryLeg) = await ledger.AddTransactionPairAsync(usd.Id, category.Id, -12m, PostedAt, payee: "Payee A");
        await SetLegAmountAsync(ledger, categoryLeg, 12.34m);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        Assert.Equal((1, "", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, usd.Id, new UpdateAccountRequest { CurrencyCode = "JPY" })));
        Assert.Equal("USD", (await AccountAsync(usd.Id)).CurrencyCode);
    }

    [Fact]
    public async Task Whole_figures_change_to_a_whole_unit_currency_and_two_place_ones_to_a_three_place_one()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var whole = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var cents = await ledger.AddBankAccountAsync("Account B", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var wholeId = await ExpenseAsync(client, ledger, whole.Id, category.Id, -12m);
        var centsId = await ExpenseAsync(client, ledger, cents.Id, category.Id, -12.34m);

        AssertOk(await PatchAsync(client, ledger, whole.Id, new UpdateAccountRequest { CurrencyCode = "JPY" }));
        AssertOk(await PatchAsync(client, ledger, cents.Id, new UpdateAccountRequest { CurrencyCode = "KWD" }));

        Assert.Equal(("JPY", "KWD"), ((await AccountAsync(whole.Id)).CurrencyCode, (await AccountAsync(cents.Id)).CurrencyCode));
        Assert.Equal(-12m, On(await LegsAsync(wholeId), whole.Id));
        Assert.Equal(-12.34m, On(await LegsAsync(centsId), cents.Id));
    }

    [Fact]
    public async Task An_opening_balance_that_does_not_fit_is_named_alone()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", openingBalance: 12.34m, currencyCode: "USD");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        Assert.Equal((0, "opening_balance", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, usd.Id, new UpdateAccountRequest { CurrencyCode = "JPY" })));
        // Resent unchanged beside the change, it is still the stored figure that does not fit.
        Assert.Equal((0, "opening_balance", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, usd.Id,
                new UpdateAccountRequest { CurrencyCode = "JPY", OpeningBalance = 12.34m })));
        Assert.Equal(("USD", 12.34m), ((await AccountAsync(usd.Id)).CurrencyCode, (await AccountAsync(usd.Id)).OpeningBalance));

        // An edit that makes it fit in the same request passes.
        AssertOk(await PatchAsync(client, ledger, usd.Id, new UpdateAccountRequest { CurrencyCode = "JPY", OpeningBalance = 12m }));
        Assert.Equal(("JPY", 12m), ((await AccountAsync(usd.Id)).CurrencyCode, (await AccountAsync(usd.Id)).OpeningBalance));
    }

    [Fact]
    public async Task Loan_terms_that_do_not_fit_are_named_with_the_transactions()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var loan = await ledger.AddAccountAsync("Loan A", "loan", currencyCode: "USD");
        var bank = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var terms = new LoanTermsDto
        {
            OriginalPrincipal = 1000.5m,
            AnnualInterestRate = 4m,
            PaymentCount = 360,
            PaymentsPerYear = 12,
            EscrowAmount = 0m,
            PaymentIsComputed = true,
        };
        AssertOk(await PatchAsync(client, ledger, loan.Id, new UpdateAccountRequest { LoanTerms = terms }));
        // A payment with cents onto the loan, from a bank account in its currency.
        await ExpenseAsync(client, ledger, bank.Id, loan.Id, -0.5m);

        Assert.Equal((1, "loan_terms", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, loan.Id, new UpdateAccountRequest { CurrencyCode = "JPY" })));
        // The editor resends the full terms: unchanged, they are still the stored figures.
        Assert.Equal((1, "loan_terms", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, loan.Id, new UpdateAccountRequest { CurrencyCode = "JPY", LoanTerms = terms })));
        Assert.Equal("USD", (await AccountAsync(loan.Id)).CurrencyCode);
    }

    // A code stored before the rule keeps working at two places (ADR-0114 D15), so its
    // figures with cents fit it and do not fit a whole-unit currency.
    [Fact]
    public async Task An_account_kept_in_a_code_no_longer_accepted_is_judged_at_two_places()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var stored = await ledger.AddBankAccountAsync("Account A", currencyCode: "XAU");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ExpenseAsync(client, ledger, stored.Id, category.Id, -12.34m);

        Assert.Equal((1, "", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, stored.Id, new UpdateAccountRequest { CurrencyCode = "JPY" })));
        Assert.Equal("XAU", (await AccountAsync(stored.Id)).CurrencyCode);
        AssertOk(await PatchAsync(client, ledger, stored.Id, new UpdateAccountRequest { CurrencyCode = "EUR" }));
    }

    // A posting between two categories has no real side, so its lines read in the category's
    // own code (ADR-0114 D15). A category takes only the reporting currency, so its code
    // changes after the ledger's does, and that change is judged like an account's.
    [Fact]
    public async Task A_category_holding_a_posting_between_categories_with_cents_cannot_change_to_a_whole_unit_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var fractional = await ledger.AddCategoryAsync("Category A");
        var other = await ledger.AddCategoryAsync("Category B");
        var whole = await ledger.AddCategoryAsync("Category C");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var (fractionalLeg, _) = await ledger.AddTransactionPairAsync(fractional.Id, other.Id, 12.34m, PostedAt, payee: "Payee A");
        await ledger.AddTransactionPairAsync(whole.Id, other.Id, 12m, PostedAt, payee: "Payee A");
        Guid headerId;
        await using (var db = Db.NewDbContext())
            headerId = (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == fractionalLeg)).HeaderId;
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}")
            {
                Content = JsonContent.Create(new UpdateLedgerRequest { CurrencyCode = "JPY" }),
            })).StatusCode);

        Assert.Equal((1, "", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, fractional.Id, new UpdateAccountRequest { CurrencyCode = "JPY" })));
        Assert.Equal("USD", (await AccountAsync(fractional.Id)).CurrencyCode);
        Assert.Equal((12.34m, -12.34m), (On(await LegsAsync(headerId), fractional.Id), On(await LegsAsync(headerId), other.Id)));

        // The anchor: a category whose lines between categories are whole changes.
        AssertOk(await PatchAsync(client, ledger, whole.Id, new UpdateAccountRequest { CurrencyCode = "JPY" }));
        Assert.Equal("JPY", (await AccountAsync(whole.Id)).CurrencyCode);
    }

    // A category line beside a real line takes the real side's currency (ADR-0114 D13), so
    // a category's own code change leaves it alone, cents and all.
    [Fact]
    public async Task A_category_line_beside_a_real_line_does_not_hold_back_the_categorys_code_change()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await ExpenseAsync(client, ledger, usd.Id, category.Id, -12.34m);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}")
            {
                Content = JsonContent.Create(new UpdateLedgerRequest { CurrencyCode = "JPY" }),
            })).StatusCode);

        AssertOk(await PatchAsync(client, ledger, category.Id, new UpdateAccountRequest { CurrencyCode = "JPY" }));
        Assert.Equal("JPY", (await AccountAsync(category.Id)).CurrencyCode);
    }

    /// <summary>
    /// No screen shows or opens a transaction merged into another, so a figure of one that
    /// does not fit the new currency does not refuse the change (decided by the owner,
    /// 2026-11-17; ADR-0114 D15). The same figure on a live row does, and the merged row's
    /// figure is kept as it is.
    /// </summary>
    /// <remarks>Every step runs through the API: a row entered by hand, a statement
    /// import's row awaiting review with cents, the import's row folded into the entered
    /// one, and the change.</remarks>
    [Fact]
    public async Task A_row_merged_into_another_does_not_hold_back_a_currency_change()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var winner = await ExpenseAsync(client, ledger, usd.Id, category.Id, -12m);
        var loser = await ImportOneRowAsync(client, ledger, usd.Id, -12.34m);

        // The anchor: on a live row, the figure with cents refuses the change.
        Assert.Equal((1, "", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, usd.Id, new UpdateAccountRequest { CurrencyCode = "JPY" })));

        using (var merged = await client.PostAsJsonAsync(
                   $"/api/ledgers/{ledger.LedgerId}/transactions/{loser}/merge",
                   new MergeTransactionRequest { FromHeaderId = winner }))
            Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());

        AssertOk(await PatchAsync(client, ledger, usd.Id, new UpdateAccountRequest { CurrencyCode = "JPY" }));

        Assert.Equal("JPY", (await AccountAsync(usd.Id)).CurrencyCode);
        Assert.Equal(-12.34m, On(await LegsAsync(loser), usd.Id));
    }

    /// <summary>
    /// Only a transaction merged into another is left out of the count (decided by the
    /// owner, 2026-11-17; ADR-0114 D15). A hidden row and a reminder's template can each be
    /// opened and edited to fit, so a figure of either that does not fit the new currency
    /// still refuses the change, beside a merged-away row that does not count.
    /// </summary>
    /// <remarks>Every step runs through the API: a statement import's two rows awaiting
    /// review, one folded into a row entered by hand and one deleted, which hides an
    /// imported row; and a reminder entered with cents.</remarks>
    [Fact]
    public async Task A_hidden_row_and_a_reminders_template_still_hold_back_a_currency_change_a_merged_away_row_does_not()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var winner = await ExpenseAsync(client, ledger, usd.Id, category.Id, -12m);
        var imported = await ImportRowsAsync(client, ledger, usd.Id, [("RELABEL-MERGED", -12.34m), ("RELABEL-HIDDEN", -5.67m)]);
        var (loser, hidden) = (imported[0], imported[1]);

        using (var merged = await client.PostAsJsonAsync(
                   $"/api/ledgers/{ledger.LedgerId}/transactions/{loser}/merge",
                   new MergeTransactionRequest { FromHeaderId = winner }))
            Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());
        using (var deleted = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{hidden}"))
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using (var reminder = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
               {
                   Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                   StartDate = new DateOnly(2026, 1, 1),
                   Payee = "Payee B",
                   SourceAccountId = usd.Id,
                   Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1.5m }],
               }))
            Assert.Equal(HttpStatusCode.Created, reminder.StatusCode);
        await using (var db = Db.NewDbContext())
        {
            var states = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId)
                .Select(h => new { h.Id, h.IsMergedInto, h.IsHidden, h.IsRecurringTemplate })
                .ToListAsync();
            Assert.Equal(winner, Assert.Single(states, s => s.Id == loser).IsMergedInto);
            Assert.True(Assert.Single(states, s => s.Id == hidden).IsHidden);
            Assert.Single(states, s => s.IsRecurringTemplate);
        }

        // The hidden row and the template, not the merged-away row.
        Assert.Equal((2, "", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, usd.Id, new UpdateAccountRequest { CurrencyCode = "JPY" })));
        Assert.Equal("USD", (await AccountAsync(usd.Id)).CurrencyCode);
    }

    /// <summary>
    /// A posting between categories of a transaction merged into another is left out of a
    /// category's code change, as a real line of one is (decided by the owner, 2026-11-17;
    /// ADR-0114 D15). The same figure refuses the change while the row is live.
    /// </summary>
    /// <remarks>The posting between categories is seeded as the released import wrote one.
    /// It is held for review, as a promote-on-clear holds a merge winner whose loser cleared
    /// at another figure: only such a row folds into another. The merge and the changes run
    /// through the API.</remarks>
    [Fact]
    public async Task A_posting_between_categories_merged_into_another_does_not_hold_back_the_categorys_code_change()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var fractional = await ledger.AddCategoryAsync("Category A");
        var other = await ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var winner = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(fractional.Id, other.Id, 12m, PostedAt, payee: "Payee A")).FromTxnId);
        var loser = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(fractional.Id, other.Id, 12.34m, PostedAt, payee: "Payee B")).FromTxnId);
        await using (var seed = Db.NewServiceDbContext())
        {
            await seed.TxnHeaders.Where(h => h.Id == loser)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.NeedsReview, true));
        }
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}")
            {
                Content = JsonContent.Create(new UpdateLedgerRequest { CurrencyCode = "JPY" }),
            })).StatusCode);

        // The anchor: on a live row, the line with cents refuses the change.
        Assert.Equal((1, "", "JPY", 0),
            await RefusalAsync(await PatchAsync(client, ledger, fractional.Id, new UpdateAccountRequest { CurrencyCode = "JPY" })));

        using (var merged = await client.PostAsJsonAsync(
                   $"/api/ledgers/{ledger.LedgerId}/transactions/{loser}/merge",
                   new MergeTransactionRequest { FromHeaderId = winner }))
            Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());
        await using (var db = Db.NewDbContext())
            Assert.Equal(winner, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);

        AssertOk(await PatchAsync(client, ledger, fractional.Id, new UpdateAccountRequest { CurrencyCode = "JPY" }));

        Assert.Equal("JPY", (await AccountAsync(fractional.Id)).CurrencyCode);
        Assert.Equal((12.34m, -12.34m), (On(await LegsAsync(loser), fractional.Id), On(await LegsAsync(loser), other.Id)));
    }

    /// <summary>Import one row of <paramref name="amount"/> onto <paramref name="accountId"/>
    /// from an OFX statement, as a person imports a bank's file, and return the row it
    /// writes, awaiting review.</summary>
    private async Task<Guid> ImportOneRowAsync(HttpClient client, SyntheticLedger ledger, Guid accountId, decimal amount) =>
        (await ImportRowsAsync(client, ledger, accountId, [("RELABEL-MERGED", amount)]))[0];

    /// <summary>Import <paramref name="rows"/> onto <paramref name="accountId"/> from one
    /// OFX statement, as a person imports a bank's file, and return the rows it writes, in
    /// order, each awaiting review.</summary>
    private async Task<IReadOnlyList<Guid>> ImportRowsAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, IReadOnlyList<(string FitId, decimal Amount)> rows)
    {
        const string AccountNumber = "RELABEL-1";
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(Transactions.TransactionEntriesEndpointTests.OfxBankStatement(
            AccountNumber, rows.Select(r => (r.FitId, DateOnly.FromDateTime(PostedAt), r.Amount, "PAYEE A")).ToList())));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ofx");
        content.Add(file, "file", "statement.qfx");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent($"{Transactions.TransactionEntriesEndpointTests.BankNumber}:{AccountNumber}"), "providerAccountId");
        using var response = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import", content);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        await using var db = Db.NewDbContext();
        var ids = new List<Guid>();
        foreach (var (fitId, _) in rows)
        {
            var row = await db.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.ExternalId == fitId);
            Assert.True(row.NeedsReview);
            ids.Add(row.Id);
        }
        return ids;
    }

    /// <summary>Set one stored leg's amount, as the released import could leave a category
    /// line, through the entity under the ledger write lock.</summary>
    private async Task SetLegAmountAsync(SyntheticLedger ledger, Guid legId, decimal amount)
    {
        await using var db = Db.NewDbContext();
        await using var write = await db.BeginLedgerWriteAsync(ledger.LedgerId);
        var leg = await db.TxnLegs.SingleAsync(l => l.Id == legId);
        leg.Amount = amount;
        await db.SaveChangesAsync();
        await write.CommitAsync();
    }
}
