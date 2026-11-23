using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// The Home Watchlist's budget rows (ADR-0114, "The FU-106 inventory, corrected"). The
/// widget draws each watched category from <c>GET /budget/progress</c>, the read This
/// month uses (<c>WatchlistWidget.tsx</c>, through <c>useBudgetProgress</c>): a row's
/// spend, its mark and how far past the mark it is. That read converts, so the rows do:
/// spending in another currency at each posting's date, into the ledger's reporting
/// currency, and a row that needs a missing rate has no figure and names it (ADR-0114 D9).
/// </summary>
/// <remarks>
/// Spending is written through <c>POST /transactions</c> and the target through
/// <c>PUT /budget/targets</c>; rates as the ledger's owner, the rate writers' role.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class WatchlistBudgetRowsConversionTests : DatabaseTest
{
    public WatchlistBudgetRowsConversionTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Jan(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_watchlist_read_converts_each_rows_spend_at_its_posting_dates_and_fails_closed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var dollars = await ledger.AddBankAccountAsync("Account A");
        var euros = await ledger.AddBankAccountAsync("Account B", currencyCode: "EUR");
        var pounds = await ledger.AddBankAccountAsync("Account C", currencyCode: "GBP");
        var watched = await ledger.AddCategoryAsync("Category A");
        var unpriced = await ledger.AddCategoryAsync("Category B");
        await using (var owner = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            foreach (var (day, rate) in new[] { (1, 1.1m), (10, 1.25m) })
            {
                owner.FxRates.Add(new FxRateRow
                {
                    Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD",
                    RateDate = new DateOnly(2026, 1, day), Rate = rate, Source = FxRateSource.Frankfurter,
                    Upstream = "Source A",
                });
            }
            owner.FxPairCoverage.Add(new FxPairCoverageRow
            {
                LedgerId = ledger.LedgerId, FromCurrency = "EUR", ToCurrency = "USD",
                FetchedThroughDate = new DateOnly(2026, 1, 31),
            });
            await owner.SaveChangesAsync();   // no GBP rate at all
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");

        async Task SpendAsync(AccountRow from, AccountRow on, int day, decimal amount)
        {
            using var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = Jan(day),
                    SourceAccountId = from.Id,
                    Postings = [new TransactionPosting { CounterpartyAccountId = on.Id, Amount = -amount }],
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }
        await SpendAsync(euros, watched, 5, 10m);     // at the 1st's rate
        await SpendAsync(dollars, watched, 8, 4m);    // as it stands
        await SpendAsync(euros, watched, 12, 20m);    // at the 10th's rate
        await SpendAsync(dollars, unpriced, 6, 3m);
        await SpendAsync(pounds, unpriced, 15, 9m);   // no GBP rate
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/budget/targets",
            new SetBudgetTargetRequest(watched.Id, "2026-01", 100m))).StatusCode);

        // The widget's own request: the month of its today, its window, and its today.
        var progress = (await client.GetFromJsonAsync<BudgetProgressDto>(
            $"/api/ledgers/{ledger.LedgerId}/budget/progress?month=2026-01&window=3&today=2026-01-20"))!;

        Assert.Equal("USD", progress.CurrencyCode);
        var row = Assert.Single(progress.Rows, r => r.CategoryId == watched.Id);
        Assert.Equal(40m, row.Actual);
        Assert.Equal(100m, row.Mark);
        Assert.Null(row.RateUnavailable);

        var failed = Assert.Single(progress.Rows, r => r.CategoryId == unpriced.Id);
        Assert.Null(failed.Actual);
        Assert.Equal([new RateUnavailable("GBP", "USD", new DateOnly(2026, 1, 15))], failed.RateUnavailable!);
    }
}
