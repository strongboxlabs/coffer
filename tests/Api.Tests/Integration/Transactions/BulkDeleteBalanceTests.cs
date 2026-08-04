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
/// surfaces explicitly. Two regressions are pinned here:
///
/// <list type="number">
///   <item><description>Deleting an investment buy must rebuild
///   <c>holdings</c>/<c>lots</c> from the surviving legs — not leave the
///   removed buy's shares behind. (Before the fix this path recomputed only
///   balances; mig 123's lots-CASCADE turned the previously-loud FK abort
///   into a silent holdings drift.)</description></item>
///   <item><description>The balance recompute must anchor on the EFFECTIVE
///   date (<c>COALESCE(override.posted_at, header.posted_at)</c>), so deleting
///   a header whose override moved it earlier than its raw date re-walks the
///   vacated <c>[effective, raw)</c> range.</description></item>
/// </list>
///
/// Hand-computed absolute oracles, atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BulkDeleteBalanceTests
{
    private readonly PostgresFixture _fixture;

    public BulkDeleteBalanceTests(PostgresFixture fixture) => _fixture = fixture;

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

    private static Task<HttpResponseMessage> BulkDeleteAsync(
        HttpClient client, Guid ledgerId, params Guid[] headerIds) =>
        client.PostAsJsonAsync(
            $"/api/ledgers/{ledgerId}/transactions/bulk-delete",
            new BulkDeleteRequest
            {
                Selection = new SelectionRequest
                {
                    Kind = "explicit",
                    HeaderIds = headerIds,
                },
            });

    /// <summary>
    /// Bulk-deleting one of two investment buys must drop its shares from the
    /// holding. The brokerage-cash balances are recomputed on either side of
    /// the fix; the holding quantity is the discriminating oracle — it drifted
    /// to 4 (3 + 1) before the bulk path learned to recompute holdings.
    /// </summary>
    [Fact]
    public async Task Bulk_delete_investment_buy_recomputes_holdings()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
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

        // tEarly May 8: 3 @ 1000 = -3000.  tLate May 12: 1 @ 1000 = -1000.
        var tEarly = await BuyAsync(8, 3m, 1000m);
        var tLate = await BuyAsync(12, 1m, 1000m);

        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using (var db = _fixture.NewDbContext())
        {
            var holding = await db.Holdings.AsNoTracking()
                .SingleAsync(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId);
            Assert.Equal(4m, holding.Quantity); // 3 + 1 before the delete
        }

        // Bulk-delete the early buy (manual -> hard delete).
        var resp = await BulkDeleteAsync(client, ledger.LedgerId, tEarly);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("hardDeleted").GetInt32());

        await using var dbAfter = _fixture.NewDbContext();

        // tEarly's balance rows are gone.
        var deletedRows = await dbAfter.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.HeaderId == tEarly).CountAsync();
        Assert.Equal(0, deletedRows);

        // tLate brokerage cash stands alone at -1000.
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var category = await ledger.AddCategoryAsync("category");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
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

        // tA May 1 +1000; tX raw May 20 -200; tMid May 10 +50; tLate May 25 +7.
        var tA = await CreateAsync(1, 1000m);
        var tX = await CreateAsync(20, -200m);
        var tMid = await CreateAsync(10, 50m);
        var tLate = await CreateAsync(25, 7m);

        // Move tX EARLIER via an override -> effective May 5, BEFORE tMid.
        // Effective order: tA(1000) -> tX(800) -> tMid(850) -> tLate(857).
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{tX}",
            new PatchTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(800m, await BalanceAfterAsync(db, tX));
            Assert.Equal(850m, await BalanceAfterAsync(db, tMid));
            Assert.Equal(857m, await BalanceAfterAsync(db, tLate));
        }

        // Bulk-delete tX (manual -> hard delete). Its EFFECTIVE date is May 5
        // but its RAW date is May 20. Anchoring on the raw date re-walks only
        // rows >= May 20 (just tLate), leaving tMid (May 10) carrying tX's
        // -200. The effective-date anchor re-walks from May 5.
        var delResp = await BulkDeleteAsync(client, ledger.LedgerId, tX);
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        await using (var db = _fixture.NewDbContext())
        {
            Assert.Equal(1000m, await BalanceAfterAsync(db, tA));
            Assert.Equal(1050m, await BalanceAfterAsync(db, tMid));  // vacated [May 5, May 20)
            Assert.Equal(1057m, await BalanceAfterAsync(db, tLate));

            var gone = await db.TxnHeaderAccountBalances.AsNoTracking()
                .Where(r => r.HeaderId == tX).CountAsync();
            Assert.Equal(0, gone);
        }
    }
}
