using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Column sort (mig 166): the register can be ordered by a chosen column +
/// direction, not just posted-date DESC. The load-bearing test is
/// <see cref="Keyset_pagination_is_gap_free_under_a_non_date_sort"/> — it drives
/// the dynamic-SQL keyset cursor across page boundaries under an amount sort, so
/// a bad cursor predicate (skipped / duplicated / reordered entries) surfaces
/// here against a live DB. Assertions key on payee order (sign/precision-
/// independent) so they don't bake in the amount-sign convention.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RegisterSortTests : DatabaseTest
{
    public RegisterSortTests(PostgresFixture fixture, ITestOutputHelper output)
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

    // Payees deliberately NOT in the same order as amounts or dates, so a working
    // sort must actually reorder — it can't coincidentally match insertion order.
    private static readonly (decimal Amount, string Payee)[] Rows =
    {
        (50m, "elm"),
        (10m, "cedar"),
        (30m, "ash"),
        (20m, "dogwood"),
        (40m, "birch"),
    };

    private async Task<(SyntheticLedger Ledger, Guid AccountId)> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var acct = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        var day = 10;
        foreach (var (amount, payee) in Rows)
            await ledger.AddTransactionPairAsync(acct.Id, other.Id, amount, Day(2026, 1, day++), payee);
        return (ledger, acct.Id);
    }

    private static List<string> Payees(RegisterPage page) =>
        page.Entries.Select(e => e.Txn!.Payee!).ToList();

    private async Task<RegisterPage> FetchAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, string query) =>
        (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountId}&{query}"))!;

    [Fact]
    public async Task Sort_by_payee_orders_alphabetically()
    {
        var (ledger, acct) = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var asc = await FetchAsync(client, ledger, acct, "limit=100&sort=payee&dir=asc");
        Assert.Equal(new[] { "ash", "birch", "cedar", "dogwood", "elm" }, Payees(asc));
    }

    [Fact]
    public async Task Sort_by_amount_desc_is_the_reverse_of_asc()
    {
        var (ledger, acct) = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var ascPayees = Payees(await FetchAsync(client, ledger, acct, "limit=100&sort=amount&dir=asc"));
        var descPayees = Payees(await FetchAsync(client, ledger, acct, "limit=100&sort=amount&dir=desc"));

        Assert.Equal(Rows.Length, ascPayees.Count);
        // Distinct amounts ⇒ a total order ⇒ desc is exactly asc reversed. Proves
        // both the sort column AND direction without assuming the amount sign.
        Assert.Equal(ascPayees.AsEnumerable().Reverse().ToList(), descPayees);
        Assert.NotEqual(ascPayees, descPayees); // guard against a no-op sort
    }

    [Fact]
    public async Task Keyset_pagination_is_gap_free_under_a_non_date_sort()
    {
        var (ledger, acct) = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The reference order: the whole set in one page, amount-desc.
        var full = Payees(await FetchAsync(client, ledger, acct, "limit=100&sort=amount&dir=desc"));

        // Walk the same order two-at-a-time via the keyset cursor. Each page's
        // `cursorForOlder` + direction=before continues past its last entry —
        // exactly what the windowed SPA does on scroll.
        var walked = new List<string>();
        var page = await FetchAsync(client, ledger, acct, "limit=2&sort=amount&dir=desc");
        walked.AddRange(Payees(page));
        var guard = 0;
        while (page.CursorForOlder is { } cursor && guard++ < 20)
        {
            page = await FetchAsync(
                client, ledger, acct,
                $"limit=2&sort=amount&dir=desc&cursor={Uri.EscapeDataString(cursor)}&direction=before");
            if (page.Entries.Count == 0) break;
            walked.AddRange(Payees(page));
        }

        // No gaps, no duplicates, same order as the single-page reference.
        Assert.Equal(full, walked);
        Assert.Equal(Rows.Length, walked.Count);
        Assert.Equal(walked.Distinct().Count(), walked.Count);
    }

    /// <summary>
    /// A split entered from one account with two lines of opposite sign onto another is
    /// two rows on that other account, each drawn with its own line's amount, while both
    /// carry the transaction's net there. The amount sort orders them by the amount each
    /// row shows (ADR-0108 D2), so a plain row whose amount lies between the two lines'
    /// sits between them, and the keyset cursor walks that order a row at a time, across
    /// the boundary between the two lines of one transaction, in both directions.
    /// </summary>
    [Fact]
    public async Task Sort_by_amount_orders_each_line_on_another_account_by_the_amount_it_shows()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var accountA = await ledger.AddBankAccountAsync("Account A");
        var accountB = await ledger.AddBankAccountAsync("Account B");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // From Account A: a line into Account B and a smaller one back out of it, and a
        // plain transfer into B whose amount lies between the lines' and below their net.
        const decimal LineIn = -6m, LineBack = 1m, Plain = -3m;
        await CreateAsync(client, ledger.LedgerId, accountA.Id, Day(2026, 1, 10), "Payee A",
            (accountB.Id, LineIn), (accountB.Id, LineBack));
        await CreateAsync(client, ledger.LedgerId, accountA.Id, Day(2026, 1, 11), "Payee B",
            (accountB.Id, Plain));
        var larger = -LineIn;
        var smaller = -LineBack;
        var between = -Plain;

        // Anchor: B shows three single rows, the split's two carrying a net that sorts
        // above the plain row's amount, though one of them shows less.
        var byDate = await FetchAsync(client, ledger, accountB.Id, "limit=100");
        Assert.Equal(3, byDate.Entries.Count);
        Assert.All(byDate.Entries, e => Assert.Equal(RegisterEntryDto.KindTxn, e.Kind));
        var splitRows = byDate.Entries.Where(e => e.Txn!.Payee == "Payee A").ToList();
        Assert.Equal(2, splitRows.Count);
        Assert.All(splitRows, e => Assert.True(e.Txn!.HeaderAccountNetAmount > between,
            $"net on the account: {e.Txn!.HeaderAccountNetAmount}"));

        var ascending = await FetchAsync(client, ledger, accountB.Id, "limit=100&sort=amount&dir=asc");
        Assert.Equal(new[] { smaller, between, larger }, ascending.Entries.Select(e => e.Txn!.Amount));
        var descending = await FetchAsync(client, ledger, accountB.Id, "limit=100&sort=amount&dir=desc");
        Assert.Equal(new[] { larger, between, smaller }, descending.Entries.Select(e => e.Txn!.Amount));

        // A row at a time, down the list and back up it: no repeats, no gaps.
        foreach (var dir in new[] { "asc", "desc" })
        {
            var whole = (await FetchAsync(client, ledger, accountB.Id, $"limit=100&sort=amount&dir={dir}"))
                .Entries.Select(e => e.Txn!.Id).ToList();

            var down = new List<Guid>();
            var last = await FetchAsync(client, ledger, accountB.Id, $"limit=1&sort=amount&dir={dir}");
            down.AddRange(last.Entries.Select(e => e.Txn!.Id));
            for (var guard = 0; last.CursorForOlder is { } older && guard < 10; guard++)
            {
                var page = await FetchAsync(client, ledger, accountB.Id,
                    $"limit=1&sort=amount&dir={dir}&cursor={Uri.EscapeDataString(older)}&direction=before");
                if (page.Entries.Count == 0) break;
                down.AddRange(page.Entries.Select(e => e.Txn!.Id));
                last = page;
            }
            Assert.Equal(whole, down);

            var up = new List<Guid>();
            string? newer = last.CursorForNewer;
            for (var guard = 0; newer is not null && guard < 10; guard++)
            {
                var back = await FetchAsync(client, ledger, accountB.Id,
                    $"limit=1&sort=amount&dir={dir}&cursor={Uri.EscapeDataString(newer)}&direction=after");
                if (back.Entries.Count == 0) break;
                up.InsertRange(0, back.Entries.Select(e => e.Txn!.Id));
                newer = back.CursorForNewer;
            }
            Assert.Equal(whole.SkipLast(1), up);
        }
    }

    /// <summary>
    /// The investment register's Action sort orders by the word each row's chip shows
    /// (ADR-0028), not by the stored catalog value: Reinvest is <c>dividend_reinvest</c>,
    /// which sorts before <c>divx</c> (DividendXfr) and <c>misc</c> (Misc) as stored and
    /// after both as a word. The register loads in pages, so the order has to hold across
    /// a page boundary, walked both ways.
    /// </summary>
    [Fact]
    public async Task Sort_by_action_orders_by_the_word_shown_across_pages()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var securityId = await ledger.AddSecurityAsync("Security A", ticker: "SECA");
        var income = await ledger.AddCategoryAsync("Category A", kind: "income");
        var bank = await ledger.AddBankAccountAsync("Account A");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Dated so that neither the date order nor the stored order is the word order.
        async Task PostAsync(CreateInvestmentTransactionRequest request)
        {
            var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions", request);
            Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created,
                $"{request.Action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
        await PostAsync(new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, PostedAt = Day(2026, 3, 1), Action = "misc",
            Amount = 5m, CategoryAccountId = income.Id,
        });
        await PostAsync(new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, PostedAt = Day(2026, 3, 2), Action = "dividend_reinvest",
            SecurityId = securityId, Shares = 1m, Price = 10m, CategoryAccountId = income.Id,
        });
        await PostAsync(new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, PostedAt = Day(2026, 3, 3), Action = "buy",
            SecurityId = securityId, Shares = 2m, Price = 10m,
        });
        await PostAsync(new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, PostedAt = Day(2026, 3, 4), Action = "divx",
            SecurityId = securityId, Amount = 3m, CategoryAccountId = income.Id, TransferAccountId = bank.Id,
        });
        await PostAsync(new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, PostedAt = Day(2026, 3, 5), Action = "dividend_cash",
            SecurityId = securityId, Amount = 4m, CategoryAccountId = income.Id,
        });

        var wordOrder = new[] { "buy", "dividend_cash", "divx", "misc", "dividend_reinvest" };
        static List<string> Actions(RegisterPage page) => page.Entries.Select(ActionOf).ToList();

        // Anchor: the stored order is not the word order, so a sort on the stored value fails.
        Assert.NotEqual(wordOrder, wordOrder.Order(StringComparer.Ordinal));

        foreach (var (dir, expected) in new[] { ("asc", wordOrder), ("desc", Enumerable.Reverse(wordOrder).ToArray()) })
        {
            var whole = await FetchAsync(client, ledger, brokerage.Id, $"limit=100&sort=action&dir={dir}");
            Assert.Equal(expected, Actions(whole));

            // Two entries a page, down the list and back up it.
            var down = new List<string>();
            var last = await FetchAsync(client, ledger, brokerage.Id, $"limit=2&sort=action&dir={dir}");
            down.AddRange(Actions(last));
            for (var guard = 0; last.CursorForOlder is { } older && guard < 10; guard++)
            {
                var page = await FetchAsync(client, ledger, brokerage.Id,
                    $"limit=2&sort=action&dir={dir}&cursor={Uri.EscapeDataString(older)}&direction=before");
                if (page.Entries.Count == 0) break;
                down.AddRange(Actions(page));
                last = page;
            }
            Assert.Equal(expected, down);

            var up = new List<string>();
            var newer = last.CursorForNewer;
            for (var guard = 0; newer is not null && guard < 10; guard++)
            {
                var back = await FetchAsync(client, ledger, brokerage.Id,
                    $"limit=2&sort=action&dir={dir}&cursor={Uri.EscapeDataString(newer)}&direction=after");
                if (back.Entries.Count == 0) break;
                up.InsertRange(0, Actions(back));
                newer = back.CursorForNewer;
            }
            Assert.Equal(expected.SkipLast(last.Entries.Count), up);
        }
    }

    /// <summary>The stored action an entry's rows carry: one row, or a group's.</summary>
    private static string ActionOf(RegisterEntryDto entry) =>
        (entry.Txn is { } txn ? new[] { txn } : entry.Legs!)
            .Select(r => r.DerivedAction)
            .First(a => a is not null)!;

    /// <summary>A transaction entered through the register's create route, one posting per line.</summary>
    private static async Task CreateAsync(
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
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created,
            $"create: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }
}
