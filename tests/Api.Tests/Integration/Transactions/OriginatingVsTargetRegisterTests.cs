using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// ADR-0036: register entries on the ORIGINATING side of a multi-
/// posting header collapse into one group; entries on a TARGET side
/// (an account touched by some but not all of the header's postings)
/// expand into one entry per posting.
///
/// Covers both the bank-style read and the SPA-facing derived_action
/// projection that fills the Action chip on per-posting target rows.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OriginatingVsTargetRegisterTests
{
    private readonly PostgresFixture _fixture;

    public OriginatingVsTargetRegisterTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static string RegisterUrl(Guid ledgerId, Guid accountId) =>
        $"/api/ledgers/{ledgerId}/transactions?account_id={accountId}&limit=100";

    [Fact]
    public async Task Originating_account_renders_one_group_entry_for_a_multi_posting_header()
    {
        // A paycheck-style split composed on the source account:
        // 3 postings, all of them touch the primary side. The
        // primary account's register sees ONE group entry — the
        // bank register's split-parent affordance.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var brokerageCash = await ledger.AddBankAccountAsync("brokerage-cash");
        var taxes = await ledger.AddCategoryAsync("Taxes");

        await ledger.AddMultiSplitAsync(
            primaryAccountId: checking.Id,
            legs: new[]
            {
                (brokerageCash.Id, -1350m),
                (brokerageCash.Id, -446.33m),
                (taxes.Id,         -200m),
            },
            postedAt: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            payee: "PAYCHECK");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            RegisterUrl(ledger.LedgerId, checking.Id)))!;
        var entry = Assert.Single(page.Entries);
        Assert.Equal(RegisterEntryDto.KindGroup, entry.Kind);
        Assert.NotNull(entry.Legs);
        Assert.Equal(3, entry.Legs!.Count);
    }

    [Fact]
    public async Task Target_account_with_multiple_postings_renders_one_entry_per_posting()
    {
        // Same paycheck split as the originating test, but viewed
        // from the target side: the brokerage-cash account is
        // touched by 2 of the 3 postings (1350 + 446.33). Its
        // register sees TWO independent entries, each kind='txn',
        // not one collapsed group.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var brokerageCash = await ledger.AddBankAccountAsync("brokerage-cash");
        var taxes = await ledger.AddCategoryAsync("Taxes");

        await ledger.AddMultiSplitAsync(
            primaryAccountId: checking.Id,
            legs: new[]
            {
                (brokerageCash.Id, -1350m),
                (brokerageCash.Id, -446.33m),
                (taxes.Id,         -200m),
            },
            postedAt: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            payee: "PAYCHECK");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            RegisterUrl(ledger.LedgerId, brokerageCash.Id)))!;
        Assert.Equal(2, page.Entries.Count);
        Assert.All(page.Entries, e =>
        {
            Assert.Equal(RegisterEntryDto.KindTxn, e.Kind);
            Assert.NotNull(e.Txn);
            // TxnGroupId stays non-null on each entry so the SPA's
            // split-counter affordance (↗ Split chip + read-only)
            // fires automatically. Editing routes back to the
            // originating account.
            Assert.NotNull(e.Txn!.TxnGroupId);
        });

        // The two posting amounts on brokerage-cash are +1350 (the
        // counter of the -1350 origin leg) and +446.33.
        var amounts = page.Entries
            .Select(e => e.Txn!.Amount)
            .OrderByDescending(a => a)
            .ToList();
        Assert.Equal(1350m, amounts[0]);
        Assert.Equal(446.33m, amounts[1]);
    }

    [Fact]
    public async Task Target_account_with_one_posting_renders_one_txn_entry()
    {
        // A target account touched by exactly one of the header's
        // postings (Taxes leg in the paycheck). Behaviourally
        // indistinguishable from any other single-leg target row.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var brokerageCash = await ledger.AddBankAccountAsync("brokerage-cash");
        var taxes = await ledger.AddCategoryAsync("Taxes");

        await ledger.AddMultiSplitAsync(
            primaryAccountId: checking.Id,
            legs: new[]
            {
                (brokerageCash.Id, -1350m),
                (brokerageCash.Id, -446.33m),
                (taxes.Id,         -200m),
            },
            postedAt: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            payee: "PAYCHECK");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            RegisterUrl(ledger.LedgerId, taxes.Id)))!;
        var entry = Assert.Single(page.Entries);
        Assert.Equal(RegisterEntryDto.KindTxn, entry.Kind);
        Assert.NotNull(entry.Txn);
        Assert.Equal(200m, entry.Txn!.Amount);
    }

    [Fact]
    public async Task Target_row_derives_Xfr_action_when_counter_is_asset_shaped()
    {
        // A cash-shape header (no header.action) whose target legs
        // sit opposite an asset-shaped counter (bank, investment,
        // asset, liability, credit_card, cash) gets derived_action
        // = 'Xfr'. Drives the investment register's Action chip on
        // per-posting target rows.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var brokerageCash = await ledger.AddBankAccountAsync("brokerage-cash");
        var taxes = await ledger.AddCategoryAsync("Taxes");

        await ledger.AddMultiSplitAsync(
            primaryAccountId: checking.Id,
            legs: new[]
            {
                (brokerageCash.Id, -1350m),
                (brokerageCash.Id, -446.33m),
                (taxes.Id,         -200m),
            },
            postedAt: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            payee: "PAYCHECK");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var brokerageRegister = (await client.GetFromJsonAsync<RegisterPage>(
            RegisterUrl(ledger.LedgerId, brokerageCash.Id)))!;
        Assert.All(brokerageRegister.Entries, e =>
        {
            Assert.Equal("Xfr", e.Txn!.DerivedAction);
            // Cash-shape header on a bank-domain account → BankRowDto,
            // which structurally carries no investment action: the
            // discriminated union (ADR-0030 §2) makes "not an
            // investment event" a type-level guarantee, stronger than
            // the old null-check on a shared field.
            Assert.IsType<BankRowDto>(e.Txn);
        });

        // The category-typed target (Taxes) has NULL derived_action
        // — categories are NOT transfers.
        var taxesRegister = (await client.GetFromJsonAsync<RegisterPage>(
            RegisterUrl(ledger.LedgerId, taxes.Id)))!;
        var entry = Assert.Single(taxesRegister.Entries);
        Assert.Null(entry.Txn!.DerivedAction);
    }

    [Fact]
    public async Task Header_action_passes_through_derived_action_unchanged()
    {
        // True investment events (Buy / Sell / Div / …) have
        // header.action set; derived_action passes the header value
        // through unchanged regardless of counter account type.
        // Set the action via raw SQL since the manual seeder
        // defaults to NULL.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");

        var (legId, _) = await ledger.AddTransactionPairAsync(
            fromAccountId: bank.Id,
            toAccountId: groceries.Id,
            amount: -25m,
            postedAt: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        var headerId = await ledger.ResolveHeaderIdAsync(legId);

        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE txn_headers SET action = 'misc' WHERE id = {headerId};");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            RegisterUrl(ledger.LedgerId, bank.Id)))!;
        var entry = Assert.Single(page.Entries);
        // The row is bank-domain (BankRowDto) — the raw header action
        // surfaces only through the universal derived_action, which
        // passes h.action through unchanged (ADR-0030 §2: InvestmentAction
        // is an investment-row-only field, absent on bank rows).
        Assert.IsType<BankRowDto>(entry.Txn);
        Assert.Equal("misc", entry.Txn!.DerivedAction);
    }
}
