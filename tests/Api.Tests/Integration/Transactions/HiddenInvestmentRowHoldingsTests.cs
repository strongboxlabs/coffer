using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Deleting an imported investment row hides it, and restoring it unhides it; either
/// way the save recomputes the position of every investment line the row has, with no
/// line written (ADR-0032, 2026-11-03 amendment; ADR-0040).
/// </summary>
/// <remarks>
/// <para>The ledger comes in through the Moneydance import, the product path that writes
/// the imported row: every header it writes carries its export id, so the investment
/// delete takes the soft-hide branch, and the import stores the position its purchase
/// makes and the purchase's lot.</para>
/// <para>The purchase is the position's only acquisition, so with it hidden the walk
/// holds no shares there: the recompute stores quantity 0, closes the lot, and the
/// Overview values nothing. A save that recomputed nothing would leave the purchase's
/// shares stored, and the consistency check would report them.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class HiddenInvestmentRowHoldingsTests : DatabaseTest
{
    public HiddenInvestmentRowHoldingsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>The purchase's shares, as the export states them (<c>samt</c> at four decimals).</summary>
    private const decimal Shares = 10m;

    /// <summary>What the purchase paid (<c>pamt</c> in cents).</summary>
    private const decimal Paid = 100m;

    /// <summary>A brokerage holding one security, bought once.</summary>
    private const string OneBuyExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"curr","id":"c-fund","type":"s","currid":"^FUND","name":"Fund A","ticker":"FUND","dec":"4","rate":"1","rrate":"1"},
            {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund A","parentid":"a-brk","currid":"c-fund","sbal":"0"},
            {"obj_type":"txn","id":"t-buy","acctid":"a-brk","desc":"Buy","dt":"20260105",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-1","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000"}
          ]
        }
        """;

    [Fact]
    public async Task Deleting_an_imported_buy_takes_its_shares_out_of_the_stored_position()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var ledgerId = await ImportLedgerAsync(client, OneBuyExport);
        var buy = await HeaderOfAsync(ledgerId, "t-buy");
        await AssertBoughtAsync(client, ledgerId, buy);

        await DeleteAsync(client, ledgerId, buy);

        await AssertHiddenAsync(client, ledgerId, buy);
    }

    [Fact]
    public async Task Restoring_a_deleted_imported_buy_puts_its_shares_back()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var ledgerId = await ImportLedgerAsync(client, OneBuyExport);
        var buy = await HeaderOfAsync(ledgerId, "t-buy");
        var before = await AssertBoughtAsync(client, ledgerId, buy);
        await DeleteAsync(client, ledgerId, buy);
        await AssertHiddenAsync(client, ledgerId, buy);

        // A restore of one row is a bulk unhide of a selection of one.
        Guid brokerage;
        await using (var db = Db.NewDbContext())
            brokerage = (await db.Accounts.AsNoTracking()
                .SingleAsync(a => a.LedgerId == ledgerId && a.HoldingsAccountId != null)).Id;
        var unhide = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/transactions/bulk-unhide",
            new BulkUnhideRequest
            {
                Selection = new SelectionRequest { Kind = "explicit", AccountId = brokerage, HeaderIds = [buy] },
            });
        Assert.Equal(HttpStatusCode.OK, unhide.StatusCode);
        using (var body = JsonDocument.Parse(await unhide.Content.ReadAsStringAsync()))
            Assert.Equal(1, body.RootElement.GetProperty("unhidden").GetInt32());

        await using (var db = Db.NewDbContext())
            Assert.False((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == buy)).IsHidden);
        var position = await StoredPositionAsync(ledgerId);
        Assert.Equal(Shares, position.Quantity);
        Assert.Equal(Paid, position.CostBasis);
        var lot = await LotOfAsync(buy);
        Assert.False(lot.IsClosed);
        Assert.Equal(Shares, lot.Quantity);
        await AssertHealthyAsync(client, ledgerId, "after the restore");
        Assert.Equal(before, await PortfolioAsync(client, ledgerId));
    }

    // ---- the states ----------------------------------------------------------------

    /// <summary>The state the import leaves: the purchase stored as the position, its
    /// lot open, a ledger that checks clean. Returns the Overview's portfolio.</summary>
    private async Task<Portfolio> AssertBoughtAsync(HttpClient client, Guid ledgerId, Guid buy)
    {
        await using (var db = Db.NewDbContext())
        {
            var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == buy);
            // An export id is what sends the delete to the soft-hide branch.
            Assert.NotNull(header.ExternalId);
            Assert.False(header.IsHidden);
        }
        var position = await StoredPositionAsync(ledgerId);
        Assert.Equal(Shares, position.Quantity);
        Assert.Equal(Paid, position.CostBasis);
        var lot = await LotOfAsync(buy);
        Assert.False(lot.IsClosed);
        Assert.Equal(Shares, lot.Quantity);
        await AssertHealthyAsync(client, ledgerId, "after the import");

        // The Overview counts the shares, so there is something for a delete to take out.
        var portfolio = await PortfolioAsync(client, ledgerId);
        Assert.Equal(Paid, portfolio.CostBasis);
        Assert.True(portfolio.Value > 0m, $"the Overview valued the purchase at {portfolio.Value}");
        return portfolio;
    }

    /// <summary>The state the delete must leave: the purchase hidden, the position
    /// stored as the walk holds it (no shares), the lot closed, the ledger clean, and
    /// the Overview valuing nothing.</summary>
    private async Task AssertHiddenAsync(HttpClient client, Guid ledgerId, Guid buy)
    {
        await using (var db = Db.NewDbContext())
            Assert.True((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == buy)).IsHidden);
        var position = await StoredPositionAsync(ledgerId);
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(0m, position.CostBasis);
        var lot = await LotOfAsync(buy);
        Assert.True(lot.IsClosed);
        Assert.Equal(0m, lot.Quantity);
        await AssertHealthyAsync(client, ledgerId, "after the delete");
        var portfolio = await PortfolioAsync(client, ledgerId);
        Assert.Equal(0m, portfolio.Value);
        Assert.Equal(0m, portfolio.CostBasis);
    }

    private static async Task DeleteAsync(HttpClient client, Guid ledgerId, Guid buy)
    {
        var delete = await client.DeleteAsync($"/api/ledgers/{ledgerId}/investment-transactions/{buy}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using var body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        Assert.Equal("soft-hidden", body.RootElement.GetProperty("kind").GetString());
    }

    // ---- reads -----------------------------------------------------------------------

    private sealed record Position(decimal Quantity, decimal CostBasis);

    private sealed record Portfolio(decimal Value, decimal CostBasis);

    private sealed record Lot(decimal Quantity, bool IsClosed);

    /// <summary>The ledger's one stored position.</summary>
    private async Task<Position> StoredPositionAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId)
            .Select(h => new Position(h.Quantity, h.CostBasis))
            .SingleAsync();
    }

    /// <summary>The lot of the purchase's security line.</summary>
    private async Task<Lot> LotOfAsync(Guid buy)
    {
        await using var db = Db.NewDbContext();
        return await (from lot in db.Lots.AsNoTracking()
                      join leg in db.TxnLegs.AsNoTracking() on lot.LegId equals leg.Id
                      where leg.HeaderId == buy
                      select new Lot(lot.Quantity, lot.IsClosed)).SingleAsync();
    }

    private static async Task<Portfolio> PortfolioAsync(HttpClient client, Guid ledgerId)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/ledgers/{ledgerId}/overview"));
        var portfolio = doc.RootElement.GetProperty("portfolio");
        return new Portfolio(portfolio.GetProperty("value").GetDecimal(), portfolio.GetProperty("costBasis").GetDecimal());
    }

    /// <summary>The consistency check, through its endpoint, reports every projection
    /// healthy; otherwise the failure names each disagreeing figure.</summary>
    private static async Task AssertHealthyAsync(HttpClient client, Guid ledgerId, string when)
    {
        using var doc = JsonDocument.Parse(await (await client.PostAsync($"/api/ledgers/{ledgerId}/balances/consistency", null))
            .EnsureSuccessStatusCode().Content.ReadAsStringAsync());
        var report = doc.RootElement;
        var failing = report.GetProperty("projections").EnumerateArray()
            .Where(p => !p.GetProperty("healthy").GetBoolean())
            .Select(p => p.GetProperty("projection").GetString() + ": "
                         + string.Join("; ", p.GetProperty("mismatches").EnumerateArray().Select(m =>
                             $"{m.GetProperty("field").GetString()} stored {m.GetProperty("stored").GetDecimal()}"
                             + $" expected {m.GetProperty("expected").GetDecimal()}")))
            .ToList();
        Assert.True(report.GetProperty("healthy").GetBoolean(),
            $"{when}, the consistency check is unhealthy: "
            + (failing.Count > 0 ? string.Join(" | ", failing) : "oversold positions listed"));
    }

    // ---- the import ------------------------------------------------------------------

    /// <summary>Import <paramref name="json"/> through the Moneydance import route and
    /// return the new ledger, once the run has succeeded.</summary>
    internal static async Task<Guid> ImportLedgerAsync(HttpClient client, string json)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "export.json");
        content.Add(new StringContent("Ledger " + Guid.NewGuid().ToString("N")[..8]), "ledgerName");
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.True(started.StatusCode == HttpStatusCode.OK,
            $"import: {(int)started.StatusCode} {await started.Content.ReadAsStringAsync()}");
        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var jobId = startedBody.RootElement.GetProperty("jobId").GetString()!;

        // The import is a background run, so this is a real wait; the ceiling makes a hang fail.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var status = JsonDocument.Parse(await client.GetStringAsync($"/api/imports/moneydance/{jobId}"));
            var state = status.RootElement.GetProperty("state").GetString();
            if (state == "running")
            {
                await Task.Delay(250);
                continue;
            }
            Assert.True(state == "succeeded", $"the import ended '{state}': {status.RootElement.GetRawText()}");
            return Guid.Parse(status.RootElement.GetProperty("ledgerId").GetString()!);
        }
        Assert.Fail("The import never left the 'running' state.");
        return Guid.Empty;
    }

    private async Task<Guid> HeaderOfAsync(Guid ledgerId, string externalId)
    {
        await using var db = Db.NewDbContext();
        return (await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledgerId && h.ExternalId == externalId)).Id;
    }
}
