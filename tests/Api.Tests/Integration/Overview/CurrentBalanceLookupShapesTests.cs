using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Integration.Transactions;

namespace Coffer.Api.Tests.Integration.Overview;

/// <summary>
/// The current balance names the same row as the register for every shape its lookup
/// has to get right: rows tied on the latest day, a latest row hidden or merged away, an
/// account with one row or none, and a row dated after today.
/// </summary>
/// <remarks>
/// <para>The current balance (<c>account_balances_as_of_now</c>) is the running balance
/// on the latest row the register does not call Scheduled, in the register's own order,
/// <c>(posted_at, seq)</c>, with the opening balance when there is none. Each account
/// below is read three ways that do not share a lookup, and all three must agree: the
/// function, the register's running balance on its newest counted row, and the
/// net-worth history's feeder (<c>account_balance_as_of_instants</c>) at the last
/// instant the register counts. The view that serves a reader with no date
/// (<c>account_current_balances</c>, every ledger at the server's date) must agree with
/// the function at that date.</para>
/// <para>Every row is written through the API, so each shape is one the app produces:
/// deleting an imported row hides it, and merging folds an imported row into one entered
/// by hand.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CurrentBalanceLookupShapesTests : DatabaseTest
{
    public CurrentBalanceLookupShapesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>The caller's today; every row is dated relative to it.</summary>
    private static readonly DateOnly Today = new(2026, 6, 15);

    [Fact]
    public async Task Every_lookup_shape_reads_the_registers_own_latest_row()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var category = await ledger.AddCategoryAsync("Category A");
        var tied = await ledger.AddBankAccountAsync("Account A", openingBalance: 1_000m);
        var hiddenLatest = await ledger.AddBankAccountAsync("Account B", openingBalance: 1_000m);
        var mergedLatest = await ledger.AddBankAccountAsync("Account C", openingBalance: 1_000m);
        var single = await ledger.AddBankAccountAsync("Account D", openingBalance: 1_000m);
        var none = await ledger.AddBankAccountAsync("Account E", openingBalance: 1_000m);
        var scheduled = await ledger.AddBankAccountAsync("Account F", openingBalance: 1_000m);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Two rows on the latest counted day: the one entered second is the register's
        // last, so its running balance is the current one.
        await PostAsync(client, ledger, tied.Id, category.Id, -10m, Today.AddDays(-9));
        await PostAsync(client, ledger, tied.Id, category.Id, -20m, Today.AddDays(-2));
        await PostAsync(client, ledger, tied.Id, category.Id, -40m, Today.AddDays(-2));

        // The latest row imported, then deleted, which hides it.
        await PostAsync(client, ledger, hiddenLatest.Id, category.Id, -15m, Today.AddDays(-8));
        await ImportAsync(client, ledger, hiddenLatest.Id, "SHAPES-B", ("SHAPES-B-1", Today.AddDays(-1), -35m));
        var toHide = await ImportedHeaderAsync(ledger, hiddenLatest.Id);
        var hidden = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{toHide}");
        Assert.True(hidden.IsSuccessStatusCode, await hidden.Content.ReadAsStringAsync());

        // The latest row imported, then folded into an earlier row entered by hand.
        var winner = await PostAsync(client, ledger, mergedLatest.Id, category.Id, -25m, Today.AddDays(-7));
        await ImportAsync(client, ledger, mergedLatest.Id, "SHAPES-C", ("SHAPES-C-1", Today.AddDays(-1), -45m));
        var toMerge = await ImportedHeaderAsync(ledger, mergedLatest.Id);
        var merged = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{toMerge}/merge", new MergeTransactionRequest { FromHeaderId = winner });
        Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());

        // One row, long before today, beside an account with none.
        await PostAsync(client, ledger, single.Id, category.Id, -55m, Today.AddDays(-300));

        // A counted row and one the register calls Scheduled.
        await PostAsync(client, ledger, scheduled.Id, category.Id, -65m, Today);
        await PostAsync(client, ledger, scheduled.Id, category.Id, -75m, Today.AddDays(1));

        var expected = new Dictionary<Guid, decimal>
        {
            [tied.Id] = 930m,
            [hiddenLatest.Id] = 985m,
            [mergedLatest.Id] = 975m,
            [single.Id] = 945m,
            [none.Id] = 1_000m,
            [scheduled.Id] = 935m,
        };

        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var current = await new AccountBalancesRepository(db, TimeProvider.System)
            .GetCurrentBalancesAsync(ledger.LedgerId, activeOnly: false, Today);
        var lastCounted = ScheduledBoundary.LastCountedInstant(Today);
        var history = (await db.AccountBalanceAsOfInstants(ledger.LedgerId, [lastCounted], null).ToListAsync())
            .ToDictionary(r => r.AccountId, r => r.Balance);

        foreach (var (accountId, figure) in expected)
        {
            Assert.Equal(figure, current[accountId]);
            Assert.Equal(figure, await RegisterCurrentBalanceAsync(client, ledger, accountId));
            Assert.Equal(figure, history[accountId]);
        }

        // The view, for a reader with no date, is the function at the server's date, for
        // every account of the ledger.
        var atServerDate = await new AccountBalancesRepository(db, TimeProvider.System)
            .GetCurrentBalancesAsync(ledger.LedgerId, activeOnly: false, today: null);
        var view = await db.AccountCurrentBalances.AsNoTracking()
            .Where(v => v.LedgerId == ledger.LedgerId)
            .ToDictionaryAsync(v => v.AccountId, v => v.Balance);
        Assert.Equal(atServerDate.OrderBy(p => p.Key), view.OrderBy(p => p.Key));
        Assert.Equal(1_000m - 65m - 75m, view[scheduled.Id]);
    }

    // ---- helpers ----------------------------------------------------------------------

    private static async Task<Guid> PostAsync(
        HttpClient client, SyntheticLedger ledger, Guid source, Guid counterparty, decimal amount, DateOnly on)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions", new CreateTransactionRequest
        {
            PostedAt = on.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Payee = "Payee A",
            SourceAccountId = source,
            Postings = [new TransactionPosting { CounterpartyAccountId = counterparty, Amount = amount }],
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task ImportAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, string accountNumber,
        params (string FitId, DateOnly On, decimal Amount)[] rows)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(TransactionEntriesEndpointTests.OfxBankStatement(
            accountNumber, [.. rows.Select(r => (r.FitId, r.On, r.Amount, "PAYEE A"))])));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(file, "file", "statement.qfx");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent($"{TransactionEntriesEndpointTests.BankNumber}:{accountNumber}"), "providerAccountId");
        var response = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import", content);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The one imported header on <paramref name="accountId"/>.</summary>
    private async Task<Guid> ImportedHeaderAsync(SyntheticLedger ledger, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledger.LedgerId && h.ExternalId != null && !h.IsRecurringTemplate
                        && db.TxnLegs.Any(l => l.HeaderId == h.Id && l.AccountId == accountId))
            .Select(h => h.Id)
            .SingleAsync();
    }

    /// <summary>The register's running balance on the newest row it counts for
    /// <see cref="Today"/>, in its own display order, else the opening balance.</summary>
    private async Task<decimal> RegisterCurrentBalanceAsync(HttpClient client, SyntheticLedger ledger, Guid accountId)
    {
        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountId}&today={Today:yyyy-MM-dd}&limit=100"))!;
        var rows = new List<RegisterRowDto>();
        foreach (var entry in page.Entries)
        {
            if (entry.Kind == RegisterEntryDto.KindTxn && entry.Txn is not null) rows.Add(entry.Txn);
            else if (entry.Legs is not null) rows.AddRange(entry.Legs);
        }
        // Newest first, as the register lists them; the first it counts is its latest.
        var counted = rows.FirstOrDefault(r => DateOnly.FromDateTime(r.PostedAt) <= Today);
        if (counted is not null) return counted.BalanceAfter!.Value;

        await using var db = Db.NewDbContext();
        return await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.OpeningBalance).SingleAsync();
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }
}
