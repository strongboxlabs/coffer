using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// ADR-0043 — the per-account frequent-counterparties endpoint that
/// pins a source account's most-used accounts + categories to the
/// top of the picker. Verifies the history-derived ranking, the
/// per-kind cap, and the system/inactive exclusions.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FrequentCounterpartiesTests
{
    private readonly PostgresFixture _fixture;

    public FrequentCounterpartiesTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    [Fact]
    public async Task Ranks_by_use_count_splits_by_kind_caps_at_three_and_excludes_inactive()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddBankAccountAsync("Source Checking");

        // Four expense categories used 4 / 3 / 2 / 1 times.
        var catA = await ledger.AddCategoryAsync("Cat A", "expense");
        var catB = await ledger.AddCategoryAsync("Cat B", "expense");
        var catC = await ledger.AddCategoryAsync("Cat C", "expense");
        var catD = await ledger.AddCategoryAsync("Cat D", "expense");
        // One asset-account counterparty used twice.
        var acct = await ledger.AddBankAccountAsync("Savings");
        // A heavily-used but INACTIVE category — must be excluded.
        var catHidden = await ledger.AddCategoryAsync("Cat Hidden", "expense");

        var day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        async Task UseAsync(Guid counterparty, int times)
        {
            for (var i = 0; i < times; i++)
                await ledger.AddTransactionPairAsync(
                    source.Id, counterparty, 10m, day.AddDays(i));
        }
        await UseAsync(catA.Id, 4);
        await UseAsync(catB.Id, 3);
        await UseAsync(catC.Id, 2);
        await UseAsync(catD.Id, 1);
        await UseAsync(acct.Id, 2);
        await UseAsync(catHidden.Id, 10);

        // Deactivate the heavily-used category.
        await using (var db = _fixture.NewDbContext())
        {
            await db.Accounts
                .Where(a => a.Id == catHidden.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false));
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{source.Id}/frequent-counterparties");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<FrequentCounterpartiesResponse>();
        Assert.NotNull(body);

        // Categories: top 3 by use count, ranked; D (1 use) drops off
        // the cap; the inactive Cat Hidden is excluded entirely.
        Assert.Equal(
            new[] { "Cat A", "Cat B", "Cat C" },
            body!.Categories.Select(c => c.Name).ToArray());
        Assert.Equal(4, body.Categories[0].UseCount);
        Assert.DoesNotContain(body.Categories, c => c.Name == "Cat Hidden");
        Assert.All(body.Categories, c => Assert.Equal("category", c.AccountType));

        // Accounts: the one asset counterparty.
        var acctRow = Assert.Single(body.Accounts);
        Assert.Equal("Savings", acctRow.Name);
        Assert.Equal(2, acctRow.UseCount);
        Assert.NotEqual("category", acctRow.AccountType);
    }

    [Fact]
    public async Task Counts_only_the_posting_paired_counterparty_not_co_occurring_split_legs()
    {
        // Replicate a paycheck split: Checking is the primary, paired
        // posting-by-posting with the 401(k) contribution, a tax
        // category, and a wage-income category. Only the leg sharing
        // the source account's posting_index is its counterparty —
        // the tax / wage legs sit on other postings (paired with
        // Checking, not the source) and must NOT surface as the
        // source's frequents (ADR-0043).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddBankAccountAsync("401k-ish Source");
        var checking = await ledger.AddBankAccountAsync("Checking");
        var tax = await ledger.AddCategoryAsync("Federal Income Tax", "expense");
        var wage = await ledger.AddCategoryAsync("Wages", "income");

        var day = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
        {
            await ledger.AddMultiSplitAsync(
                checking.Id,
                new[]
                {
                    (source.Id, 100m),  // posting 0: Checking ↔ source
                    (tax.Id, 20m),      // posting 1: Checking ↔ tax
                    (wage.Id, -120m),   // posting 2: Checking ↔ wage
                },
                day.AddDays(i));
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{source.Id}/frequent-counterparties");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<FrequentCounterpartiesResponse>();
        Assert.NotNull(body);

        // The source's posting-paired counterparty is Checking only.
        var acct = Assert.Single(body!.Accounts);
        Assert.Equal("Checking", acct.Name);
        Assert.Equal(3, acct.UseCount);
        // The co-occurring split legs (tax, wage) are NOT counterparties
        // of the source — they paired with Checking on other postings.
        Assert.DoesNotContain(body.Categories, c => c.Name == "Federal Income Tax");
        Assert.DoesNotContain(body.Categories, c => c.Name == "Wages");
        Assert.Empty(body.Categories);
    }

    [Fact]
    public async Task Dilutes_split_counterparties_so_a_singleton_pick_outranks_a_more_used_split_category()
    {
        // A category used only inside a multi-counterparty split should
        // not crowd out one the user picks on simple one-off
        // transactions — even when the split category lands in MORE
        // headers. Each transaction contributes ~1 unit of "intent"
        // spread across its counterparties: a 2-way split gives each
        // category 1/2, a singleton gives a full 1 (ADR-0043 dilution).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddBankAccountAsync("Source Checking");

        // Two categories that only ever appear together in a 2-way split.
        var split1 = await ledger.AddCategoryAsync("Split One", "expense");
        var split2 = await ledger.AddCategoryAsync("Split Two", "expense");
        // One category the user picks on plain one-off transactions.
        var single = await ledger.AddCategoryAsync("Single Pick", "expense");

        var day = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        // 8 split headers: each split category is in 8 headers (UseCount
        // 8) but diluted by 2 → score ≈ 8 × w/2 = 4w.
        for (var i = 0; i < 8; i++)
        {
            await ledger.AddMultiSplitAsync(
                source.Id,
                new[] { (split1.Id, 10m), (split2.Id, 10m) },
                day.AddDays(i));
        }
        // 5 singletons: UseCount 5, undiluted → score 5w. Fewer headers,
        // higher rank.
        for (var i = 0; i < 5; i++)
            await ledger.AddTransactionPairAsync(
                source.Id, single.Id, 10m, day.AddDays(i));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{source.Id}/frequent-counterparties");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<FrequentCounterpartiesResponse>();
        Assert.NotNull(body);

        // The singleton category ranks FIRST despite the lowest raw use
        // count of the three — dilution sinks the split-only categories.
        Assert.Equal("Single Pick", body!.Categories[0].Name);
        Assert.Equal(5, body.Categories[0].UseCount);
        // Raw usage is still reported honestly: the split categories
        // landed in more headers (8 each), yet rank below the singleton.
        var split1Row = Assert.Single(body.Categories, c => c.Name == "Split One");
        var split2Row = Assert.Single(body.Categories, c => c.Name == "Split Two");
        Assert.Equal(8, split1Row.UseCount);
        Assert.Equal(8, split2Row.UseCount);
    }

    [Fact]
    public async Task Returns_422_when_account_not_in_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var other = await SyntheticLedger.CreateAsync(_fixture);
        var foreign = await other.AddBankAccountAsync("Foreign");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{foreign.Id}/frequent-counterparties");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }
}
