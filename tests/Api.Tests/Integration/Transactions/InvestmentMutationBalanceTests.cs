using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Balance correctness across the INVESTMENT transaction lifecycle
/// (create / PATCH / delete via <c>InvestmentTransactionsEndpoints</c>).
/// Each test drives one investment-shape mutation through the HTTP
/// surface and asserts exact <c>net_amount</c> / <c>balance_after</c>
/// (and holdings quantity where cheap) read off the stored tables — the
/// independent, hand-computed oracle.
///
/// Sign convention (from the create repository's posting builders): a
/// <c>buy</c> posts brokerage cash <c>-(shares*price)</c> plus a
/// <c>-fee</c> leg when a fee is supplied; a <c>sell</c> posts
/// <c>+(|shares|*price)</c>; <c>dividend_reinvest</c> nets brokerage cash
/// to zero (an income pair <c>+principal</c> cancels the security pair
/// <c>-principal</c>). <c>Shares</c> is a SIGNED delta (negative on a
/// dispose). PATCH is a wholesale reshape (ADR-0025): the body is the
/// full new state. Atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvestmentMutationBalanceTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentMutationBalanceTests(PostgresFixture fixture) => _fixture = fixture;

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

    /// <summary>Buy then partial sell: brokerage cash + holdings shift,
    /// open quantity drops to the remainder.</summary>
    [Fact]
    public async Task Create_sell_after_buy_shifts_cash_and_holdings()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Buy 10 @ $650 = -$6500 brokerage cash, +6500 holdings, qty 10.
        var buyResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 650m,
            });
        Assert.Equal(HttpStatusCode.Created, buyResp.StatusCode);

        // Sell 4 @ $700 = +$2800 brokerage cash, -$2800 holdings, qty -> 6.
        var sellResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc),
                Action = "sell",
                SecurityId = securityId,
                Shares = -4m,
                Price = 700m,
            });
        Assert.Equal(HttpStatusCode.Created, sellResp.StatusCode);
        var sellHeaderId = (await sellResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var db = _fixture.NewDbContext();

        // Brokerage cash after the sell: -6500 (buy) + 2800 (sell) = -3700.
        var brokerageAfterSell = await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == sellHeaderId && r.AccountId == brokerage.Id);
        Assert.Equal(2800m, brokerageAfterSell.NetAmount);
        Assert.Equal(-3700m, brokerageAfterSell.BalanceAfter);

        var holdingsAfterSell = await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == sellHeaderId && r.AccountId == holdingsAccountId);
        Assert.Equal(-2800m, holdingsAfterSell.NetAmount);

        // FIFO recompute leaves 6 shares open (10 bought - 4 sold).
        var holding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId);
        Assert.Equal(6m, holding.Quantity);
    }

    /// <summary>Dividend reinvest is cash-neutral on the brokerage and
    /// adds shares to the holding.</summary>
    [Fact]
    public async Task Create_dividend_reinvest_is_cash_neutral()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        var divIncome = await ledger.AddCategoryAsync("dividend-income");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc),
                Action = "dividend_reinvest",
                SecurityId = securityId,
                Shares = 2m,
                Price = 650m,
                CategoryAccountId = divIncome.Id,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var headerId = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var db = _fixture.NewDbContext();

        // Brokerage cash nets to zero: +1300 (income) - 1300 (security).
        var brokerageRow = await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == headerId && r.AccountId == brokerage.Id);
        Assert.Equal(0m, brokerageRow.NetAmount);

        var holding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId);
        Assert.Equal(2m, holding.Quantity);
    }

    /// <summary>Buy WITH a fee leg charges principal + fee to brokerage
    /// cash; the fee category takes the offsetting leg.</summary>
    [Fact]
    public async Task Create_buy_with_fee_posting_charges_cash_and_fee_leg()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        var feeCategory = await ledger.AddCategoryAsync("brokerage-fees");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 650m,
                FeeAccountId = feeCategory.Id,
                FeeAmount = 9.95m,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var headerId = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        await using var db = _fixture.NewDbContext();

        // Brokerage cash: principal -6500 plus the -9.95 fee leg = -6509.95.
        var brokerageRow = await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == headerId && r.AccountId == brokerage.Id);
        Assert.Equal(-6509.95m, brokerageRow.NetAmount);
        Assert.Equal(-6509.95m, brokerageRow.BalanceAfter);

        var feeRow = await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == headerId && r.AccountId == feeCategory.Id);
        Assert.Equal(9.95m, feeRow.NetAmount);
    }

    /// <summary>PATCH a buy's shares (wholesale reshape): cash + holdings
    /// follow the new quantity.</summary>
    [Fact]
    public async Task Patch_buy_shares_shifts_cash_balance()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var createResp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 650m,
            });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var headerId = (await createResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        await using (var db = _fixture.NewDbContext())
        {
            var pre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == headerId && r.AccountId == brokerage.Id);
            Assert.Equal(-6500m, pre.BalanceAfter);
        }

        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 12m,
                Price = 650m,
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var dbAfter = _fixture.NewDbContext();
        var post = await dbAfter.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == headerId && r.AccountId == brokerage.Id);
        Assert.Equal(-7800m, post.NetAmount);
        Assert.Equal(-7800m, post.BalanceAfter);

        var holding = await dbAfter.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId);
        Assert.Equal(12m, holding.Quantity);
    }

    /// <summary>PATCH an investment txn's date LATER past another on the
    /// same brokerage — the vacated-range recompute on brokerage cash (the
    /// investment-editor analogue of the bank date-move regression).</summary>
    [Fact]
    public async Task Patch_posted_at_later_recomputes_vacated_range_on_brokerage_cash()
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

        // tA May 5: -1000   tX May 10: -200   tB May 15: -50  (running -1250)
        var tA = await BuyAsync(5, 1m, 1000m);
        var tX = await BuyAsync(10, 1m, 200m);
        var tB = await BuyAsync(15, 1m, 50m);

        await using (var db = _fixture.NewDbContext())
        {
            var tBpre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == tB && r.AccountId == brokerage.Id);
            Assert.Equal(-1250m, tBpre.BalanceAfter);
        }

        // Move tX to May 20 — AFTER tB (full reshape body at the new date).
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{tX}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = securityId,
                Shares = 1m,
                Price = 200m,
            });
        Assert.True(
            patchResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patchResp.StatusCode}: {await patchResp.Content.ReadAsStringAsync()}");

        // New order: tA (-1000) -> tB (-1050) -> tX (-1250). tB vacated -> -1050.
        await using (var db = _fixture.NewDbContext())
        {
            var tAbal = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == tA && r.AccountId == brokerage.Id);
            var tBbal = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == tB && r.AccountId == brokerage.Id);
            var tXbal = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == tX && r.AccountId == brokerage.Id);
            Assert.Equal(-1000m, tAbal.BalanceAfter);
            Assert.Equal(-1050m, tBbal.BalanceAfter);
            Assert.Equal(-1250m, tXbal.BalanceAfter);
        }
    }

    /// <summary>DELETE a manual buy: legs + lots cascade, no stale balance
    /// rows, the survivor walks alone, holdings reconcile.</summary>
    [Fact]
    public async Task Delete_buy_leaves_no_stale_balance_rows()
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

        // tEarly May 8: -3000   tLate May 12: -1000 (running -4000).
        var tEarly = await BuyAsync(8, 3m, 1000m);
        var tLate = await BuyAsync(12, 1m, 1000m);

        await using (var db = _fixture.NewDbContext())
        {
            var latePre = await db.TxnHeaderAccountBalances.AsNoTracking()
                .SingleAsync(r => r.HeaderId == tLate && r.AccountId == brokerage.Id);
            Assert.Equal(-4000m, latePre.BalanceAfter);
        }

        var deleteResp = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{tEarly}");
        Assert.True(
            deleteResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)deleteResp.StatusCode}: {await deleteResp.Content.ReadAsStringAsync()}");

        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var dbAfter = _fixture.NewDbContext();

        var deletedRows = await dbAfter.TxnHeaderAccountBalances.AsNoTracking()
            .Where(r => r.HeaderId == tEarly).CountAsync();
        Assert.Equal(0, deletedRows);

        var latePost = await dbAfter.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(r => r.HeaderId == tLate && r.AccountId == brokerage.Id);
        Assert.Equal(-1000m, latePost.BalanceAfter);

        var holding = await dbAfter.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId);
        Assert.Equal(1m, holding.Quantity);
    }

    /// <summary>
    /// Delete a Buy whose lot was already FIFO-consumed (<c>is_closed</c>) by
    /// a later Sell. Pre-mig-123 this aborted with a 23503 — the closed lot
    /// still RESTRICT-referenced the buy's leg. With <c>lots.leg_id ON DELETE
    /// CASCADE</c> the leg + its closed lot cascade away, and the post-save
    /// recompute rebuilds the holding from the surviving legs (the Sell now
    /// drains the later Buy instead). This is the headline mig-123 scenario
    /// the PR #196 audit flagged as sound-by-construction but unverified.
    /// </summary>
    [Fact]
    public async Task Delete_buy_whose_lot_was_consumed_cascades_and_rebuilds_holding()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> TradeAsync(int day, string action, decimal shares, decimal price)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    PostedAt = new DateTime(2026, 5, day, 12, 0, 0, DateTimeKind.Utc),
                    Action = action,
                    SecurityId = securityId,
                    Shares = shares,
                    Price = price,
                });
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        // Buy A 10 @ 100 (May 1), Buy B 20 @ 120 (May 5), Sell 10 @ 150
        // (May 10). FIFO drains A's 10 first -> A's lot closes; B stays open
        // at 20. Holding = 20.
        var tBuyA = await TradeAsync(1, "buy", 10m, 100m);
        await TradeAsync(5, "buy", 20m, 120m);
        await TradeAsync(10, "sell", -10m, 150m);

        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using (var db = _fixture.NewDbContext())
        {
            var lots = await db.Lots.AsNoTracking()
                .Where(l => l.LedgerId == ledger.LedgerId)
                .OrderBy(l => l.UnitCost)
                .ToListAsync();
            Assert.Equal(2, lots.Count);
            Assert.Equal(100m, lots[0].UnitCost);   // Buy A
            Assert.True(lots[0].IsClosed);           // fully consumed by the Sell
            Assert.Equal(0m, lots[0].Quantity);
            Assert.Equal(120m, lots[1].UnitCost);    // Buy B
            Assert.False(lots[1].IsClosed);
            Assert.Equal(20m, lots[1].Quantity);

            var holding = await db.Holdings.AsNoTracking()
                .SingleAsync(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId);
            Assert.Equal(20m, holding.Quantity);
        }

        // Delete Buy A — the consumed (closed) lot.
        var deleteResp = await client.DeleteAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{tBuyA}");
        Assert.True(
            deleteResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)deleteResp.StatusCode}: {await deleteResp.Content.ReadAsStringAsync()}");

        await using (var db = _fixture.NewDbContext())
        {
            // A's closed lot is gone; the Sell now drains 10 of B -> B open
            // at 10. Exactly one lot remains.
            var lot = Assert.Single(await db.Lots.AsNoTracking()
                .Where(l => l.LedgerId == ledger.LedgerId)
                .ToListAsync());
            Assert.Equal(120m, lot.UnitCost);
            Assert.False(lot.IsClosed);
            Assert.Equal(10m, lot.Quantity);

            var holding = await db.Holdings.AsNoTracking()
                .SingleAsync(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId);
            Assert.Equal(10m, holding.Quantity);    // 20 bought (B) - 10 sold
            Assert.Equal(1200m, holding.CostBasis); // 10 @ 120
        }
    }
}
