using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// ADR-0084: an investment TRADE leg written through the normal EF write path
/// (native investment-transactions endpoint / MCP) seeds a <c>trade</c>-source
/// row into <c>security_prices</c> via the <see cref="TradePriceFromLegInterceptor"/>.
/// The rank gate (<c>manual == fetch &gt; trade &gt; simplefin &gt; import</c>)
/// means a trade beats an import/simplefin row but a fetch/manual price for the
/// day is never clobbered. Each test bootstraps an atomic
/// <see cref="SyntheticLedger"/> and scopes every query to its ledger id.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TradeDerivedPricesTests : DatabaseTest
{
    public TradeDerivedPricesTests(PostgresFixture fixture, ITestOutputHelper output)
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

    private async Task<Guid> BuyAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId,
        decimal shares, decimal price, DateTime postedAt)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerageId,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = shares,
                Price = price,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();
    }

    private async Task<List<SecurityPriceRow>> PriceRowsAsync(SyntheticLedger ledger, Guid securityId)
    {
        await using var db = Db.NewDbContext();
        return await db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == ledger.LedgerId && p.SecurityId == securityId)
            .ToListAsync();
    }

    [Fact]
    public async Task Buy_through_the_write_path_seeds_a_trade_source_price()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 10m, price: 100m, postedAt);

        var rows = await PriceRowsAsync(ledger, securityId);
        var row = Assert.Single(rows);
        Assert.Equal(PriceSource.Trade, row.Source);
        // unit_price = amount / |shares|, where amount = round(shares * price, 2); with a
        // whole-cent product that recovers the trade price exactly (ADR-0073).
        Assert.Equal(100m, row.Price);
        Assert.Equal(new DateOnly(2026, 5, 15), row.PriceDate);
    }

    [Fact]
    public async Task Manual_write_overwrites_a_trade_price_for_the_same_day()
    {
        // Rank gate (ADR-0084 D1): manual outranks trade, so a later manual price
        // reclaims the day — a Yahoo close would do the same via the feed.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 10m, price: 100m, postedAt);

        var manual = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/securities/{securityId}/prices",
            new { price = 123.45m, priceDate = new DateOnly(2026, 5, 15) });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);

        var rows = await PriceRowsAsync(ledger, securityId);
        var row = Assert.Single(rows);   // still one row per (security, day)
        Assert.Equal(PriceSource.Manual, row.Source);
        Assert.Equal(123.45m, row.Price);
    }

    [Fact]
    public async Task Trade_does_not_clobber_a_pre_existing_manual_price()
    {
        // The reverse direction: a manual price already owns the day; a same-day
        // trade must NOT overwrite it (the function's DO UPDATE ... WHERE gate
        // excludes manual/fetch rows).
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        var day = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc);
        await ledger.AddSecurityPriceAsync(securityId, 999.99m, day, source: PriceSource.Manual);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 10m, price: 100m, postedAt);

        var rows = await PriceRowsAsync(ledger, securityId);
        var row = Assert.Single(rows);
        Assert.Equal(PriceSource.Manual, row.Source);
        Assert.Equal(999.99m, row.Price);
    }

    [Fact]
    public async Task Trade_overwrites_a_pre_existing_import_price()
    {
        // A trade is a truer observation than the one-time import seed (ADR-0084
        // D5): the same-day import row is replaced by the execution price.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        var day = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc);
        await ledger.AddSecurityPriceAsync(securityId, 50m, day, source: PriceSource.Import);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 10m, price: 100m, postedAt);

        var rows = await PriceRowsAsync(ledger, securityId);
        var row = Assert.Single(rows);
        Assert.Equal(PriceSource.Trade, row.Source);
        Assert.Equal(100m, row.Price);
    }

    [Fact]
    public async Task Dividend_cash_writes_no_trade_price_row()
    {
        // A priceless leg (dividend_cash carries pamt = 0 -> unit_price 0/NULL) is
        // not a trade and seeds nothing (ADR-0084 D2).
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        var income = await ledger.AddCategoryAsync("Dividends", kind: "income");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc),
                Action = "dividend_cash",
                SecurityId = securityId,
                CategoryAccountId = income.Id,
                Amount = 42m,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var rows = await PriceRowsAsync(ledger, securityId);
        Assert.Empty(rows);
    }

    /// <summary>
    /// An edit that only moves a trade's date prices the day it moves to, as an edit
    /// re-upserts its day (ADR-0084 D4), though no line changes: the date is the
    /// header's, and a changed header date counts as a change to every line it has
    /// (ADR-0032, 2026-11-03 amendment).
    /// </summary>
    /// <remarks>
    /// The edit sends the trade as it stands with another date, so the editor keeps every
    /// line, with its id and its values. The day the trade left keeps its price, as a
    /// deleted trade's does (ADR-0084 D4).
    /// </remarks>
    [Fact]
    public async Task A_date_only_edit_prices_the_day_the_trade_moves_to()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var from = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 20, 14, 30, 0, DateTimeKind.Utc);
        var headerId = await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 10m, price: 100m, from);
        var legsBefore = await LegsAsync(headerId);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = to,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
            });
        Assert.True(
            patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: {await patch.Content.ReadAsStringAsync()}");

        // Premise: the edit kept every line as it was; only the header moved.
        Assert.Equal(legsBefore, await LegsAsync(headerId));

        var rows = await PriceRowsAsync(ledger, securityId);
        var moved = Assert.Single(rows, r => r.PriceDate == DateOnly.FromDateTime(to));
        Assert.Equal(PriceSource.Trade, moved.Source);
        Assert.Equal(100m, moved.Price);
        Assert.Single(rows, r => r.PriceDate == DateOnly.FromDateTime(from));
    }

    // =================================================================
    // A trade leaving its day (ADR-0084 D4, 2026-11-03 amendment): the day is
    // re-derived from the executions that remain, and keeps its row when none
    // does. Each case puts two executions on one day, the later (by seq) at the
    // higher price, so the day's stored price is the later one's until it leaves.
    // =================================================================

    private static readonly DateTime TradeDay = new(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);

    /// <summary>Two buys on <see cref="TradeDay"/>, the later at the higher price, which
    /// the day's stored price then is.</summary>
    private async Task<(Guid Earlier, Guid Later)> TwoTradesOnOneDayAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId)
    {
        var earlier = await BuyAsync(client, ledger, brokerageId, securityId, shares: 10m, price: 100m, TradeDay);
        var later = await BuyAsync(client, ledger, brokerageId, securityId, shares: 5m, price: 110m, TradeDay.AddHours(1));
        var row = Assert.Single(await PriceRowsAsync(ledger, securityId));
        Assert.Equal(110m, row.Price);
        return (earlier, later);
    }

    [Fact]
    public async Task A_deleted_trade_leaves_its_day_priced_by_the_execution_that_remains()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var (_, later) = await TwoTradesOnOneDayAsync(client, ledger, brokerage.Id, securityId);

        var delete = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions/{later}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Contains("hard-deleted", await delete.Content.ReadAsStringAsync());

        var row = Assert.Single(await PriceRowsAsync(ledger, securityId));
        Assert.Equal((PriceSource.Trade, DateOnly.FromDateTime(TradeDay), 100m), (row.Source, row.PriceDate, row.Price));
        await AssertNoTradePriceFindingAsync(client, ledger);
    }

    [Fact]
    public async Task A_re_dated_trade_leaves_its_old_day_priced_by_the_execution_that_remains()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var (_, later) = await TwoTradesOnOneDayAsync(client, ledger, brokerage.Id, securityId);

        var to = TradeDay.AddDays(5);
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{later}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = to,
                Action = "buy",
                SecurityId = securityId,
                Shares = 5m,
                Price = 110m,
            });
        Assert.True(
            patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: {await patch.Content.ReadAsStringAsync()}");

        var rows = await PriceRowsAsync(ledger, securityId);
        Assert.Equal(100m, Assert.Single(rows, r => r.PriceDate == DateOnly.FromDateTime(TradeDay)).Price);
        Assert.Equal(110m, Assert.Single(rows, r => r.PriceDate == DateOnly.FromDateTime(to)).Price);
        Assert.All(rows, r => Assert.Equal(PriceSource.Trade, r.Source));
        await AssertNoTradePriceFindingAsync(client, ledger);
    }

    /// <summary>
    /// An edit that moves a trade to another security takes its line off the first
    /// security's day: the editor drops the line and writes one for the other security,
    /// with the header's date unchanged, so only the line says the trade left.
    /// </summary>
    [Fact]
    public async Task A_trade_moved_to_another_security_leaves_the_first_securitys_day_to_what_remains()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        var otherId = await ledger.AddSecurityAsync("ETFB", ticker: "ETFB");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var (_, later) = await TwoTradesOnOneDayAsync(client, ledger, brokerage.Id, securityId);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{later}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = TradeDay.AddHours(1),
                Action = "buy",
                SecurityId = otherId,
                Shares = 5m,
                Price = 110m,
            });
        Assert.True(
            patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: {await patch.Content.ReadAsStringAsync()}");

        Assert.Equal(100m, Assert.Single(await PriceRowsAsync(ledger, securityId)).Price);
        Assert.Equal(110m, Assert.Single(await PriceRowsAsync(ledger, otherId)).Price);
        await AssertNoTradePriceFindingAsync(client, ledger);
    }

    /// <summary>
    /// An edit that takes a trade's amount to 0 changes its lines in place: the editor keeps
    /// each one, the security line on its security, its unit price now 0, so it no longer
    /// prices its day. Only the line says the trade left: the header's date and action are
    /// unchanged, and the line is no execution to arrive anywhere.
    /// </summary>
    [Fact]
    public async Task A_trade_edited_to_no_price_leaves_its_day_priced_by_the_execution_that_remains()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var (_, later) = await TwoTradesOnOneDayAsync(client, ledger, brokerage.Id, securityId);
        var legsBefore = await LegsAsync(later);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{later}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = TradeDay.AddHours(1),
                Action = "buy",
                SecurityId = securityId,
                Shares = 5m,
                Price = 110m,
                Amount = 0m,
            });
        Assert.True(
            patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: {await patch.Content.ReadAsStringAsync()}");

        // Premise: the same lines, the security line still holding its shares, at no price.
        var legsAfter = await LegsAsync(later);
        Assert.Equal(legsBefore.Select(l => l.Id), legsAfter.Select(l => l.Id));
        Assert.Contains(legsAfter, l => l.Quantity == 5m && l.UnitPrice == 0m);

        var row = Assert.Single(await PriceRowsAsync(ledger, securityId));
        Assert.Equal((PriceSource.Trade, DateOnly.FromDateTime(TradeDay), 100m), (row.Source, row.PriceDate, row.Price));
        await AssertNoTradePriceFindingAsync(client, ledger);
    }

    [Fact]
    public async Task A_bulk_deleted_trade_leaves_its_day_priced_by_the_execution_that_remains()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var (_, later) = await TwoTradesOnOneDayAsync(client, ledger, brokerage.Id, securityId);

        var delete = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-delete",
            new BulkDeleteRequest
            {
                Selection = new SelectionRequest { Kind = "explicit", AccountId = brokerage.Id, HeaderIds = [later] },
            });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(1, (await delete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hardDeleted").GetInt32());

        var row = Assert.Single(await PriceRowsAsync(ledger, securityId));
        Assert.Equal((PriceSource.Trade, 100m), (row.Source, row.Price));
        await AssertNoTradePriceFindingAsync(client, ledger);
    }

    /// <summary>
    /// The day's only execution deleted: no execution remains to derive the day from, so
    /// its price is kept, a past execution being a real observation (ADR-0084 D4). The
    /// check does not report it; the unbacked-price advisory lists the security.
    /// </summary>
    [Fact]
    public async Task A_days_only_trade_deleted_keeps_its_price_and_the_check_reports_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var only = await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 10m, price: 100m, TradeDay);

        var delete = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions/{only}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var row = Assert.Single(await PriceRowsAsync(ledger, securityId));
        Assert.Equal((PriceSource.Trade, DateOnly.FromDateTime(TradeDay), 100m), (row.Source, row.PriceDate, row.Price));
        var check = await AssertNoTradePriceFindingAsync(client, ledger);
        Assert.Contains(securityId, check.Unbacked);
    }

    /// <summary>
    /// Merging a security into another (MCP <c>merge_securities</c>) moves its executions
    /// onto the keeper's days without the change tracker, so the merge re-derives them:
    /// the keeper prices each such day, and the merged-away security keeps its own rows
    /// (ADR-0084 D4).
    /// </summary>
    [Fact]
    public async Task A_security_merge_prices_the_keepers_days_from_the_moved_trades()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var sourceId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");
        var keeperId = await ledger.AddSecurityAsync("ETFB", ticker: "ETFB");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        await BuyAsync(client, ledger, brokerage.Id, sourceId, shares: 10m, price: 100m, TradeDay);
        Assert.Empty(await PriceRowsAsync(ledger, keeperId));

        await using (var db = Db.NewServiceFactory().Create())
        {
            var merged = await McpWriteTools.MergeSecurities(
                McpTestGuard.Writable(), new SecuritiesRepository(db, TimeProvider.System),
                ledger.LedgerId, sourceId, keeperId, dryRun: false);
            Assert.True(merged.Ok);
        }

        var keeper = Assert.Single(await PriceRowsAsync(ledger, keeperId));
        Assert.Equal((PriceSource.Trade, DateOnly.FromDateTime(TradeDay), 100m), (keeper.Source, keeper.PriceDate, keeper.Price));
        Assert.Single(await PriceRowsAsync(ledger, sourceId));
        await AssertNoTradePriceFindingAsync(client, ledger);
    }

    /// <summary>
    /// The consistency check, through its endpoint, reports no trade-price finding and is
    /// healthy; otherwise the failure names each finding. Returns the securities the
    /// unbacked-price advisory lists.
    /// </summary>
    private static async Task<(List<Guid> Unbacked, bool Healthy)> AssertNoTradePriceFindingAsync(
        HttpClient client, SyntheticLedger ledger)
    {
        using var doc = JsonDocument.Parse(await (await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency", null)).EnsureSuccessStatusCode()
            .Content.ReadAsStringAsync());
        var report = doc.RootElement;
        var failing = report.GetProperty("projections").EnumerateArray()
            .Where(p => !p.GetProperty("healthy").GetBoolean())
            .Select(p => p.GetProperty("projection").GetString() + ": "
                         + string.Join("; ", p.GetProperty("mismatches").EnumerateArray().Select(m =>
                             $"{m.GetProperty("scope").GetString()} {m.GetProperty("field").GetString()}"
                             + $" stored {m.GetProperty("stored").GetDecimal()} expected {m.GetProperty("expected").GetDecimal()}")))
            .ToList();
        Assert.True(failing.Count == 0, "the consistency check reports: " + string.Join(" | ", failing));
        var healthy = report.GetProperty("healthy").GetBoolean();
        Assert.True(healthy, "the consistency check is unhealthy with every projection healthy: oversold positions listed");
        var unbacked = report.GetProperty("unbackedPrices").EnumerateArray()
            .Select(u => u.GetProperty("securityId").GetGuid())
            .ToList();
        return (unbacked, healthy);
    }

    /// <summary>The header's lines, each as id and the values a trade price reads.</summary>
    private async Task<List<(Guid Id, decimal Amount, decimal? Quantity, decimal? UnitPrice)>> LegsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return (await db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == headerId)
                .OrderBy(l => l.Id)
                .Select(l => new { l.Id, l.Amount, l.Quantity, l.UnitPrice })
                .ToListAsync())
            .Select(l => (l.Id, l.Amount, l.Quantity, l.UnitPrice))
            .ToList();
    }

    [Fact]
    public async Task Trade_price_date_is_the_utc_calendar_day_of_posted_at()
    {
        // A late-UTC-day trade still lands on that UTC date (ADR-0084 D3): the
        // interceptor normalizes posted_at to UTC before deriving the day.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("ETFA", ticker: "ETFA");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 15, 23, 30, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, brokerage.Id, securityId, shares: 5m, price: 200m, postedAt);

        var rows = await PriceRowsAsync(ledger, securityId);
        var row = Assert.Single(rows);
        Assert.Equal(PriceSource.Trade, row.Source);
        Assert.Equal(new DateOnly(2026, 5, 15), row.PriceDate);
    }
}
