using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Moving a transaction to an account in another currency relabels (ADR-0114 D13, ADR-0072
/// D3): every stored figure is kept and reads in the target's currency. The move answers
/// how many mirrored pairs now carry two amounts. The move is refused, with nothing
/// written, when a two-amount pair would land in one currency with amounts that differ
/// (<c>relabel-pair-not-representable</c>), or when a moved figure carries more places than
/// the target's currency allows (<c>relabel-places-exceed-currency</c>, ADR-0114 D15),
/// counted as transactions; a selection of several is refused whole.
/// </summary>
/// <remarks>
/// Every transaction is written through <c>POST /transactions</c> or
/// <c>POST /investment-transactions</c> and moved through the bulk move, a move of one row
/// being an explicit selection of one, on synthetic ledgers, except a posting between two
/// categories, which only the Moneydance import writes and is seeded as it stores one.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MoveAccountRelabelTests : DatabaseTest
{
    public MoveAccountRelabelTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string PairCode = "relabel-pair-not-representable";
    private const string PlacesCode = "relabel-places-exceed-currency";
    private static readonly DateTime PostedAt = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(
        SyntheticLedger Ledger, Guid UsdBank, Guid UsdSavings, Guid EurBank, Guid EurSavings, Guid Category);

    private async Task<Seed> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usdBank = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var usdSavings = await ledger.AddBankAccountAsync("Bank B", currencyCode: "USD");
        var eurBank = await ledger.AddBankAccountAsync("Bank C", currencyCode: "EUR");
        var eurSavings = await ledger.AddBankAccountAsync("Bank D", currencyCode: "EUR");
        var category = await ledger.AddCategoryAsync("Category A");
        return new Seed(ledger, usdBank.Id, usdSavings.Id, eurBank.Id, eurSavings.Id, category.Id);
    }

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task<Guid> CreateAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, params TransactionPosting[] postings)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions", new CreateTransactionRequest
        {
            PostedAt = PostedAt,
            Payee = "Payee A",
            SourceAccountId = source,
            Postings = postings,
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("headerId").GetGuid();
    }

    /// <summary>One transaction moved: the bulk move of an explicit selection of one.</summary>
    private static Task<HttpResponseMessage> MoveAsync(
        HttpClient client, SyntheticLedger ledger, Guid headerId, Guid source, Guid target) =>
        BulkMoveAsync(client, ledger, source, target, headerId);

    private static Task<HttpResponseMessage> BulkMoveAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, Guid target, params Guid[] headerIds) =>
        client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-move-account",
            new { selection = new { kind = "explicit", accountId = source, headerIds }, targetAccountId = target });

    /// <summary>A move of one transaction's count of pairs that now carry two amounts. The
    /// transaction did move: a selection that moved nothing answers 200 too.</summary>
    private static async Task<int> MovedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        var moved = (await response.Content.ReadFromJsonAsync<BulkMoveAccountResponse>())!;
        Assert.True(moved.Moved > 0, $"nothing moved: {body}");
        return moved.BecameTwoAmount;
    }

    /// <summary>A refusal's code and count, and the root, for its other members.</summary>
    private static async Task<(string? Code, int Count, JsonElement Root)> RefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        var root = JsonDocument.Parse(body).RootElement;
        return (root.GetProperty("code").GetString(), root.GetProperty("count").GetInt32(), root);
    }

    private async Task<IReadOnlyList<TxnLegRow>> LegsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync();
    }

    private static decimal On(IReadOnlyList<TxnLegRow> legs, Guid accountId) =>
        Assert.Single(legs, l => l.AccountId == accountId).Amount;

    /// <summary>The account's current balance, as the account list shows it.</summary>
    private async Task<decimal?> BalanceAsync(Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.AccountCurrentBalances.AsNoTracking()
            .Where(b => b.AccountId == accountId)
            .Select(b => (decimal?)b.Balance)
            .FirstOrDefaultAsync();
    }

    // ---- pairs (ADR-0114 D13) --------------------------------------------------------------

    [Fact]
    public async Task A_two_amount_pair_whose_sides_differ_cannot_land_in_one_currency_and_nothing_moves()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -100m, CounterpartyAmount = 91.37m });
        var before = (await BalanceAsync(seed.UsdBank), await BalanceAsync(seed.EurSavings));

        // Its USD side onto another EUR account would leave one currency with two figures.
        Assert.Equal((PairCode, 1), Strip(await RefusalAsync(await MoveAsync(client, seed.Ledger, headerId, seed.UsdBank, seed.EurSavings))));

        var legs = await LegsAsync(headerId);
        Assert.Equal((-100m, 91.37m), (On(legs, seed.UsdBank), On(legs, seed.EurBank)));
        Assert.DoesNotContain(legs, l => l.AccountId == seed.EurSavings);
        Assert.Equal(before, (await BalanceAsync(seed.UsdBank), await BalanceAsync(seed.EurSavings)));
    }

    [Fact]
    public async Task A_two_amount_pair_of_equal_magnitudes_lands_in_one_currency_as_a_mirror()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -50m, CounterpartyAmount = 50m });

        Assert.Equal(0, await MovedAsync(await MoveAsync(client, seed.Ledger, headerId, seed.UsdBank, seed.EurSavings)));

        var legs = await LegsAsync(headerId);
        Assert.Equal((-50m, 50m), (On(legs, seed.EurSavings), On(legs, seed.EurBank)));
    }

    [Fact]
    public async Task A_mirrored_transfer_moved_into_another_currency_keeps_its_figures_and_is_counted()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.UsdSavings, Amount = -25m });

        Assert.Equal(1, await MovedAsync(await MoveAsync(client, seed.Ledger, headerId, seed.UsdBank, seed.EurBank)));

        var legs = await LegsAsync(headerId);
        Assert.Equal((-25m, 25m), (On(legs, seed.EurBank), On(legs, seed.UsdSavings)));
        Assert.Equal((-25m, 0m), (await BalanceAsync(seed.EurBank), await BalanceAsync(seed.UsdBank)));
    }

    // The anchor of the count: a move between two accounts in one currency relabels nothing.
    [Fact]
    public async Task A_move_between_two_accounts_in_one_currency_keeps_a_two_amount_pair_and_counts_nothing()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreateAsync(client, seed.Ledger, seed.EurBank,
            new TransactionPosting { CounterpartyAccountId = seed.UsdSavings, Amount = -25m, CounterpartyAmount = 27m });

        Assert.Equal(0, await MovedAsync(await MoveAsync(client, seed.Ledger, headerId, seed.EurBank, seed.EurSavings)));
        var legs = await LegsAsync(headerId);
        Assert.Equal((-25m, 27m), (On(legs, seed.EurSavings), On(legs, seed.UsdSavings)));
    }

    // ADR-0114 D13: zero and zero is representable across currencies.
    [Fact]
    public async Task A_mirrored_zero_pair_moved_into_another_currency_is_allowed_and_counted()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.UsdSavings, Amount = 0m });

        Assert.Equal(1, await MovedAsync(await MoveAsync(client, seed.Ledger, headerId, seed.UsdBank, seed.EurBank)));
        var legs = await LegsAsync(headerId);
        Assert.Equal((0m, 0m), (On(legs, seed.EurBank), On(legs, seed.UsdSavings)));
    }

    [Fact]
    public async Task A_category_expense_moved_into_another_currency_keeps_its_figure_and_is_not_counted()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -12.34m });

        Assert.Equal(0, await MovedAsync(await MoveAsync(client, seed.Ledger, headerId, seed.UsdBank, seed.EurBank)));
        var legs = await LegsAsync(headerId);
        Assert.Equal((-12.34m, 12.34m), (On(legs, seed.EurBank), On(legs, seed.Category)));
    }

    [Fact]
    public async Task One_row_that_cannot_be_relabelled_refuses_the_whole_bulk_move()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var mirrored = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.UsdSavings, Amount = -25m });
        var twoAmounts = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.EurBank, Amount = -100m, CounterpartyAmount = 91.37m });

        var (code, count, _) = await RefusalAsync(
            await BulkMoveAsync(client, seed.Ledger, seed.UsdBank, seed.EurSavings, mirrored, twoAmounts));
        Assert.Equal((PairCode, 1), (code, count));
        Assert.Contains(await LegsAsync(mirrored), l => l.AccountId == seed.UsdBank);
        Assert.Contains(await LegsAsync(twoAmounts), l => l.AccountId == seed.UsdBank);

        // Without the row that cannot land, the rest moves and is counted.
        var moved = await BulkMoveAsync(client, seed.Ledger, seed.UsdBank, seed.EurSavings, mirrored);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var body = (await moved.Content.ReadFromJsonAsync<BulkMoveAccountResponse>())!;
        Assert.Equal((1, 1), (body.Moved, body.BecameTwoAmount));
        Assert.Equal((-25m, 25m), (On(await LegsAsync(mirrored), seed.EurSavings), On(await LegsAsync(mirrored), seed.UsdSavings)));
    }

    // The cash-only brokerage transfer a move converts to a plain bank transfer (ADR-0072
    // D3's exception) relabels the same way, moved alone and in a selection.
    [Fact]
    public async Task A_cash_only_brokerage_transfer_moved_onto_a_bank_in_another_currency_is_counted()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var checking = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var eurBank = await ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR");
        var eurSavings = await ledger.AddBankAccountAsync("Bank C", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var single = await PostCashTransferAsync(client, ledger, brokerage.Id, checking.Id);
        var bulk = await PostCashTransferAsync(client, ledger, brokerage.Id, checking.Id);

        Assert.Equal(1, await MovedAsync(await MoveAsync(client, ledger, single, brokerage.Id, eurBank.Id)));
        var moved = await BulkMoveAsync(client, ledger, brokerage.Id, eurSavings.Id, bulk);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(1, (await moved.Content.ReadFromJsonAsync<BulkMoveAccountResponse>())!.BecameTwoAmount);

        await using var db = Db.NewDbContext();
        Assert.Null((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == single)).Action);
        var legs = await LegsAsync(single);
        Assert.Equal(0m, On(legs, eurBank.Id) + On(legs, checking.Id));
    }

    private static async Task<Guid> PostCashTransferAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid otherAccountId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerageId,
                PostedAt = PostedAt,
                Action = "transfer",
                Amount = 40m,
                TransferAccountId = otherAccountId,
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("headerId").GetGuid();
    }

    // ---- places (ADR-0114 D15) -------------------------------------------------------------

    [Fact]
    public async Task A_figure_with_cents_cannot_move_into_a_whole_unit_currency_and_a_whole_one_can()
    {
        var seed = await SeedAsync();
        var jpy = await seed.Ledger.AddBankAccountAsync("Bank E", currencyCode: "JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var fractional = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -12.34m });
        var whole = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -12m });

        var (code, count, root) = await RefusalAsync(await MoveAsync(client, seed.Ledger, fractional, seed.UsdBank, jpy.Id));
        Assert.Equal((PlacesCode, 1, "JPY", 0), (code, count, root.GetProperty("currencyCode").GetString(), root.GetProperty("places").GetInt32()));
        // A move names no account-level figure.
        Assert.Equal(0, root.GetProperty("accountFigures").GetArrayLength());
        Assert.Equal(-12.34m, On(await LegsAsync(fractional), seed.UsdBank));

        Assert.Equal(0, await MovedAsync(await MoveAsync(client, seed.Ledger, whole, seed.UsdBank, jpy.Id)));
        Assert.Equal(-12m, On(await LegsAsync(whole), jpy.Id));

        // The negative anchor: the same figure into another two-place currency moves.
        Assert.Equal(0, await MovedAsync(await MoveAsync(client, seed.Ledger, fractional, seed.UsdBank, seed.EurBank)));
        Assert.Equal(-12.34m, On(await LegsAsync(fractional), seed.EurBank));
    }

    [Fact]
    public async Task A_three_place_figure_cannot_move_into_a_two_place_currency_and_a_two_place_one_can()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var kwd = await ledger.AddBankAccountAsync("Bank A", currencyCode: "KWD");
        var usd = await ledger.AddBankAccountAsync("Bank B", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var thousandths = await CreateAsync(client, ledger, kwd.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1.234m });
        var hundredths = await CreateAsync(client, ledger, kwd.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1.23m });

        Assert.Equal((PlacesCode, 1), Strip(await RefusalAsync(await MoveAsync(client, ledger, thousandths, kwd.Id, usd.Id))));
        Assert.Equal(0, await MovedAsync(await MoveAsync(client, ledger, hundredths, kwd.Id, usd.Id)));
        Assert.Equal(-1.23m, On(await LegsAsync(hundredths), usd.Id));
    }

    [Fact]
    public async Task The_bulk_move_counts_each_transaction_that_does_not_fit_and_moves_none()
    {
        var seed = await SeedAsync();
        var jpy = await seed.Ledger.AddBankAccountAsync("Bank E", currencyCode: "JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        // A split with two lines that do not fit is one transaction.
        var split = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -1.5m },
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -2.5m });
        var single = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -0.25m });
        var whole = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -3m });

        Assert.Equal((PlacesCode, 2),
            Strip(await RefusalAsync(await BulkMoveAsync(client, seed.Ledger, seed.UsdBank, jpy.Id, split, single, whole))));
        foreach (var headerId in new[] { split, single, whole })
            Assert.DoesNotContain(await LegsAsync(headerId), l => l.AccountId == jpy.Id);
    }

    // A posting between two categories has no real side, so its lines read in each
    // category's own code (ADR-0114 D15), and a category keeps its code when the reporting
    // currency changes: a move between two categories in different codes relabels its line.
    [Fact]
    public async Task A_posting_between_categories_with_cents_cannot_move_onto_a_whole_unit_category()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddCategoryAsync("Category A");
        var other = await ledger.AddCategoryAsync("Category B");
        var sameCode = await ledger.AddCategoryAsync("Category C");
        var jpy = await ledger.AddCategoryAsync("Category D", currencyCode: "JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var single = await HeaderOfAsync((await ledger.AddTransactionPairAsync(source.Id, other.Id, 12.34m, PostedAt)).FromTxnId);
        var bulk = await HeaderOfAsync((await ledger.AddTransactionPairAsync(source.Id, other.Id, 0.5m, PostedAt)).FromTxnId);

        var (code, count, root) = await RefusalAsync(await MoveAsync(client, ledger, single, source.Id, jpy.Id));
        Assert.Equal((PlacesCode, 1, "JPY"), (code, count, root.GetProperty("currencyCode").GetString()));
        Assert.Equal((PlacesCode, 1), Strip(await RefusalAsync(await BulkMoveAsync(client, ledger, source.Id, jpy.Id, bulk))));
        Assert.DoesNotContain(await LegsAsync(single), l => l.AccountId == jpy.Id);
        Assert.DoesNotContain(await LegsAsync(bulk), l => l.AccountId == jpy.Id);

        // The anchor: a move between two categories in one code still moves.
        Assert.Equal(0, await MovedAsync(await MoveAsync(client, ledger, single, source.Id, sameCode.Id)));
        Assert.Equal(12.34m, On(await LegsAsync(single), sameCode.Id));
    }

    private async Task<Guid> HeaderOfAsync(Guid legId)
    {
        await using var db = Db.NewDbContext();
        return (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == legId)).HeaderId;
    }

    // A category line beside a real line takes the real side's currency (ADR-0114 D13), so
    // it moves between categories in different codes, cents and all.
    [Fact]
    public async Task A_category_line_beside_a_real_line_moves_onto_a_category_in_another_code()
    {
        var seed = await SeedAsync();
        var jpy = await seed.Ledger.AddCategoryAsync("Category D", currencyCode: "JPY");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, seed.Ledger);
        var headerId = await CreateAsync(client, seed.Ledger, seed.UsdBank,
            new TransactionPosting { CounterpartyAccountId = seed.Category, Amount = -12.34m });

        Assert.Equal(0, await MovedAsync(await MoveAsync(client, seed.Ledger, headerId, seed.Category, jpy.Id)));
        Assert.Equal(12.34m, On(await LegsAsync(headerId), jpy.Id));
    }

    private static (string? Code, int Count) Strip((string? Code, int Count, JsonElement Root) refusal) =>
        (refusal.Code, refusal.Count);
}
