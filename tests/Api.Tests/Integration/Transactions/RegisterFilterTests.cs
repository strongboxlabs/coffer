using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Server-side register filtering + search. Each dimension pushes into the
/// register's entry-key query so the windowed keyset cursor walks ONLY matching entries
/// — the property client-side filtering can't provide. The pagination test is
/// the load-bearing one: a filter with more matches than the page size must
/// return every match across pages and never leak a non-match.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RegisterFilterTests : DatabaseTest
{
    public RegisterFilterTests(PostgresFixture fixture, ITestOutputHelper output)
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

    private static DateTime Day(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Search_matches_payee_case_insensitively()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 50m, Day(2026, 1, 10), "WAREHOUSE");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 60m, Day(2026, 1, 11), "BOOKSHOP");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 70m, Day(2026, 1, 12), "Warehouse Fuel");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&search=warehouse"))!;

        // Both "WAREHOUSE" (upper) and "Warehouse Fuel" match the case-insensitive
        // ILIKE; "BOOKSHOP" is excluded.
        Assert.Equal(2, page.Entries.Count);
        Assert.DoesNotContain(page.Entries, e => e.Txn!.Payee == "BOOKSHOP");
        Assert.All(page.Entries, e => Assert.Contains("arehouse", e.Txn!.Payee!.ToLowerInvariant()));
    }

    [Fact]
    public async Task Date_range_bounds_are_inclusive()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m, Day(2026, 1, 15), "jan");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 20m, Day(2026, 2, 15), "feb");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 30m, Day(2026, 3, 15), "mar");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&date_from=2026-02-01&date_to=2026-02-28"))!;

        var entry = Assert.Single(page.Entries);
        Assert.Equal("feb", entry.Txn!.Payee);
    }

    [Fact]
    public async Task Amount_range_filters_by_magnitude()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 50m, Day(2026, 1, 10), "small");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 150m, Day(2026, 1, 11), "mid");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 500m, Day(2026, 1, 12), "big");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&amount_min=100&amount_max=200"))!;

        var entry = Assert.Single(page.Entries);
        Assert.Equal("mid", entry.Txn!.Payee);
    }

    /// <summary>
    /// A split entered from one account with two lines of opposite sign onto another is
    /// two rows on that other account, each drawn with its own line's amount, while both
    /// carry the transaction's net there. The amount range keeps the row whose shown
    /// amount is in range (ADR-0108 D2), never both rows by the net they share.
    /// </summary>
    [Fact]
    public async Task Amount_range_reads_the_amount_each_line_on_another_account_shows()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");
        var accountB = await ledger.AddBankAccountAsync("Account B");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // From Account A: a line into Account B and a smaller one back out of it. On B the
        // rows show the larger and the smaller magnitude; the net they share lies between.
        const decimal LineIn = -6m, LineBack = 1m, Threshold = 2m;
        await CreateSplitAsync(client, ledger.LedgerId, accountA.Id, Day(2026, 1, 10), "Payee A",
            (accountB.Id, LineIn), (accountB.Id, LineBack));
        var larger = -LineIn;
        var smaller = -LineBack;
        var net = larger + smaller;

        var register = $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountB.Id}&limit=100";

        // Anchor: unfiltered, B shows the two rows, each with its own line's amount and
        // both with the net, which is above the threshold while one row's magnitude is not.
        var all = (await client.GetFromJsonAsync<RegisterPage>(register))!;
        Assert.Equal(2, all.Entries.Count);
        Assert.All(all.Entries, e => Assert.Equal(RegisterEntryDto.KindTxn, e.Kind));
        Assert.Equal(new[] { smaller, larger }, all.Entries.Select(e => e.Txn!.Amount).Order());
        Assert.All(all.Entries, e => Assert.Equal(net, e.Txn!.HeaderAccountNetAmount));
        Assert.True(Math.Abs(smaller) < Threshold && Threshold < Math.Abs(net) && Math.Abs(net) < Math.Abs(larger));
        var largerRow = all.Entries.Single(e => e.Txn!.Amount == larger).Txn!.Id;
        var smallerRow = all.Entries.Single(e => e.Txn!.Amount == smaller).Txn!.Id;

        var atLeast = (await client.GetFromJsonAsync<RegisterPage>(
            $"{register}&amount_min={Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))!;
        Assert.Equal(new[] { largerRow }, atLeast.Entries.Select(e => e.Txn!.Id));

        var atMost = (await client.GetFromJsonAsync<RegisterPage>(
            $"{register}&amount_max={Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))!;
        Assert.Equal(new[] { smallerRow }, atMost.Entries.Select(e => e.Txn!.Id));
    }

    /// <summary>
    /// A split a category owns, paid from accounts in different currencies, shows the
    /// mixed marker on the category's register, not the stored net, which adds its lines
    /// as they are (ADR-0114 D7). A filter matches only what the row shows (ADR-0108 D2),
    /// so no amount range keeps it: not one around that net, nor one around a line. The
    /// same split paid in one currency shows its net and is kept by the range around it.
    /// </summary>
    [Fact]
    public async Task Amount_range_keeps_no_category_split_paid_in_more_than_one_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A", currencyCode: "USD");
        var accountB = await ledger.AddBankAccountAsync("Account B", currencyCode: "CAD");
        var category = await ledger.AddCategoryAsync("Category A");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Lines of two magnitudes whose net lies in a range that holds neither line.
        const decimal LineA = 10m, LineB = 20m;
        await CreateCategorySplitAsync(client, ledger.LedgerId, category.Id, Day(2026, 1, 10), "Payee A",
            (accountA.Id, LineA), (accountB.Id, LineB));
        await CreateCategorySplitAsync(client, ledger.LedgerId, category.Id, Day(2026, 1, 11), "Payee B",
            (accountA.Id, LineA), (accountA.Id, LineB));

        var register = $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={category.Id}&limit=100";
        static string Range(decimal min, decimal max) =>
            $"&amount_min={min.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&amount_max={max.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        // Anchor: unfiltered, each split is one group on the category, the first with its
        // lines in two currencies and the second in one, both carrying the same net.
        var all = (await client.GetFromJsonAsync<RegisterPage>(register))!;
        Assert.Equal(2, all.Entries.Count);
        Assert.All(all.Entries, e => Assert.Equal(RegisterEntryDto.KindGroup, e.Kind));
        var mixed = all.Entries.Single(e => e.Legs![0].Payee == "Payee A");
        var single = all.Entries.Single(e => e.Legs![0].Payee == "Payee B");
        Assert.Equal(new[] { "CAD", "USD" }, mixed.Legs!.Select(l => l.CurrencyCode).Order());
        Assert.All(single.Legs!, l => Assert.Equal("USD", l.CurrencyCode));
        Assert.All(all.Entries.SelectMany(e => e.Legs!), l => Assert.Equal(LineA + LineB, l.HeaderAccountNetAmount));

        // Around the net: the one-currency split is kept, the mixed one is not.
        var aroundNet = (await client.GetFromJsonAsync<RegisterPage>(register + Range(LineA + LineB - 5m, LineA + LineB + 5m)))!;
        Assert.Equal(new[] { "Payee B" }, aroundNet.Entries.Select(e => e.Legs![0].Payee));

        // At one line's magnitude: neither, since each row shows a total or the marker.
        var atLine = (await client.GetFromJsonAsync<RegisterPage>(register + Range(LineB, LineB)))!;
        Assert.Empty(atLine.Entries);
    }

    /// <summary>
    /// A split the category owns, as its register's editor writes one: the first line
    /// entered from its paying account, then reshaped from the category into one line per
    /// paying account, each with the category's amount.
    /// </summary>
    private async Task CreateCategorySplitAsync(
        HttpClient client, Guid ledgerId, Guid categoryId, DateTime postedAt, string payee,
        params (Guid Paying, decimal Amount)[] lines)
    {
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = payee,
                SourceAccountId = lines[0].Paying,
                Postings = [new TransactionPosting { CounterpartyAccountId = categoryId, Amount = -lines[0].Amount }],
            });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"create: {(int)created.StatusCode} {body}");
        Guid headerId;
        using (var doc = JsonDocument.Parse(body)) headerId = doc.RootElement.GetProperty("headerId").GetGuid();
        Guid categoryLeg;
        await using (var db = Db.NewDbContext())
            categoryLeg = await db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == headerId && l.AccountId == categoryId)
                .Select(l => l.Id)
                .SingleAsync();

        using var reshaped = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
            $"/api/ledgers/{ledgerId}/transactions/{headerId}")
        {
            Content = JsonContent.Create(new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = categoryId,
                    Items = [.. lines.Select((l, i) => new TransactionPosting
                    {
                        LegId = i == 0 ? categoryLeg : null,
                        CounterpartyAccountId = l.Paying,
                        Amount = l.Amount,
                    })],
                },
            }),
        });
        Assert.True(reshaped.StatusCode == HttpStatusCode.OK,
            $"reshape: {(int)reshaped.StatusCode} {await reshaped.Content.ReadAsStringAsync()}");
    }

    /// <summary>A transaction entered through the register's create route, one posting per line.</summary>
    private static async Task CreateSplitAsync(
        HttpClient client, Guid ledgerId, Guid sourceAccountId, DateTime postedAt, string payee,
        params (Guid Counterparty, decimal Amount)[] lines)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = payee,
                SourceAccountId = sourceAccountId,
                Postings = [.. lines.Select(l => new TransactionPosting { CounterpartyAccountId = l.Counterparty, Amount = l.Amount })],
            });
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"create: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Status_scheduled_matches_future_dated_only()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m, Day(2020, 1, 1), "past");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 20m, Day(2030, 1, 1), "future");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var scheduled = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&status=scheduled&today=2026-07-13"))!;
        var entry = Assert.Single(scheduled.Entries);
        Assert.Equal("future", entry.Txn!.Payee);
    }

    [Fact]
    public async Task Tag_filter_matches_tagged_entries()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        var (taggedLeg, _) = await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m, Day(2026, 1, 10), "supplies");
        // A one-split's tags live on a leg, never on its header: the register's own.
        await ledger.AddLegTagAsync(taggedLeg, "Project A");
        await ledger.AddTransactionPairAsync(acct.Id, other.Id, 20m, Day(2026, 1, 11), "untagged");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&tag={Uri.EscapeDataString("Project A")}"))!;

        var entry = Assert.Single(page.Entries);
        Assert.Equal("supplies", entry.Txn!.Payee);
    }

    [Fact]
    public async Task Category_filter_matches_entries_posting_to_that_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var acct = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var dining = await ledger.AddCategoryAsync("Dining");
        await ledger.AddTransactionPairAsync(acct.Id, groceries.Id, 40m, Day(2026, 1, 10), "market");
        await ledger.AddTransactionPairAsync(acct.Id, dining.Id, 25m, Day(2026, 1, 11), "cafe");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=100&category_id={groceries.Id}"))!;

        var entry = Assert.Single(page.Entries);
        Assert.Equal("market", entry.Txn!.Payee);
    }

    [Fact]
    public async Task Security_filter_matches_entries_involving_that_security()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var other = await ledger.AddBankAccountAsync("checking");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var buy = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = Day(2026, 1, 10),
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                Amount = 1000m,
            });
        Assert.Equal(HttpStatusCode.Created, buy.StatusCode);
        // A non-security cash deposit into the brokerage — must NOT match.
        await ledger.AddTransactionPairAsync(brokerage.Id, other.Id, 500m, Day(2026, 1, 11), "cash deposit");

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={brokerage.Id}&limit=100&security_id={securityId}"))!;

        Assert.NotEmpty(page.Entries);
        Assert.All(page.Entries, e => Assert.NotEqual("cash deposit", e.Txn?.Payee));
    }

    [Fact]
    public async Task Filter_pages_across_the_cursor_returning_all_matches_and_no_non_matches()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");

        // 12 matches + 12 non-matches, interleaved by date so any window
        // straddles both — the filter must survive the keyset walk.
        const int matches = 12;
        for (var i = 0; i < matches; i++)
        {
            await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m + i, Day(2026, 1, 1).AddDays(i * 2), "WAREHOUSE");
            await ledger.AddTransactionPairAsync(acct.Id, other.Id, 10m + i, Day(2026, 1, 1).AddDays(i * 2 + 1), "BOOKSHOP");
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var seen = 0;
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var url = $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={acct.Id}&limit=5&search=warehouse"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}&direction=before");
            var page = (await client.GetFromJsonAsync<RegisterPage>(url))!;
            Assert.All(page.Entries, e => Assert.Equal("WAREHOUSE", e.Txn!.Payee));
            seen += page.Entries.Count;
            cursor = page.CursorForOlder;
            if (cursor is null) break;
        }

        Assert.Equal(matches, seen);
    }

    [Fact]
    public async Task A_parent_categorys_register_is_empty_until_subcategories_are_included()
    {
        // A rollup parent holds NO postings of its own — they are all on its
        // children — so without subcategories its register shows nothing while
        // every other screen reports a total against it. True from the
        // Categories tree and the sidebar too, not just the budget link.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var taxes = await ledger.AddCategoryAsync("Taxes");
        var federal = await ledger.AddCategoryAsync("Federal", parentId: taxes.Id);
        var medicare = await ledger.AddCategoryAsync("Medicare", parentId: federal.Id);

        var when = new DateTime(2026, 4, 3, 0, 30, 0, DateTimeKind.Utc);
        await ledger.AddTransactionPairAsync(bank.Id, federal.Id, -100m, when, payee: "fed");
        await ledger.AddTransactionPairAsync(bank.Id, medicare.Id, -20m, when, payee: "medicare");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var url = $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={taxes.Id}";

        var narrow = await client.GetFromJsonAsync<JsonElement>(url);
        Assert.Empty(narrow.GetProperty("entries").EnumerateArray());

        // ...and with the subtree, the grandchild comes too — the walk is not one
        // level deep.
        // Repository first, so a failure here says WHICH layer is wrong.
        var repo = new RegisterRepository(Db.NewDbContext(), TimeProvider.System);
        var direct = await repo.GetPageAsync(
            ledger.LedgerId, taxes.Id, cursor: null, direction: "older", startingAtHeaderId: null,
            limit: 50, hidden: false, filter: new RegisterFilter(IncludeSubcategories: true));
        Assert.Equal(2, direct.Entries.Count);

        var wide = await client.GetFromJsonAsync<JsonElement>(url + "&include_subcategories=true");
        Assert.Equal(2, wide.GetProperty("entries").EnumerateArray().Count());
    }
}
