using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Balance + holdings correctness through the BULK-delete path
/// (<c>POST /transactions/bulk-delete</c>). The bulk repository issues
/// <c>ExecuteDeleteAsync</c> / <c>ExecuteUpdateAsync</c>, which BYPASS the
/// EF ChangeTracker — so neither the balance nor the holdings recompute
/// interceptor fires. The repository must therefore recompute BOTH derived
/// surfaces explicitly. Two properties are pinned here:
///
/// <list type="number">
///   <item><description>Deleting an investment buy must rebuild
///   <c>holdings</c>/<c>lots</c> from the surviving legs — not leave the
///   removed buy's shares behind. (Mig 123's lots-CASCADE means a path that
///   recomputed only balances would drift holdings silently rather than abort
///   on an FK.)</description></item>
///   <item><description>The balance recompute must anchor on the EFFECTIVE
///   date (<c>COALESCE(override.posted_at, header.posted_at)</c>), so deleting
///   a header whose override moved it earlier than its raw date re-walks the
///   vacated <c>[effective, raw)</c> range.</description></item>
/// </list>
///
/// Hand-computed absolute oracles, atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BulkDeleteBalanceTests : DatabaseTest
{
    public BulkDeleteBalanceTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static Task<HttpResponseMessage> BulkDeleteAsync(
        HttpClient client, Guid ledgerId, Guid accountId, params Guid[] headerIds) =>
        client.PostAsJsonAsync(
            $"/api/ledgers/{ledgerId}/transactions/bulk-delete",
            new BulkDeleteRequest
            {
                Selection = new SelectionRequest
                {
                    Kind = "explicit",
                    AccountId = accountId,
                    HeaderIds = headerIds,
                },
            });

    /// <summary>
    /// Bulk-deleting one of two investment buys must drop its shares from the
    /// holding. The brokerage-cash balances are recomputed on either side of
    /// the fix; the holding quantity is the discriminating oracle — it stayed at
    /// both buys' shares before the bulk path learned to recompute holdings.
    /// </summary>
    [Fact]
    public async Task Bulk_delete_investment_buy_recomputes_holdings()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> BuyAsync(int day, decimal shares, decimal price)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    Action = "buy",
                    SecurityId = securityId,
                    Shares = shares,
                    Price = price,
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        // tEarly May 8 and tLate May 12, at the same price; tEarly is the larger buy.
        // Each one's cash out is its shares times the price.
        var tEarly = await BuyAsync(8, 3m, 1000m);
        var tLate = await BuyAsync(12, 1m, 1000m);

        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using (var db = Db.NewDbContext())
        {
            var holding = await db.Holdings.AsNoTracking()
                .SingleAsync(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId);
            Assert.Equal(4m, holding.Quantity); // both buys' shares before the delete
        }

        // Bulk-delete the early buy (manual -> hard delete).
        var resp = await BulkDeleteAsync(client, ledger.LedgerId, brokerage.Id, tEarly);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("hardDeleted").GetInt32());

        await using var dbAfter = Db.NewDbContext();

        // tEarly's balance rows are gone.
        var deletedRows = await dbAfter.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.HeaderId == tEarly).CountAsync();
        Assert.Equal(0, deletedRows);

        // tLate's brokerage cash stands alone: its own cash out, nothing before it.
        var latePost = await dbAfter.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == tLate && r.AccountId == brokerage.Id);
        Assert.Equal(-1000m, latePost.BalanceAfter);

        // The discriminator: holdings rebuilt from the surviving buy only.
        var holdingAfter = await dbAfter.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId);
        Assert.Equal(1m, holdingAfter.Quantity);
    }

    /// <summary>
    /// Bulk-deleting a header whose override moved its effective date EARLIER
    /// than its raw header date must re-walk the vacated range. Anchoring on
    /// the raw date would leave the rows between the effective and raw dates
    /// carrying the deleted header's amount.
    /// </summary>
    [Fact]
    public async Task Bulk_delete_with_earlier_override_recomputes_vacated_range()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> CreateAsync(int day, decimal amount)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    SourceAccountId = bank.Id,
                    Postings = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = category.Id, Amount = amount },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        Task<decimal> BalanceAfterAsync(AppDbContext db, Guid headerId) =>
            db.TxnHeaderAccountBalances.AsNoTracking()
                .Where(r => r.HeaderId == headerId && r.AccountId == bank.Id)
                .Select(r => r.BalanceAfter)
                .SingleAsync();

        // tA May 1, a deposit; tX raw May 20, an outflow; tMid May 10 and tLate
        // May 25, smaller deposits.
        var tA = await CreateAsync(1, 1000m);
        var tX = await CreateAsync(20, -200m);
        var tMid = await CreateAsync(10, 50m);
        var tLate = await CreateAsync(25, 7m);

        // Move tX EARLIER via an override -> effective May 5, BEFORE tMid.
        // Effective order: tA -> tX -> tMid -> tLate, each a running sum.
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{tX}",
            new PatchTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using (var db = Db.NewDbContext())
        {
            Assert.Equal(800m, await BalanceAfterAsync(db, tX));
            Assert.Equal(850m, await BalanceAfterAsync(db, tMid));
            Assert.Equal(857m, await BalanceAfterAsync(db, tLate));
        }

        // Bulk-delete tX (manual -> hard delete). Its EFFECTIVE date is May 5
        // but its RAW date is May 20. Anchoring on the raw date re-walks only
        // rows >= May 20 (just tLate), leaving tMid (May 10) carrying tX's
        // outflow. The effective-date anchor re-walks from May 5.
        var delResp = await BulkDeleteAsync(client, ledger.LedgerId, bank.Id, tX);
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        await using (var db = Db.NewDbContext())
        {
            Assert.Equal(1000m, await BalanceAfterAsync(db, tA));
            Assert.Equal(1050m, await BalanceAfterAsync(db, tMid));  // vacated [May 5, May 20)
            Assert.Equal(1057m, await BalanceAfterAsync(db, tLate));

            var gone = await db.TxnHeaderAccountBalances.AsNoTracking()
                .Where(r => r.HeaderId == tX).CountAsync();
            Assert.Equal(0, gone);
        }
    }

    /// <summary>
    /// A filtered select-all deletes the rows it read and no other: a row another request
    /// moves into the filter while the delete runs, after the selection is read and before
    /// the rows go, is left in place with its balances.
    /// </summary>
    /// <remarks>
    /// <para>The move is made, and committed, as the delete is about to run its delete
    /// statement: every read the delete recomputes from has run by then. Asked again
    /// there, the selection would take the moved row, which none of those reads saw.</para>
    /// <para>The delete holds the ledger write lock (migration 275), so a write that
    /// recomputes, a re-dating PATCH among them, waits for it and cannot move a row in
    /// meanwhile. The move here is one that takes no lock: marking the row cleared, under a
    /// filter on the cleared rows.</para>
    /// </remarks>
    [Fact]
    public async Task A_filtered_select_all_deletes_only_the_rows_it_read()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> CreateAsync(int day, decimal amount)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    SourceAccountId = bank.Id,
                    Postings = new[]
                    {
                        new TransactionPosting { CounterpartyAccountId = category.Id, Amount = amount },
                    },
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        async Task ClearAsync(Guid headerId)
        {
            var put = await client.PutAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}/recon-status",
                new SetReconStatusRequest { Status = "cleared", AccountId = bank.Id });
            Assert.True(put.StatusCode == HttpStatusCode.NoContent,
                $"expected 204, got {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync()}");
        }

        // In the filter, the bank's cleared rows: one, on May 12. Outside it: one on May 2,
        // and one on May 25 after both.
        var inside = await CreateAsync(12, -10m);
        var moved = await CreateAsync(2, -20m);
        var later = await CreateAsync(25, -5m);
        await ClearAsync(inside);

        var atDelete = new BeforeHeaderDelete(() => ClearAsync(moved));
        await using (var db = Db.NewServiceFactoryWith(atDelete).Create())
        {
            var bulk = new Coffer.Api.Db.Repositories.BulkTransactionsRepository(
                db, new Coffer.Api.Db.Repositories.LegDerivedRecomputeService(db),
                new Coffer.Api.Db.Repositories.HoldingsRecomputeService(db), TimeProvider.System);
            var (hard, soft) = await bulk.BulkDeleteAsync(ledger.LedgerId, new SelectionRequest
            {
                Kind = "all",
                AccountId = bank.Id,
                StatusFilter = "cleared",
                SelectedAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            Assert.Equal((1, 0), (hard, soft));
        }
        Assert.True(atDelete.Fired, "the move never ran before the delete statement, so nothing was tested");

        await using var after = Db.NewDbContext();
        Assert.False(await after.TxnHeaders.AsNoTracking().AnyAsync(h => h.Id == inside));
        Assert.True(await after.TxnHeaders.AsNoTracking().AnyAsync(h => h.Id == moved),
            "the row moved into the filter after the selection was read was deleted");
        // The bank's balances walk the two that remain: the moved row, then the later one.
        var balances = await after.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.AccountId == bank.Id)
            .ToDictionaryAsync(r => r.HeaderId, r => r.BalanceAfter);
        Assert.Equal(-20m, balances[moved]);
        Assert.Equal(-25m, balances[later]);
    }

    /// <summary>Runs <paramref name="action"/> once, as the first delete of txn_headers is
    /// about to run on the context it is added to.</summary>
    private sealed class BeforeHeaderDelete(Func<Task> action) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("DELETE FROM txn_headers", StringComparison.Ordinal))
            {
                Fired = true;
                await action();
            }
            return result;
        }
    }
}
