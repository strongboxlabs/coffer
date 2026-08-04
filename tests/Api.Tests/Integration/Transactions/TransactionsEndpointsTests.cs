using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for <c>GET /api/ledgers/{ledgerId}/transactions</c> —
/// the keyset-paginated register query introduced in PR 3.7. Each test
/// mints a fresh synthetic ledger with bank/category accounts and seeds
/// transaction pairs directly so the resolved_transactions view has
/// realistic input.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TransactionsEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public TransactionsEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    /// <summary>
    /// Shared arrange step: ledger + bank account + expense category + N
    /// transaction pairs, each one day apart so posted_at is strictly
    /// increasing. Returns the bank account so account-filter tests can
    /// scope to it.
    /// </summary>
    private static async Task<(SyntheticLedger Ledger, AccountTuple Accounts, DateTime FirstPostedAt)>
        SeedRegisterAsync(PostgresFixture fixture, int pairCount)
    {
        var ledger = await SyntheticLedger.CreateAsync(fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        // Use UTC dates with seconds precision so the cursor's JSON
        // round-trip is exact (Postgres timestamptz preserves the
        // value).
        var firstPostedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < pairCount; i++)
        {
            await ledger.AddTransactionPairAsync(
                fromAccountId: bank.Id,
                toAccountId: groceries.Id,
                amount: -(10m + i),
                postedAt: firstPostedAt.AddDays(i),
                payee: $"merchant-{i:D3}");
        }
        return (ledger, new AccountTuple(bank.Id, groceries.Id), firstPostedAt);
    }

    private sealed record AccountTuple(Guid BankId, Guid CategoryId);

    /// <summary>Flatten a page of entries into transactions — collapses
    /// "group" entries to their legs in array order. Used by tests that
    /// don't care about the entry/group distinction.</summary>
    private static IReadOnlyList<RegisterRowDto> Flatten(RegisterPage page)
    {
        var rows = new List<RegisterRowDto>();
        foreach (var entry in page.Entries)
        {
            if (entry.Kind == RegisterEntryDto.KindTxn && entry.Txn is not null)
                rows.Add(entry.Txn);
            else if (entry.Legs is not null)
                rows.AddRange(entry.Legs);
        }
        return rows;
    }

    [Fact]
    public async Task Get_returns_page_sorted_by_posted_at_descending()
    {
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 3);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // No account filter: both legs of each pair appear, so 3 pairs
        // → 6 transactions, each its own entry (none of them are
        // grouped), sorted by posted_at DESC.
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?limit=100");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = (await response.Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Equal(6, page.Entries.Count);
        Assert.All(page.Entries, e => Assert.Equal(RegisterEntryDto.KindTxn, e.Kind));
        Assert.Null(page.CursorForOlder);

        var rows = Flatten(page);
        var postedAts = rows.Select(i => i.PostedAt).ToArray();
        Assert.Equal(postedAts.OrderByDescending(p => p).ToArray(), postedAts);

        Assert.All(rows, item =>
            Assert.True(item.AccountId == accounts.BankId || item.AccountId == accounts.CategoryId,
                $"row's account_id {item.AccountId} should be either bank or category"));
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    [Fact]
    public async Task Move_account_relocates_a_transaction_to_another_real_account()
    {
        // ADR-0072 D3: a mis-filed row moves from its source account to another
        // real account — it leaves the source register and appears in the target.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        var groceries = await ledger.AddCategoryAsync("groceries");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -25m,
            new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), "merchant");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var before = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bank.Id}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        var row = Assert.Single(Flatten(before));

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{row.HeaderId}/move-account",
            new { sourceAccountId = bank.Id, targetAccountId = savings.Id });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var bankAfter = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bank.Id}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Empty(Flatten(bankAfter));
        var savingsAfter = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={savings.Id}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.NotEmpty(Flatten(savingsAfter));
    }

    [Fact]
    public async Task Move_account_rejects_a_self_transfer_collision()
    {
        // Moving the source leg of a transfer onto the account that is already
        // the other side would collide with UNIQUE(header, posting, account).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        await ledger.AddTransactionPairAsync(bank.Id, savings.Id, -50m,
            new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), "transfer");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bank.Id}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        var row = Assert.Single(Flatten(page));

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{row.HeaderId}/move-account",
            new { sourceAccountId = bank.Id, targetAccountId = savings.Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("transaction-move-collision", await ReadCodeAsync(resp));
    }

    [Fact]
    public async Task Move_account_rejects_a_split_moved_to_an_investment_account()
    {
        // ADR-0072 D3 guard: a split (multi-posting) transaction cannot be
        // moved to an investment account.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var utilities = await ledger.AddCategoryAsync("utilities");
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var (_, headerId) = await ledger.AddMultiSplitAsync(
            bank.Id,
            new (Guid, decimal)[] { (groceries.Id, -10m), (utilities.Id, -20m) },
            new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), "split");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}/move-account",
            new { sourceAccountId = bank.Id, targetAccountId = brokerage.Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("transaction-move-split-to-investment", await ReadCodeAsync(resp));
    }

    [Fact]
    public async Task Move_account_allows_a_single_cash_row_to_an_investment_account()
    {
        // ADR-0072 D3: a NON-split cash row may legitimately move to an
        // investment account (a mis-filed brokerage deposit). Only a SPLIT
        // headed to an investment account is rejected.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -25m,
            new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), "deposit");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bank.Id}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        var row = Assert.Single(Flatten(page));

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{row.HeaderId}/move-account",
            new { sourceAccountId = bank.Id, targetAccountId = brokerage.Id });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var bankAfter = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bank.Id}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Empty(Flatten(bankAfter));
    }

    [Fact]
    public async Task Bulk_move_account_relocates_the_whole_selection()
    {
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 2);
        var savings = await ledger.AddBankAccountAsync("savings");
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var body = new
        {
            selection = new
            {
                kind = "all",
                accountId = accounts.BankId,
                statusFilter = "all",
                selectedAt = DateTime.UtcNow.AddMinutes(1),
                excludeIds = Array.Empty<Guid>(),
                headerIds = Array.Empty<Guid>(),
            },
            targetAccountId = savings.Id,
        };
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-move-account", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var bankAfter = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Empty(Flatten(bankAfter));
        var savingsAfter = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={savings.Id}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Equal(2, Flatten(savingsAfter).Count());
    }

    [Fact]
    public async Task Bulk_move_account_rejects_investment_shape_transactions()
    {
        // ADR-0072 D3 + layer independence: the endpoint refuses to move an
        // investment-shape header (action != null — holdings-tied) regardless
        // of caller, all-or-nothing.
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 2);
        var savings = await ledger.AddBankAccountAsync("savings");

        await using (var db = _fixture.NewDbContext())
        {
            var oneHeader = await db.TxnHeaders
                .Where(h => h.LedgerId == ledger.LedgerId)
                .Select(h => h.Id)
                .FirstAsync();
            await db.TxnHeaders.Where(h => h.Id == oneHeader)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Action, "buy"));
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var body = new
        {
            selection = new
            {
                kind = "all",
                accountId = accounts.BankId,
                statusFilter = "all",
                selectedAt = DateTime.UtcNow.AddMinutes(1),
                excludeIds = Array.Empty<Guid>(),
                headerIds = Array.Empty<Guid>(),
            },
            targetAccountId = savings.Id,
        };
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-move-account", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("transaction-header-is-investment", await ReadCodeAsync(resp));

        // All-or-nothing: nothing moved — the bank rows are untouched.
        var bankAfter = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.NotEmpty(Flatten(bankAfter));
        var savingsAfter = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={savings.Id}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Empty(Flatten(savingsAfter));
    }

    [Fact]
    public async Task Bulk_unhide_restores_soft_hidden_rows_to_the_register()
    {
        // ADR-0072 D2: an all-mode "hidden" selection un-hides the rows, which
        // then re-appear in the normal (visible) register.
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 2);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.LedgerId == ledger.LedgerId)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsHidden, true));
        }

        var body = new
        {
            selection = new
            {
                kind = "all",
                accountId = accounts.BankId,
                statusFilter = "hidden",
                selectedAt = DateTime.UtcNow.AddMinutes(1),
                excludeIds = Array.Empty<Guid>(),
                headerIds = Array.Empty<Guid>(),
            },
        };
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-unhide", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The hidden view is now empty for the account; the visible register shows them.
        var stillHidden = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&hidden=true&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Empty(Flatten(stillHidden));

        var visible = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.NotEmpty(Flatten(visible));
    }

    [Fact]
    public async Task Explicit_selection_of_a_hidden_row_is_counted_and_unhidden()
    {
        // Regression (ADR-0072 D1): an explicit selection — a checkbox on a
        // specific hidden row — carries no statusFilter, so it must NOT be
        // visibility-scoped. Otherwise the summary counts 0 (the bulk bar
        // flashes then vanishes) and an explicit unhide/move silently no-ops.
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 2);
        Guid hiddenId;
        await using (var db = _fixture.NewDbContext())
        {
            await db.TxnHeaders.Where(h => h.LedgerId == ledger.LedgerId)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsHidden, true));
            hiddenId = await db.TxnHeaders
                .Where(h => h.LedgerId == ledger.LedgerId)
                .Select(h => h.Id)
                .FirstAsync();
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The footer summary counts the explicitly-selected hidden row.
        var summaryResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/selection-summary",
            new { kind = "explicit", headerIds = new[] { hiddenId } });
        Assert.Equal(HttpStatusCode.OK, summaryResp.StatusCode);
        using (var doc = JsonDocument.Parse(
            await summaryResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        }

        // Explicit bulk-unhide restores exactly that row to the register.
        var unhideResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-unhide",
            new { selection = new { kind = "explicit", headerIds = new[] { hiddenId } } });
        Assert.Equal(HttpStatusCode.OK, unhideResp.StatusCode);

        var visible = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Contains(Flatten(visible), r => r.HeaderId == hiddenId);
    }

    [Fact]
    public async Task Hidden_filter_shows_only_soft_hidden_rows()
    {
        // ADR-0072 D1: ?hidden=true pages the soft-hidden "recovery" view; the
        // default (visible) register still excludes hidden rows.
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 3);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        Guid hiddenHeaderId;
        await using (var db = _fixture.NewDbContext())
        {
            hiddenHeaderId = await db.TxnHeaders
                .Where(h => h.LedgerId == ledger.LedgerId)
                .OrderBy(h => h.PostedAt)
                .Select(h => h.Id)
                .FirstAsync();
            await db.TxnHeaders.Where(h => h.Id == hiddenHeaderId)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsHidden, true));
        }

        var visible = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.DoesNotContain(Flatten(visible), r => r.HeaderId == hiddenHeaderId);

        var hidden = (await (await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&hidden=true&limit=100"))
            .Content.ReadFromJsonAsync<RegisterPage>())!;
        var hiddenRows = Flatten(hidden);
        Assert.NotEmpty(hiddenRows);
        Assert.All(hiddenRows, r => Assert.Equal(hiddenHeaderId, r.HeaderId));
    }

    [Fact]
    public async Task Get_paginates_through_cursor_until_next_cursor_is_null()
    {
        var (ledger, _, _) = await SeedRegisterAsync(_fixture, pairCount: 5); // 10 entries total (no groups)
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // limit=4 means: page 1 = 4 entries + cursor, page 2 = 4 + cursor,
        // page 3 = 2 entries + null cursor (10 total). Follow until null.
        var collected = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)   // 10 = safety stop
        {
            var url = $"/api/ledgers/{ledger.LedgerId}/transactions?limit=4"
                    + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = (await response.Content.ReadFromJsonAsync<RegisterPage>())!;
            collected.AddRange(Flatten(body).Select(i => i.Id));
            cursor = body.CursorForOlder;
            if (cursor is null) break;
        }

        Assert.Null(cursor);
        Assert.Equal(10, collected.Count);
        Assert.Equal(collected.Count, collected.Distinct().Count());   // no duplicates across pages
    }

    [Fact]
    public async Task Get_with_account_id_filter_returns_only_that_accounts_rows()
    {
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 3);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Bank-only view: should be 3 single-txn entries, all referencing
        // bank.id (none grouped — pair seeder doesn't set txn_group_id).
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = (await response.Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Equal(3, page.Entries.Count);
        Assert.All(page.Entries, e => Assert.Equal(RegisterEntryDto.KindTxn, e.Kind));
        Assert.All(Flatten(page),
            item => Assert.Equal(accounts.BankId, item.AccountId));
    }

    [Fact]
    public async Task Get_with_cross_ledger_account_id_returns_422_account_not_in_ledger()
    {
        // ledgerA + accountA, ledgerB exists but caller is authed for B
        // and supplies accountA in the query — the API must reject with
        // 422 account-not-in-ledger (distinct from ledger-not-visible).
        var ledgerA = await SyntheticLedger.CreateAsync(_fixture);
        var accountA = await ledgerA.AddBankAccountAsync("a-bank");

        var ledgerB = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledgerB);

        var response = await client.GetAsync(
            $"/api/ledgers/{ledgerB.LedgerId}/transactions?account_id={accountA.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_returns_422_ledger_not_visible_when_user_has_no_grant()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        await alice.AddBankAccountAsync("alices-bank");

        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var response = await bobClient.GetAsync($"/api/ledgers/{alice.LedgerId}/transactions");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task Get_returns_422_register_limit_invalid_when_limit_out_of_range(int limit)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?limit={limit}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("register-limit-invalid",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_excludes_user_hidden_rows()
    {
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 2); // 4 rows
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Snapshot the pre-hide page so we know which ids exist, then
        // hide one of them via an overrides row.
        var beforeResp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}");
        var beforePage = (await beforeResp.Content.ReadFromJsonAsync<RegisterPage>())!;
        var beforeRows = Flatten(beforePage);
        Assert.Equal(2, beforeRows.Count);
        var hiddenId = beforeRows[0].Id;
        await ledger.HideTransactionAsync(hiddenId);

        var afterResp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}");
        var afterPage = (await afterResp.Content.ReadFromJsonAsync<RegisterPage>())!;
        var afterRows = Flatten(afterPage);
        Assert.Single(afterRows);
        Assert.DoesNotContain(afterRows, i => i.Id == hiddenId);
    }

    [Fact]
    public async Task Get_excludes_rows_that_were_merged_away()
    {
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 2);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Pick two rows; mark one as merged-into the other so it should
        // disappear from the register.
        var pre = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}");
        var prePage = (await pre.Content.ReadFromJsonAsync<RegisterPage>())!;
        var preRows = Flatten(prePage);
        Assert.Equal(2, preRows.Count);
        await ledger.MarkTransactionMergedAsync(
            losingId: preRows[0].Id, winnerId: preRows[1].Id);

        var post = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}");
        var postPage = (await post.Content.ReadFromJsonAsync<RegisterPage>())!;
        var postRows = Flatten(postPage);
        Assert.Single(postRows);
        Assert.Equal(preRows[1].Id, postRows[0].Id);
    }

    [Fact]
    public async Task Get_surfaces_register_parity_fields_from_migration_018()
    {
        // Verifies the resolved_transactions view extensions land on the
        // public DTO: check_number, counterparty_account_*, txn_group_id /
        // leg_index, and tags. The counterparty values are the "other
        // side" of the symmetric posting per ADR-0019 — the MD register
        // surfaces this as the Category column.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");

        var (fromId, _) = await ledger.AddTransactionPairAsync(
            fromAccountId: bank.Id,
            toAccountId: groceries.Id,
            amount: -42.50m,
            postedAt: new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc),
            payee: "Whole Foods");
        await ledger.SetCheckNumberAsync(fromId, "1284");
        await ledger.AddTagAsync(fromId, "weekly");
        await ledger.AddTagAsync(fromId, "essentials");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bank.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = (await response.Content.ReadFromJsonAsync<RegisterPage>())!;
        var entry = Assert.Single(page.Entries);
        Assert.Equal(RegisterEntryDto.KindTxn, entry.Kind);
        var row = entry.Txn!;
        Assert.Equal(fromId, row.Id);

        // Counterparty: the bank-side leg's "category" is the other
        // account (groceries). The view's subqueries pull the name +
        // type via the FK chain.
        Assert.Equal(groceries.Id, row.CounterpartyAccountId);
        Assert.Equal("groceries", row.CounterpartyAccountName);
        Assert.Equal("category",  row.CounterpartyAccountType);

        // Check number propagates from t.check_number.
        Assert.Equal("1284", row.CheckNumber);

        // Tags are deterministically ordered (ORDER BY tg.name) and
        // never null.
        Assert.Equal(new[] { "essentials", "weekly" }, row.Tags);

        // Single-split pairs get NULL txn_group_id; leg_index defaults to 0.
        Assert.Null(row.TxnGroupId);
        Assert.Equal(0, row.LegIndex);
    }

    [Fact]
    public async Task Get_returns_full_parent_child_path_in_counterparty_name()
    {
        // resolved_transactions.counterparty_account_name now carries the
        // root-to-leaf path (account_path() in migration 021) instead of
        // the leaf-only account name. Categories like "Base" or "Health"
        // are ambiguous in isolation; the path makes the register chip
        // meaningful ("Wages & Salary/Base", "Taxes/Federal Income Tax").
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var taxes = await ledger.AddCategoryAsync("Taxes");
        var federal = await ledger.AddCategoryAsync("Federal Income Tax", parentId: taxes.Id);

        var (fromId, _) = await ledger.AddTransactionPairAsync(
            fromAccountId: bank.Id,
            toAccountId: federal.Id,
            amount: -200m,
            postedAt: new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc),
            payee: "IRS");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bank.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = (await response.Content.ReadFromJsonAsync<RegisterPage>())!;
        var row = Assert.Single(page.Entries).Txn!;
        Assert.Equal(fromId, row.Id);
        Assert.Equal("Taxes/Federal Income Tax", row.CounterpartyAccountName);
    }

    [Fact]
    public async Task Get_collapses_multi_split_legs_into_one_group_entry()
    {
        // Three-leg paycheck: $4470 gross, split across federal tax,
        // state tax, and net deposit. The bank-side view should see
        // ONE group entry with three legs (sorted by leg_index ASC).
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var federal = await ledger.AddCategoryAsync("federal-tax");
        var state = await ledger.AddCategoryAsync("state-tax");
        var net = await ledger.AddCategoryAsync("net-deposit");

        var (originIds, groupId) = await ledger.AddMultiSplitAsync(
            primaryAccountId: bank.Id,
            legs: new[]
            {
                (federal.Id, -1200m),
                (state.Id,   -300m),
                (net.Id,     -2970m),
            },
            postedAt: new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            payee: "Acme Payroll");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bank.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = (await response.Content.ReadFromJsonAsync<RegisterPage>())!;
        var entry = Assert.Single(page.Entries);
        Assert.Equal(RegisterEntryDto.KindGroup, entry.Kind);
        Assert.Null(entry.Txn);
        Assert.Equal(groupId, entry.GroupId);

        var legs = entry.Legs!;
        Assert.Equal(3, legs.Count);
        // Sorted by leg_index ASC.
        Assert.Equal(new[] { 0, 1, 2 }, legs.Select(l => l.LegIndex).ToArray());
        // Each leg references the originally-seeded origin-side id.
        Assert.Equal(originIds.ToArray(), legs.Select(l => l.Id).ToArray());
        // All legs share the same txn_group_id and account.
        Assert.All(legs, l => Assert.Equal(groupId, l.TxnGroupId));
        Assert.All(legs, l => Assert.Equal(bank.Id, l.AccountId));
        // Leg amounts preserved verbatim — the API surfaces raw legs,
        // SPA aggregates for display.
        Assert.Equal(new[] { -1200m, -300m, -2970m },
            legs.Select(l => l.Amount).ToArray());
    }

    [Fact]
    public async Task Get_paginates_by_entry_never_slicing_a_group()
    {
        // One 3-leg group + 5 single transactions, all on distinct dates
        // so the order is deterministic. limit=2 → page 1 = 2 entries
        // (whatever they are), and the group stays whole inside its page.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var federal = await ledger.AddCategoryAsync("federal-tax");
        var state = await ledger.AddCategoryAsync("state-tax");
        var net = await ledger.AddCategoryAsync("net-deposit");

        // Group: dated 2026-05-15 (most recent).
        await ledger.AddMultiSplitAsync(
            primaryAccountId: bank.Id,
            legs: new[]
            {
                (federal.Id, -100m),
                (state.Id,   -50m),
                (net.Id,     -350m),
            },
            postedAt: new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            payee: "Payroll");
        // 5 single pairs on earlier dates.
        for (var i = 0; i < 5; i++)
        {
            await ledger.AddTransactionPairAsync(
                fromAccountId: bank.Id,
                toAccountId: groceries.Id,
                amount: -(10m + i),
                postedAt: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddDays(i),
                payee: $"merchant-{i:D2}");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // 6 entries total (1 group + 5 singles). limit=2 → 3 pages.
        var seenGroupIds = new HashSet<Guid>();
        var entriesByKind = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var url = $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={bank.Id}&limit=2"
                    + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var resp = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = (await resp.Content.ReadFromJsonAsync<RegisterPage>())!;
            Assert.True(body.Entries.Count <= 2,
                $"page returned {body.Entries.Count} entries, expected ≤ 2");
            foreach (var e in body.Entries)
            {
                entriesByKind.Add(e.Kind);
                if (e.Kind == RegisterEntryDto.KindGroup)
                {
                    Assert.NotNull(e.GroupId);
                    seenGroupIds.Add(e.GroupId!.Value);
                    // Every leg of the group lives in this same page —
                    // never split across the cursor boundary.
                    Assert.Equal(3, e.Legs!.Count);
                }
            }
            cursor = body.CursorForOlder;
            if (cursor is null) break;
        }

        Assert.Null(cursor);
        Assert.Equal(6, entriesByKind.Count);  // 1 group + 5 txn
        Assert.Single(seenGroupIds);           // the group surfaced once, intact
        Assert.Equal(1, entriesByKind.Count(k => k == RegisterEntryDto.KindGroup));
        Assert.Equal(5, entriesByKind.Count(k => k == RegisterEntryDto.KindTxn));
    }

    // -- Migration 031: bidirectional cursors --------------------------------

    [Fact]
    public async Task Get_with_direction_after_returns_entries_newer_than_cursor()
    {
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 6);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Load the most-recent 3 entries (oldest-half remains unseen).
        var firstResp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&limit=3");
        var firstPage = (await firstResp.Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Equal(3, firstPage.Entries.Count);
        Assert.NotNull(firstPage.CursorForOlder);
        // First page is the timeline head — nothing newer.
        Assert.Null(firstPage.CursorForNewer);

        var firstIds = Flatten(firstPage).Select(r => r.Id).ToHashSet();

        // Page older once to get a cursor that lives in the middle of the
        // dataset. From there, direction='after' should walk back toward
        // the newer entries (the ones we already saw on page 1).
        var olderResp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&limit=3"
            + $"&cursor={Uri.EscapeDataString(firstPage.CursorForOlder!)}");
        var olderPage = (await olderResp.Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.NotNull(olderPage.CursorForNewer);

        // Now fetch direction='after' from the older page's top cursor —
        // should return exactly the entries that are newer than that
        // cursor (i.e. the first page's entries, minus the boundary).
        var afterResp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&limit=10"
            + $"&direction=after"
            + $"&cursor={Uri.EscapeDataString(olderPage.CursorForNewer!)}");
        Assert.Equal(HttpStatusCode.OK, afterResp.StatusCode);
        var afterPage = (await afterResp.Content.ReadFromJsonAsync<RegisterPage>())!;
        var afterIds = Flatten(afterPage).Select(r => r.Id).ToHashSet();

        // Sanity: the after-page is non-empty and overlaps the original
        // first-page (we walked back toward the head). Entries are
        // time-DESC regardless of direction (the SQL's outer SELECT re-
        // sorts).
        Assert.NotEmpty(afterIds);
        Assert.True(afterIds.IsSubsetOf(firstIds),
            "direction=after page should be a subset of the originally-seen newer entries");
        var postedAts = Flatten(afterPage).Select(r => r.PostedAt).ToArray();
        Assert.Equal(postedAts.OrderByDescending(p => p).ToArray(), postedAts);
    }

    [Fact]
    public async Task Get_with_starting_at_anchors_focused_header_at_index_zero()
    {
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 5);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Capture all entries first, then pick a middle one to anchor on.
        var allResp = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}&limit=100");
        var allPage = (await allResp.Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Equal(5, allPage.Entries.Count);

        // Resolve a middle entry's header id (entry[2] of 0..4).
        var middleEntry = allPage.Entries[2];
        Assert.Equal(RegisterEntryDto.KindTxn, middleEntry.Kind);
        var middleHeaderId = middleEntry.Txn!.HeaderId;

        // Request a page anchored at that header.
        var anchored = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}"
            + $"&starting_at={middleHeaderId}&limit=10");
        Assert.Equal(HttpStatusCode.OK, anchored.StatusCode);
        var anchoredPage = (await anchored.Content.ReadFromJsonAsync<RegisterPage>())!;

        // Focused entry is the first row of the anchored page (newest).
        Assert.NotEmpty(anchoredPage.Entries);
        var firstAnchored = anchoredPage.Entries[0];
        Assert.Equal(RegisterEntryDto.KindTxn, firstAnchored.Kind);
        Assert.Equal(middleHeaderId, firstAnchored.Txn!.HeaderId);

        // The rest of the page is strictly older than the focused entry.
        var focusedPostedAt = firstAnchored.Txn!.PostedAt;
        var olderRows = Flatten(anchoredPage).Skip(1).ToList();
        Assert.All(olderRows, r => Assert.True(r.PostedAt <= focusedPostedAt));

        // Both cursors are populated — cursorForNewer points at the
        // anchor itself (load entries newer than it); cursorForOlder
        // points at the oldest entry on the page (load entries older).
        Assert.NotNull(anchoredPage.CursorForNewer);
        Assert.NotNull(anchoredPage.CursorForOlder);
    }

    [Fact]
    public async Task Get_with_invalid_direction_returns_422()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?direction=sideways");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("register-direction-invalid", body);
    }

    [Fact]
    public async Task Get_with_starting_at_for_nonexistent_header_returns_empty_page()
    {
        var (ledger, accounts, _) = await SeedRegisterAsync(_fixture, pairCount: 2);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A header id that doesn't exist (random Guid) resolves to no
        // cursor — the endpoint returns an empty page rather than
        // surfacing the lookup miss as an error. The SPA decides how
        // to render that.
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accounts.BankId}"
            + $"&starting_at={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = (await response.Content.ReadFromJsonAsync<RegisterPage>())!;
        Assert.Empty(page.Entries);
        Assert.Null(page.CursorForOlder);
        Assert.Null(page.CursorForNewer);
    }
}
