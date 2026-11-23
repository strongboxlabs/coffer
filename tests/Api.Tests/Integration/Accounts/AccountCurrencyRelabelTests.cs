using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// Changing a real account's currency relabels (ADR-0114 D13, ADR-0050 D8): every stored
/// figure on the account, and on a brokerage's Holdings sibling (ADR-0019 Rule 4), is kept
/// and reads in the new currency, and the PATCH answers how many mirrored pairs now carry
/// two amounts. It is refused, with nothing written, when a two-amount pair would land in
/// one currency with amounts that differ (<c>relabel-pair-not-representable</c>), or when a
/// stored figure carries more places than the new currency allows
/// (<c>relabel-places-exceed-currency</c>, ADR-0114 D15).
/// </summary>
/// <remarks>
/// Transactions are written through <c>POST /transactions</c> and
/// <c>POST /investment-transactions</c>, and the currency through
/// <c>PATCH /accounts/{id}</c>, on synthetic ledgers. The investment seeds use the
/// synthetic ledger's helpers, the shapes the Moneydance importer writes.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AccountCurrencyRelabelTests : DatabaseTest
{
    public AccountCurrencyRelabelTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string PairCode = "relabel-pair-not-representable";
    private const string PlacesCode = "relabel-places-exceed-currency";
    private static readonly DateTime PostedAt = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

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

    private static Task<HttpResponseMessage> SetCurrencyAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, string currencyCode) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{accountId}")
        {
            Content = JsonContent.Create(new UpdateAccountRequest { CurrencyCode = currencyCode }),
        });

    /// <summary>The edit's count of pairs that now carry two amounts.</summary>
    private static async Task<int> RelabelledAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return (await response.Content.ReadFromJsonAsync<UpdateAccountResponse>())!.BecameTwoAmount;
    }

    private static async Task<(string? Code, int Count)> RefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return (doc.RootElement.GetProperty("code").GetString(), doc.RootElement.GetProperty("count").GetInt32());
    }

    private async Task<string> CurrencyOfAsync(Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).CurrencyCode;
    }

    private async Task<IReadOnlyList<TxnLegRow>> LegsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync();
    }

    private static decimal On(IReadOnlyList<TxnLegRow> legs, Guid accountId) =>
        Assert.Single(legs, l => l.AccountId == accountId).Amount;

    // ---- pairs (ADR-0114 D13) --------------------------------------------------------------

    [Fact]
    public async Task A_two_amount_pair_whose_sides_differ_refuses_a_change_into_the_other_sides_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var headerId = await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = eur.Id, Amount = -100m, CounterpartyAmount = 91.37m });

        Assert.Equal((PairCode, 1), await RefusalAsync(await SetCurrencyAsync(client, ledger, usd.Id, "EUR")));
        Assert.Equal("USD", await CurrencyOfAsync(usd.Id));
        var legs = await LegsAsync(headerId);
        Assert.Equal((-100m, 91.37m), (On(legs, usd.Id), On(legs, eur.Id)));

        // The negative anchor: into a third currency the pair stays across two, so it passes.
        Assert.Equal(0, await RelabelledAsync(await SetCurrencyAsync(client, ledger, usd.Id, "GBP")));
        Assert.Equal("GBP", await CurrencyOfAsync(usd.Id));
    }

    // ---- a pair on a transaction merged into another (ADR-0114 D13, owner 2026-11-18) -------

    /// <summary>The pair refusal's first sentence, for a change into
    /// <paramref name="currencyCode"/>.</summary>
    internal static string PairDetail(string currencyCode) =>
        $"A transfer between two currencies would end up in {currencyCode} with two amounts that differ, which one currency cannot hold.";

    private static readonly string PairDetailEur = PairDetail("EUR");
    private static readonly string LiveDetail = PairDetailEur + " Make its two amounts match first.";
    internal const string MergedAwaySentence =
        " That transfer was merged into another transaction, so no register shows it.";
    internal const string MixedSentence =
        " Where you can open the transfer, make its two amounts match first. Where it was merged into "
        + "another transaction, no register shows it.";
    internal const string RestoreSentence =
        " Deleting the transaction it was merged into brings it back, so you can make its two amounts match.";

    /// <summary>
    /// A feed row awaiting review, saved as a transfer to an account in another currency
    /// with a second amount of another magnitude, then merged into a row entered by hand,
    /// is in no register. A currency change that would put both its sides in one currency
    /// is still refused, as nothing detects such a pair later, and the refusal names the
    /// transaction it was merged into and says that deleting it brings the row back
    /// (decided by the owner, 2026-11-18; ADR-0114 D13). Before the merge, the same pair
    /// gets the refusal a pair on a row a register shows gets, with no <c>mergedInto</c>.
    /// </summary>
    /// <remarks>Every step runs through the API: the entry by hand, a statement import, the
    /// bank editor's save, the merge and the change.</remarks>
    [Fact]
    public async Task A_two_amount_pair_on_a_row_merged_into_another_names_the_transaction_it_was_merged_into()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var winner = await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -9m });
        var feed = (await ImportRowsAsync(client, ledger, usd.Id, [("RELABEL-PAIR-1", -9m)]))[0];
        await SaveAsTransferAsync(client, ledger, feed, usd.Id, eur.Id, -9m, 8.10m);

        // The anchor: awaiting review, the row is in a register, so the refusal is a live pair's.
        Assert.Equal((1, LiveDetail, false),
            ToLive(await PairRefusalAsync(await SetCurrencyAsync(client, ledger, usd.Id, "EUR"))));

        await MergeAsync(client, ledger, feed, winner);

        var refusal = await PairRefusalAsync(await SetCurrencyAsync(client, ledger, usd.Id, "EUR"));
        Assert.Equal((1, PairDetailEur + MergedAwaySentence + RestoreSentence), (refusal.Count, refusal.Detail));
        Assert.Equal((winner, usd.Id, "Bank A", "2026-03-02", "Payee A", false), Assert.Single(refusal.MergedInto!));
        Assert.Equal("USD", await CurrencyOfAsync(usd.Id));
        var legs = await LegsAsync(feed);
        Assert.Equal((-9m, 8.10m), (On(legs, usd.Id), On(legs, eur.Id)));
    }

    /// <summary>
    /// A pair on a row a register shows and one on a row merged into another, refused
    /// together: the count is both, the detail says what to do with each, and
    /// <c>mergedInto</c> names only the transaction the merged row was merged into.
    /// </summary>
    [Fact]
    public async Task A_live_pair_and_a_merged_away_pair_are_refused_together_and_only_the_merged_one_is_named()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = eur.Id, Amount = -100m, CounterpartyAmount = 91.37m });
        var winner = await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -9m });
        var feed = (await ImportRowsAsync(client, ledger, usd.Id, [("RELABEL-PAIR-2", -9m)]))[0];
        await SaveAsTransferAsync(client, ledger, feed, usd.Id, eur.Id, -9m, 8.10m);
        await MergeAsync(client, ledger, feed, winner);

        var refusal = await PairRefusalAsync(await SetCurrencyAsync(client, ledger, usd.Id, "EUR"));

        Assert.Equal((2, PairDetailEur + MixedSentence + RestoreSentence), (refusal.Count, refusal.Detail));
        Assert.Equal(winner, Assert.Single(refusal.MergedInto!).HeaderId);
        Assert.Equal("USD", await CurrencyOfAsync(usd.Id));
    }

    /// <summary>
    /// A row merged into an imported row: deleting an imported row only hides it, which
    /// un-merges nothing, so the refusal names the transaction and promises no way back. Once
    /// that transaction is deleted, so hidden, the entry says it is hidden, for a link to
    /// the register's Hidden view (ADR-0072 D1).
    /// </summary>
    [Fact]
    public async Task A_pair_merged_into_an_imported_row_is_named_with_no_way_back_and_hidden_once_deleted()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var imported = await ImportRowsAsync(client, ledger, usd.Id, [("RELABEL-PAIR-3", -9m), ("RELABEL-PAIR-4", -9m)]);
        var (winner, feed) = (imported[0], imported[1]);
        using (var approved = await client.PatchAsJsonAsync(
                   $"/api/ledgers/{ledger.LedgerId}/transactions/{winner}", new PatchTransactionRequest { Approve = true }))
            Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
        await SaveAsTransferAsync(client, ledger, feed, usd.Id, eur.Id, -9m, 8.10m);
        await MergeAsync(client, ledger, feed, winner);

        var refusal = await PairRefusalAsync(await SetCurrencyAsync(client, ledger, usd.Id, "EUR"));
        Assert.Equal((1, PairDetailEur + MergedAwaySentence), (refusal.Count, refusal.Detail));
        var entry = Assert.Single(refusal.MergedInto!);
        Assert.Equal((winner, usd.Id, false), (entry.HeaderId, entry.AccountId, entry.Hidden));

        using (var deleted = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{winner}"))
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        await using (var db = Db.NewDbContext())
            Assert.Equal(winner, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == feed)).IsMergedInto);

        var hidden = await PairRefusalAsync(await SetCurrencyAsync(client, ledger, usd.Id, "EUR"));
        Assert.Equal((1, PairDetailEur + MergedAwaySentence), (hidden.Count, hidden.Detail));
        var hiddenEntry = Assert.Single(hidden.MergedInto!);
        Assert.Equal((winner, true), (hiddenEntry.HeaderId, hiddenEntry.Hidden));
        Assert.Equal("USD", await CurrencyOfAsync(usd.Id));
    }

    /// <summary>
    /// Two pairs on rows merged away, one into a row entered by hand and one into an
    /// imported row, refused together: both transactions are named, and the refusal
    /// promises no way back, as deleting the imported row only hides it (settled by Claude,
    /// 2026-11-18; ADR-0114 D13). While only the row entered by hand is a winner, the way
    /// back is promised.
    /// </summary>
    [Fact]
    public async Task Pairs_merged_into_a_row_entered_by_hand_and_an_imported_row_promise_no_way_back()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var byHand = await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -9m });
        var imported = await ImportRowsAsync(client, ledger, usd.Id,
            [("RELABEL-PAIR-5", -9m), ("RELABEL-PAIR-6", -9m), ("RELABEL-PAIR-7", -9m)]);
        var (importedWinner, intoByHand, intoImported) = (imported[0], imported[1], imported[2]);
        using (var approved = await client.PatchAsJsonAsync(
                   $"/api/ledgers/{ledger.LedgerId}/transactions/{importedWinner}", new PatchTransactionRequest { Approve = true }))
            Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
        await SaveAsTransferAsync(client, ledger, intoByHand, usd.Id, eur.Id, -9m, 8.10m);
        await SaveAsTransferAsync(client, ledger, intoImported, usd.Id, eur.Id, -9m, 8.20m);
        await MergeAsync(client, ledger, intoByHand, byHand);

        // The anchor: the one merged-away pair's winner was entered by hand, so the way back
        // is promised, and the other pair is still awaiting review in a register.
        var anchor = await PairRefusalAsync(await SetCurrencyAsync(client, ledger, usd.Id, "EUR"));
        Assert.Equal((2, PairDetailEur + MixedSentence + RestoreSentence), (anchor.Count, anchor.Detail));

        await MergeAsync(client, ledger, intoImported, importedWinner);

        var refusal = await PairRefusalAsync(await SetCurrencyAsync(client, ledger, usd.Id, "EUR"));
        Assert.Equal((2, PairDetailEur + MergedAwaySentence), (refusal.Count, refusal.Detail));
        Assert.Equal(
            new[] { byHand, importedWinner }.Order(),
            refusal.MergedInto!.Select(m => m.HeaderId).Order());
        Assert.Equal("USD", await CurrencyOfAsync(usd.Id));
    }

    /// <summary>A pair refusal: its count, detail and <c>mergedInto</c> entries (null when
    /// the member is absent), each as (header, account, account name, date, payee,
    /// hidden).</summary>
    internal static async Task<(int Count, string Detail, List<(Guid HeaderId, Guid AccountId, string Account, string Date, string? Payee, bool Hidden)>? MergedInto)>
        PairRefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(PairCode, root.GetProperty("code").GetString());
        Assert.Equal("Transfer amounts differ", root.GetProperty("title").GetString());
        var mergedInto = root.TryGetProperty("mergedInto", out var list)
            ? list.EnumerateArray().Select(e => (
                    e.GetProperty("headerId").GetGuid(),
                    e.GetProperty("accountId").GetGuid(),
                    e.GetProperty("account").GetString()!,
                    e.GetProperty("date").GetString()!,
                    e.GetProperty("payee").GetString(),
                    e.GetProperty("hidden").GetBoolean()))
                .ToList()
            : null;
        return (root.GetProperty("count").GetInt32(), root.GetProperty("detail").GetString()!, mergedInto);
    }

    private static (int Count, string Detail, bool HasMergedInto) ToLive(
        (int Count, string Detail, List<(Guid, Guid, string, string, string?, bool)>? MergedInto) refusal) =>
        (refusal.Count, refusal.Detail, refusal.MergedInto is not null);

    /// <summary>The bank editor's save of a row as a transfer to <paramref name="to"/> with
    /// a second amount, leaving it awaiting review.</summary>
    private async Task SaveAsTransferAsync(
        HttpClient client, SyntheticLedger ledger, Guid headerId, Guid from, Guid to, decimal amount, decimal counterpartyAmount)
    {
        Guid legId;
        await using (var db = Db.NewDbContext())
            legId = (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.HeaderId == headerId && l.AccountId == from)).Id;
        using var response = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}", new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = from,
                Items = [new TransactionPosting { LegId = legId, CounterpartyAccountId = to, Amount = amount, CounterpartyAmount = counterpartyAmount }],
            },
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Fold <paramref name="loser"/> into <paramref name="winner"/>, as the bank
    /// editor's merge candidate does.</summary>
    private static async Task MergeAsync(HttpClient client, SyntheticLedger ledger, Guid loser, Guid winner)
    {
        using var merged = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());
    }

    /// <summary>Import <paramref name="rows"/> onto <paramref name="accountId"/> from one
    /// OFX statement, as a person imports a bank's file, and return the rows it writes, in
    /// order, each awaiting review.</summary>
    private async Task<IReadOnlyList<Guid>> ImportRowsAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, IReadOnlyList<(string FitId, decimal Amount)> rows)
    {
        const string AccountNumber = "RELABEL-PAIR";
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

    [Fact]
    public async Task A_two_amount_pair_of_equal_magnitudes_lands_in_one_currency_as_a_mirror()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var eur = await ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var headerId = await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = eur.Id, Amount = -50m, CounterpartyAmount = 50m });

        Assert.Equal(0, await RelabelledAsync(await SetCurrencyAsync(client, ledger, usd.Id, "EUR")));
        Assert.Equal("EUR", await CurrencyOfAsync(usd.Id));
        var legs = await LegsAsync(headerId);
        Assert.Equal((-50m, 50m), (On(legs, usd.Id), On(legs, eur.Id)));
    }

    [Fact]
    public async Task Each_mirrored_transfer_to_an_account_in_the_old_currency_is_counted_and_keeps_its_figures()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var usd = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var savings = await ledger.AddBankAccountAsync("Bank B", currencyCode: "USD");
        var card = await ledger.AddAccountAsync("Card A", "credit_card", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var toSavings = await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = savings.Id, Amount = -25m });
        var fromCard = await CreateAsync(client, ledger, card.Id,
            new TransactionPosting { CounterpartyAccountId = usd.Id, Amount = -40m });
        // An expense mirrors its category, which takes the account's currency: not counted.
        var expense = await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -12.34m });
        // A zero transfer is a mirrored pair too, and zero and zero is representable across
        // currencies (ADR-0114 D13: zero and zero is representable across currencies).
        var zero = await CreateAsync(client, ledger, usd.Id,
            new TransactionPosting { CounterpartyAccountId = savings.Id, Amount = 0m });

        Assert.Equal(3, await RelabelledAsync(await SetCurrencyAsync(client, ledger, usd.Id, "EUR")));

        Assert.Equal((-25m, 25m), (On(await LegsAsync(toSavings), usd.Id), On(await LegsAsync(toSavings), savings.Id)));
        Assert.Equal((-40m, 40m), (On(await LegsAsync(fromCard), card.Id), On(await LegsAsync(fromCard), usd.Id)));
        Assert.Equal((-12.34m, 12.34m), (On(await LegsAsync(expense), usd.Id), On(await LegsAsync(expense), category.Id)));
        Assert.Equal((0m, 0m), (On(await LegsAsync(zero), usd.Id), On(await LegsAsync(zero), savings.Id)));

        // Any other edit relabels nothing.
        var rename = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{usd.Id}")
        {
            Content = JsonContent.Create(new UpdateAccountRequest { Name = "Bank C" }),
        });
        Assert.Equal(0, await RelabelledAsync(rename));
    }

    [Fact]
    public async Task A_brokerages_change_relabels_its_holdings_sibling_and_counts_its_transfers()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "USD");
        var holdings = brokerage.HoldingsAccountId!.Value;
        var bank = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var security = await ledger.AddSecurityAsync("Security A", "SECA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var cash = await PostCashTransferAsync(client, ledger, brokerage.Id, bank.Id);
        // Shares in: a security posting between the brokerage and its holdings, and a
        // transfer posting between the brokerage and the bank.
        var sharesIn = await ledger.AddSharesInAsync(brokerage.Id, holdings, bank.Id, security, 10m, 120m, PostedAt);

        // The cash transfer and the shares-in transfer posting are counted; the security
        // posting relabels on both sides and keeps its shape.
        Assert.Equal(2, await RelabelledAsync(await SetCurrencyAsync(client, ledger, brokerage.Id, "EUR")));
        Assert.Equal(("EUR", "EUR"), (await CurrencyOfAsync(brokerage.Id), await CurrencyOfAsync(holdings)));
        Assert.Equal(0m, (await LegsAsync(cash)).Sum(l => l.Amount));

        await using var db = Db.NewDbContext();
        var sharesInHeader = (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == sharesIn)).HeaderId;
        Assert.Equal(120m, On(await LegsAsync(sharesInHeader), holdings));
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
    public async Task A_figure_with_cents_refuses_a_change_to_a_whole_unit_currency_and_whole_figures_pass()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var cents = await ledger.AddBankAccountAsync("Bank A", currencyCode: "USD");
        var whole = await ledger.AddBankAccountAsync("Bank B", currencyCode: "USD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await CreateAsync(client, ledger, cents.Id, new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -12.34m });
        await CreateAsync(client, ledger, cents.Id, new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -3m });
        await CreateAsync(client, ledger, whole.Id, new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -12m });

        Assert.Equal((PlacesCode, 1), await RefusalAsync(await SetCurrencyAsync(client, ledger, cents.Id, "JPY")));
        Assert.Equal("USD", await CurrencyOfAsync(cents.Id));

        Assert.Equal(0, await RelabelledAsync(await SetCurrencyAsync(client, ledger, whole.Id, "JPY")));
        Assert.Equal("JPY", await CurrencyOfAsync(whole.Id));
        // The negative anchor: another two-place currency takes the cents.
        Assert.Equal(0, await RelabelledAsync(await SetCurrencyAsync(client, ledger, cents.Id, "EUR")));
    }

    [Fact]
    public async Task A_three_place_figure_refuses_a_change_to_a_two_place_currency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var thousandths = await ledger.AddBankAccountAsync("Bank A", currencyCode: "KWD");
        var hundredths = await ledger.AddBankAccountAsync("Bank B", currencyCode: "KWD");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        await CreateAsync(client, ledger, thousandths.Id, new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1.234m });
        await CreateAsync(client, ledger, hundredths.Id, new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -1.23m });

        Assert.Equal((PlacesCode, 1), await RefusalAsync(await SetCurrencyAsync(client, ledger, thousandths.Id, "USD")));
        Assert.Equal("KWD", await CurrencyOfAsync(thousandths.Id));
        Assert.Equal(0, await RelabelledAsync(await SetCurrencyAsync(client, ledger, hundredths.Id, "USD")));
    }

    // The Holdings sibling's figures are the brokerage's (ADR-0019 Rule 4): a share leg that
    // does not fit refuses the brokerage's change though every brokerage leg fits.
    [Fact]
    public async Task A_holdings_figure_that_does_not_fit_refuses_its_brokerages_change()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var from = await ledger.AddInvestmentAccountAsync("Brokerage A", currencyCode: "USD");
        var to = await ledger.AddInvestmentAccountAsync("Brokerage B", currencyCode: "USD");
        var security = await ledger.AddSecurityAsync("Security A", "SECA");
        // Shares moved in kind: the brokerages' own legs are zero, the holdings legs carry
        // the value, which has cents.
        await ledger.AddTransferSharesAsync(
            from.Id, from.HoldingsAccountId!.Value, to.Id, to.HoldingsAccountId!.Value, security, 1m, 12.34m, PostedAt);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        Assert.Equal((PlacesCode, 1), await RefusalAsync(await SetCurrencyAsync(client, ledger, to.Id, "JPY")));
        Assert.Equal(("USD", "USD"), (await CurrencyOfAsync(to.Id), await CurrencyOfAsync(to.HoldingsAccountId!.Value)));
    }
}
