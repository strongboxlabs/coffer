using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The first edit of a transaction keeps its amount from before the edit in the row's
/// originals (ADR-0114 D15): the amount on its originating account, the sum of its legs
/// there, as its register showed it, with the rounded mark clear.
/// </summary>
/// <remarks>
/// Every edit path captures through <c>AnnotationWriter.CaptureOriginalsAsync</c> before
/// it changes anything, so a capture taken after the change would record the new amount;
/// each test edits the amount itself, which is what tells the two apart. Capture is once
/// per header, so a second edit, or an edit of a row an import seeded, keeps the row on
/// file.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class HeaderOriginalsCaptureAmountTests : DatabaseTest
{
    private static readonly DateTime PostedAt = new(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);

    public HeaderOriginalsCaptureAmountTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_bank_first_edit_keeps_the_amount_from_before_the_edit()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var fuel = await ledger.AddCategoryAsync("fuel");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A split, so the amount is the sum of the originating account's legs.
        var headerId = await CreateBankAsync(client, ledger, bank.Id,
            (groceries.Id, -40m), (fuel.Id, -25m));
        Assert.Null(await OriginalsAsync(headerId));

        await PatchBankAsync(client, ledger, headerId, bank.Id, (groceries.Id, -50m));

        var originals = await OriginalsAsync(headerId);
        Assert.NotNull(originals);
        Assert.Equal(-65m, originals.Value.Amount);
        Assert.False(originals.Value.RoundedAtImport);
        Assert.Equal(-50m, await SumOnAsync(headerId, bank.Id));
    }

    [Fact]
    public async Task An_investment_first_edit_keeps_the_amount_from_before_the_edit()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var security = await ledger.AddSecurityAsync("Fund", ticker: "FND");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id, PostedAt = PostedAt, Action = "buy",
                SecurityId = security, Shares = 1m, Price = 10m, Amount = 10m,
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var headerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
        // The buy's cash leaves the brokerage, the transaction's originating account.
        Assert.Equal(-10m, await SumOnAsync(headerId, brokerage.Id));

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id, PostedAt = PostedAt, Action = "buy",
                SecurityId = security, Shares = 2m, Price = 10m, Amount = 20m,
            });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());
        Assert.Equal(-20m, await SumOnAsync(headerId, brokerage.Id));

        var originals = await OriginalsAsync(headerId);
        Assert.NotNull(originals);
        Assert.Equal(-10m, originals.Value.Amount);
        Assert.False(originals.Value.RoundedAtImport);
    }

    [Fact]
    public async Task A_second_edit_keeps_the_first_capture()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var headerId = await CreateBankAsync(client, ledger, bank.Id, (groceries.Id, -30m));
        await PatchBankAsync(client, ledger, headerId, bank.Id, (groceries.Id, -50m));
        await PatchBankAsync(client, ledger, headerId, bank.Id, (groceries.Id, -70m));

        var originals = await OriginalsAsync(headerId);
        Assert.NotNull(originals);
        Assert.Equal(-30m, originals.Value.Amount);
        Assert.Equal(-70m, await SumOnAsync(headerId, bank.Id));
    }

    [Fact]
    public async Task An_edit_leaves_the_originals_an_import_seeded()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // An import that rounded: the legs at the currency's places, the bank's figure
        // finer, and the mark set.
        var headerId = await CreateBankAsync(client, ledger, bank.Id, (groceries.Id, -41.25m));
        await ledger.SeedRoundedImportOriginalsAsync(headerId, -41.2468m);

        await PatchBankAsync(client, ledger, headerId, bank.Id, (groceries.Id, -50m));

        var originals = await OriginalsAsync(headerId);
        Assert.NotNull(originals);
        Assert.Equal(-41.2468m, originals.Value.Amount);
        Assert.True(originals.Value.RoundedAtImport);
    }

    /// <summary>
    /// Captured once, an originals row changes on file for two writers only (ADR-0114 D15):
    /// a promote-on-clear restates the amount and its rounded mark, and the SimpleFIN
    /// pre-split backfill restates the payee and memo while they still hold the values it
    /// replaces. Both are tracked writes, so those four need a setter, and every other
    /// column stays init-only.
    /// </summary>
    [Fact]
    public void Only_the_amount_its_mark_the_payee_and_the_memo_are_restated_on_file()
    {
        var restatable = typeof(Coffer.Api.Db.Entities.TxnHeaderOriginalRow)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } setter
                && !setter.ReturnParameter.GetRequiredCustomModifiers()
                    .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Amount", "Memo", "Payee", "RoundedAtImport" }, restatable);
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task<Guid> CreateBankAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, params (Guid Counterparty, decimal Amount)[] postings)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = PostedAt,
                SourceAccountId = source,
                Postings = postings
                    .Select(p => new TransactionPosting { CounterpartyAccountId = p.Counterparty, Amount = p.Amount })
                    .ToArray(),
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task PatchBankAsync(
        HttpClient client, SyntheticLedger ledger, Guid headerId, Guid source, params (Guid Counterparty, decimal Amount)[] postings)
    {
        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = source,
                    Items = postings
                        .Select(p => new TransactionPosting { CounterpartyAccountId = p.Counterparty, Amount = p.Amount })
                        .ToArray(),
                },
            });
        Assert.True(resp.IsSuccessStatusCode, $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<(decimal? Amount, bool RoundedAtImport)?> OriginalsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        var row = await db.TxnHeaderOriginals.AsNoTracking().SingleOrDefaultAsync(o => o.HeaderId == headerId);
        return row is null ? null : (row.Amount, row.RoundedAtImport);
    }

    private async Task<decimal> SumOnAsync(Guid headerId, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == accountId)
            .SumAsync(l => l.Amount);
    }
}
