using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Accounts;

/// <summary>
/// Editing an account's opening balance re-derives its stored running balances.
/// </summary>
/// <remarks>
/// <para>
/// The opening balance is the SEED of every <c>txn_header_account_balances</c> row on
/// the account (mig 206: the walk starts from the last stored balance before its anchor,
/// else <c>accounts.opening_balance</c>). Change it and every stored row is stale.
/// </para>
/// <para>
/// Nothing noticed. <c>UpdateAsync</c> writes the column with
/// <c>ExecuteUpdateAsync</c>, which bypasses the ChangeTracker, so
/// <c>LegDerivedRecomputeInterceptor</c> never fires — and its switch matches only
/// TxnLegRow / TxnHeaderRow and the two override rows, never AccountRow, so a
/// SaveChanges path would not have helped either. Migration 088 dropped the last
/// trigger on <c>accounts</c>. The result was wrong balances in the register, on the
/// dashboard and in net-worth history, and they did NOT self-heal: the incremental
/// recompute seeds from the last stored row before its anchor, so it inherits the
/// stale figure. Only a manual repair cleared it.
/// </para>
/// <para>
/// No test covered this. <c>LedgerBalanceRecomputeEquivalenceTests</c> sets the column
/// with raw SQL and rebuilds by hand, which sidesteps the write path entirely, and the
/// accounts mutation tests only round-trip the DTO. So this asserts the property the
/// bug violated: PATCH the opening balance, then READ what is stored.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class OpeningBalanceRecomputeTests
{
    private readonly PostgresFixture _fixture;

    public OpeningBalanceRecomputeTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Changing_the_opening_balance_reseeds_every_stored_running_balance()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("Checking", openingBalance: 250.75m);
        var expense = await ledger.AddCategoryAsync("Groceries", "expense");

        // Three headers, so a reseed has to move a SEQUENCE of rows, not just one.
        await ledger.AddTransactionPairAsync(
            checking.Id, expense.Id, -20m, new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(
            checking.Id, expense.Id, -30m, new DateTime(2026, 2, 5, 12, 0, 0, DateTimeKind.Utc));
        await ledger.AddTransactionPairAsync(
            checking.Id, expense.Id, -40m, new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));

        var before = await StoredBalancesAsync(ledger, checking.Id);
        Assert.Equal([230.75m, 200.75m, 160.75m], before);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The correction a user makes when they realise the account started elsewhere.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{checking.Id}",
            new UpdateAccountRequest { OpeningBalance = 1250.75m });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        // Every row shifts by the delta (+1000), in order. Before the fix these stayed
        // at the old figures indefinitely — each one exactly 1000 short.
        var after = await StoredBalancesAsync(ledger, checking.Id);
        Assert.Equal([1230.75m, 1200.75m, 1160.75m], after);
    }

    [Fact]
    public async Task An_edit_that_leaves_the_opening_balance_alone_does_not_disturb_the_balances()
    {
        // The recompute is conditional, so the cheap path stays cheap: renaming an
        // account must not trigger a full-account rewalk. Asserting the balances are
        // untouched is the observable half of that.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("Checking", openingBalance: 100m);
        var expense = await ledger.AddCategoryAsync("Groceries", "expense");
        await ledger.AddTransactionPairAsync(
            checking.Id, expense.Id, -25m, new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{checking.Id}",
            new UpdateAccountRequest { Name = "Main Checking" });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        Assert.Equal([75m], await StoredBalancesAsync(ledger, checking.Id));
    }

    /// <summary>Stored <c>balance_after</c> for the account, in posted order.</summary>
    private async Task<List<decimal>> StoredBalancesAsync(SyntheticLedger ledger, Guid accountId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(b => b.AccountId == accountId)
            .Join(db.TxnHeaders.AsNoTracking(), b => b.HeaderId, h => h.Id, (b, h) => new { b, h })
            .OrderBy(x => x.h.PostedAt).ThenBy(x => x.h.Seq)
            .Select(x => x.b.BalanceAfter)
            .ToListAsync();
    }
}
