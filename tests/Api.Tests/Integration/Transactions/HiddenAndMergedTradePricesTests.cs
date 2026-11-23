using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// A hidden or merged-away row is no trade in the ledger, for a day's trade price as for
/// valuation (migration 273, ADR-0084 2026-11-04 amendment): hiding, restoring, merging
/// and un-merging a trade each re-derive the day it sits on, singly and in bulk, and the
/// consistency check reads the same rule.
/// </summary>
/// <remarks>
/// <para>The imported rows come in through the Moneydance import, the product path that
/// writes them: every header it writes carries its export id, so a delete takes the
/// soft-hide branch. Two purchases of one fund sit on one day; the import gives them
/// their order (<c>seq</c>), and the day's price is the later one's until it leaves.</para>
/// <para>A merge's loser is a statement row, which lands awaiting review with no security;
/// a person turns it into a purchase with the investment edit and then folds it into the
/// entry it duplicates, or edits it after the merge. Both are API calls.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class HiddenAndMergedTradePricesTests : DatabaseTest
{
    public HiddenAndMergedTradePricesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>The day the imported purchases sit on.</summary>
    private static readonly DateOnly ImportedDay = new(2026, 1, 5);

    /// <summary>A brokerage holding one fund, bought twice on one day at different
    /// prices, the later higher.</summary>
    private const string TwoBuysOneDayExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"curr","id":"c-fund","type":"s","currid":"^FUND","name":"Fund A","ticker":"FUND","dec":"4","rate":"1","rrate":"1"},
            {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund A","parentid":"a-brk","currid":"c-fund","sbal":"0"},
            {"obj_type":"txn","id":"t-buy-1","acctid":"a-brk","desc":"Buy","dt":"20260105",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-1","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000"},
            {"obj_type":"txn","id":"t-buy-2","acctid":"a-brk","desc":"Buy","dt":"20260105",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-2","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"50000","0.pamt":"-6000"}
          ]
        }
        """;

    /// <summary>The two purchases of one day, then a third a week later at a price of
    /// its own.</summary>
    private const string TwoBuysOneDayThenOneMoreExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"curr","id":"c-fund","type":"s","currid":"^FUND","name":"Fund A","ticker":"FUND","dec":"4","rate":"1","rrate":"1"},
            {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund A","parentid":"a-brk","currid":"c-fund","sbal":"0"},
            {"obj_type":"txn","id":"t-buy-1","acctid":"a-brk","desc":"Buy","dt":"20260105",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-1","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000"},
            {"obj_type":"txn","id":"t-buy-2","acctid":"a-brk","desc":"Buy","dt":"20260105",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-2","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"50000","0.pamt":"-6000"},
            {"obj_type":"txn","id":"t-buy-3","acctid":"a-brk","desc":"Buy","dt":"20260112",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-3","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"30000","0.pamt":"-3300"}
          ]
        }
        """;

    /// <summary>The same brokerage with the first purchase only.</summary>
    private const string OneBuyExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"curr","id":"c-fund","type":"s","currid":"^FUND","name":"Fund A","ticker":"FUND","dec":"4","rate":"1","rrate":"1"},
            {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund A","parentid":"a-brk","currid":"c-fund","sbal":"0"},
            {"obj_type":"txn","id":"t-buy-1","acctid":"a-brk","desc":"Buy","dt":"20260105",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-1","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000"}
          ]
        }
        """;

    /// <summary>A purchase, then shares moved in a week later (<c>ShrsIn</c>) against a
    /// transfer account, with an amount on the movement.</summary>
    private const string ShareMovementExport = """
        {
          "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
          "all_items": [
            {"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"},{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
            {"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"acct","id":"a-xfer","type":"i","name":"Transfers","parentid":"a-root","currid":"c-usd","sbal":"0"},
            {"obj_type":"curr","id":"c-fund","type":"s","currid":"^FUND","name":"Fund A","ticker":"FUND","dec":"4","rate":"1","rrate":"1"},
            {"obj_type":"acct","id":"a-sec","type":"s","name":"Fund A","parentid":"a-brk","currid":"c-fund","sbal":"0"},
            {"obj_type":"txn","id":"t-buy-1","acctid":"a-brk","desc":"Buy","dt":"20260105",
             "invest.txntype":"buy","xfer_type":"xfrtp_buysell",
             "0.id":"s-1","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"100000","0.pamt":"-10000"},
            {"obj_type":"txn","id":"t-in","acctid":"a-brk","desc":"ShrsIn","dt":"20260112",
             "qif_invst_action":"ShrsIn","xfer_type":"xfrtp_buysellxfr",
             "0.id":"s-3","0.acctid":"a-sec","0.invest.splittype":"sec","0.samt":"50000","0.pamt":"-500",
             "1.id":"s-4","1.acctid":"a-xfer","1.invest.splittype":"xfr","1.samt":"-500","1.pamt":"500"}
          ]
        }
        """;

    // ---- hiding and restoring ------------------------------------------------------

    [Fact]
    public async Task Deleting_the_later_imported_trade_prices_its_day_from_the_trade_that_remains()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var day = await ImportTwoBuysAsync(client);

        await DeleteAsync(client, day.LedgerId, day.Later);

        Assert.Equal(day.EarlierPrice, await TradePriceAsync(day.LedgerId, day.SecurityId, ImportedDay));
        await AssertHealthyAsync(client, day.LedgerId, "after the delete");
    }

    [Fact]
    public async Task Restoring_the_deleted_trade_prices_its_day_from_it_again()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var day = await ImportTwoBuysAsync(client);
        await DeleteAsync(client, day.LedgerId, day.Later);
        Assert.Equal(day.EarlierPrice, await TradePriceAsync(day.LedgerId, day.SecurityId, ImportedDay));

        // A restore of one row is a bulk unhide of a selection of one.
        var unhide = await client.PostAsJsonAsync($"/api/ledgers/{day.LedgerId}/transactions/bulk-unhide",
            new BulkUnhideRequest
            {
                Selection = new SelectionRequest
                {
                    Kind = "explicit", AccountId = await BrokerageOfAsync(day.LedgerId), HeaderIds = [day.Later],
                },
            });
        Assert.Equal(HttpStatusCode.OK, unhide.StatusCode);
        Assert.Equal(1, (await unhide.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("unhidden").GetInt32());

        Assert.Equal(day.LaterPrice, await TradePriceAsync(day.LedgerId, day.SecurityId, ImportedDay));
        await AssertHealthyAsync(client, day.LedgerId, "after the restore");
    }

    /// <summary>
    /// The day's only trade hidden: nothing remains to derive the day from, so its price is
    /// kept, a past execution being a real observation (ADR-0084 D4). The check reports
    /// nothing; the advisory lists the security and the price list marks the row.
    /// </summary>
    [Fact]
    public async Task A_days_only_trade_hidden_keeps_its_price_as_one_no_trade_backs()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var ledgerId = await HiddenInvestmentRowHoldingsTests.ImportLedgerAsync(client, OneBuyExport);
        var buy = await HeaderOfAsync(ledgerId, "t-buy-1");
        var securityId = await SecurityOfAsync(ledgerId);
        var report = await AssertHealthyAsync(client, ledgerId, "after the import");
        Assert.DoesNotContain(report.UnbackedPrices, u => u.SecurityId == securityId);

        await DeleteAsync(client, ledgerId, buy);

        var row = Assert.Single(await PricesAsync(ledgerId, securityId));
        Assert.Equal((ImportedDay, 10m, PriceSource.Trade), (row.PriceDate, row.Price, row.Source));
        report = await AssertHealthyAsync(client, ledgerId, "after the delete");
        Assert.Contains(report.UnbackedPrices, u => u.SecurityId == securityId && u.Count == 1);
        var page = (await client.GetFromJsonAsync<SecurityPricesPage>(
            $"/api/ledgers/{ledgerId}/securities/{securityId}/prices"))!;
        var listed = Assert.Single(page.Items);
        Assert.True(listed.Unbacked, "the price list does not mark the hidden trade's price as one no trade backs");
    }

    [Fact]
    public async Task A_bulk_delete_and_a_bulk_restore_each_re_derive_the_day()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var day = await ImportTwoBuysAsync(client);
        var brokerage = await BrokerageOfAsync(day.LedgerId);

        var delete = await client.PostAsJsonAsync(
            $"/api/ledgers/{day.LedgerId}/transactions/bulk-delete",
            new BulkDeleteRequest
            {
                Selection = new SelectionRequest { Kind = "explicit", AccountId = brokerage, HeaderIds = [day.Later] },
            });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(1, (await delete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("softHidden").GetInt32());
        Assert.Equal(day.EarlierPrice, await TradePriceAsync(day.LedgerId, day.SecurityId, ImportedDay));
        await AssertHealthyAsync(client, day.LedgerId, "after the bulk delete");

        var restore = await client.PostAsJsonAsync(
            $"/api/ledgers/{day.LedgerId}/transactions/bulk-unhide",
            new BulkUnhideRequest
            {
                Selection = new SelectionRequest
                {
                    Kind = "explicit", AccountId = brokerage, StatusFilter = "hidden", HeaderIds = [day.Later],
                },
            });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal(1, (await restore.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("unhidden").GetInt32());
        Assert.Equal(day.LaterPrice, await TradePriceAsync(day.LedgerId, day.SecurityId, ImportedDay));
        await AssertHealthyAsync(client, day.LedgerId, "after the bulk restore");
    }

    /// <summary>
    /// A delete of a trade sent while a select-all restore of the Hidden view runs, after the
    /// restore has read its selection and before its update: the delete waits for the
    /// restore, then applies. The restore restores the row it read; the deleted trade ends
    /// hidden, and its day keeps the price of the trade that remains on it.
    /// </summary>
    /// <remarks>
    /// Both take the ledger write lock (migration 275) as their first statement, so the
    /// delete waits there, an <c>advisory</c> wait, holding nothing, until the restore
    /// commits; then it hides the row and re-derives its day.
    /// </remarks>
    [Fact]
    public async Task A_delete_sent_during_a_bulk_restore_of_the_hidden_view_waits_for_it_then_applies()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var ledgerId = await HiddenInvestmentRowHoldingsTests.ImportLedgerAsync(client, TwoBuysOneDayThenOneMoreExport);
        var securityId = await SecurityOfAsync(ledgerId);
        var brokerage = await BrokerageOfAsync(ledgerId);
        var weekLater = await HeaderOfAsync(ledgerId, "t-buy-3");

        // The first day's two purchases, in the order the import gave them: the later
        // prices the day.
        List<(Guid Id, decimal Price)> firstDay;
        await using (var db = Db.NewDbContext())
        {
            firstDay = (await (from h in db.TxnHeaders.AsNoTracking()
                               join l in db.TxnLegs.AsNoTracking() on h.Id equals l.HeaderId
                               where h.LedgerId == ledgerId && l.SecurityId == securityId
                                     && (h.ExternalId == "t-buy-1" || h.ExternalId == "t-buy-2")
                               orderby h.Seq
                               select new { h.Id, l.UnitPrice })
                    .ToListAsync())
                .Select(t => (t.Id, Math.Round(t.UnitPrice!.Value, 4)))
                .ToList();
        }
        Assert.Equal(2, firstDay.Count);
        var (earlier, later) = (firstDay[0], firstDay[1]);
        Assert.Equal(later.Price, await TradePriceAsync(ledgerId, securityId, ImportedDay));

        // The week-later purchase deleted, so the Hidden view lists it alone.
        await DeleteAsync(client, ledgerId, weekLater);
        await AssertHealthyAsync(client, ledgerId, "after the delete");

        // Sent as the restore is about to run its update, and left waiting for the ledger
        // write lock the restore holds, not awaited there.
        Task? deleting = null;
        var waitedAtTheLock = false;
        bool? deleteDoneAtCommit = null;
        var atRestore = new BeforeHeaderUpdate(async () =>
        {
            deleting = DeleteAsync(client, ledgerId, later.Id);
            waitedAtTheLock = await AdvisoryWaitOrDoneAsync(deleting);
        });
        var atCommit = new BeforeCommit(() => deleteDoneAtCommit = deleting?.IsCompleted);
        try
        {
            await using var db = Db.NewServiceFactoryWith(atRestore, atCommit).Create();
            var bulk = new BulkTransactionsRepository(
                db, new LegDerivedRecomputeService(db), new HoldingsRecomputeService(db), TimeProvider.System);
            var restored = await bulk.BulkUnhideAsync(ledgerId, new SelectionRequest
            {
                Kind = "all",
                AccountId = brokerage,
                StatusFilter = "hidden",
                SelectedAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            Assert.Equal(1, restored);
        }
        finally
        {
            if (deleting is not null) await deleting;
        }
        Assert.True(atRestore.Fired, "the delete was never sent before the restore's update, so nothing was tested");
        Assert.True(waitedAtTheLock, "the delete did not wait at the ledger write lock the restore held");
        Assert.True(deleteDoneAtCommit == false, "the delete finished before the restore committed");

        await using (var db = Db.NewDbContext())
        {
            Assert.False((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == weekLater)).IsHidden);
            Assert.True((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == later.Id)).IsHidden,
                "the delete sent during the restore did not hide its row");
        }
        Assert.Equal(earlier.Price, await TradePriceAsync(ledgerId, securityId, ImportedDay));
        await AssertHealthyAsync(client, ledgerId, "after the restore and the delete");
    }

    /// <summary>Whether a session of this test's database waits at an advisory lock before
    /// <paramref name="running"/> is done. Asked as the superuser: the writes run as the app
    /// role, whose sessions' wait events another role cannot read.</summary>
    private async Task<bool> AdvisoryWaitOrDoneAsync(Task running)
    {
        await using var probe = new Npgsql.NpgsqlConnection(Db.SuperuserConnectionString);
        await probe.OpenAsync();
        for (var i = 0; i < 400 && !running.IsCompleted; i++)
        {
            await using var cmd = new Npgsql.NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                 WHERE datname = current_database() AND wait_event_type = 'Lock' AND wait_event = 'advisory'
                """, probe);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return true;
            await Task.Delay(25);
        }
        return false;
    }

    /// <summary>Runs <c>action</c> once, just before the first transaction of the context it
    /// is added to commits.</summary>
    private sealed class BeforeCommit(Action action) : DbTransactionInterceptor
    {
        private int _fired;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0) action();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Runs <paramref name="action"/> once, as the first update of txn_headers is
    /// about to run on the context it is added to.</summary>
    private sealed class BeforeHeaderUpdate(Func<Task> action) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("UPDATE txn_headers", StringComparison.Ordinal))
            {
                Fired = true;
                await action();
            }
            return result;
        }
    }

    /// <summary>
    /// An install upgraded from a version that hid a trade without re-deriving its day: the
    /// later trade was deleted then, so the day still holds its price. The check reads the
    /// rule, reports the day once, and the trade-price repair writes the remaining trade's.
    /// </summary>
    /// <remarks>
    /// The earlier version's save is reproduced through EF with every save interceptor but
    /// the trade-price one, which in that version ran on the delete and read no hidden flag,
    /// so it moved nothing: the balances and the position are recomputed as that version
    /// recomputed them, and only the day's price is left as it was.
    /// </remarks>
    [Fact]
    public async Task A_day_an_earlier_version_left_priced_by_a_hidden_trade_is_reported_and_repaired()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var day = await ImportTwoBuysAsync(client);

        await using (var earlier = EarlierVersionContext())
        {
            var header = await earlier.TxnHeaders.SingleAsync(h => h.Id == day.Later);
            header.IsHidden = true;
            header.NeedsReview = false;
            await earlier.SaveChangesUnderLedgerLockAsync(day.LedgerId);
        }
        Assert.Equal(day.LaterPrice, await TradePriceAsync(day.LedgerId, day.SecurityId, ImportedDay));

        var report = await ReportAsync(client, day.LedgerId);
        Assert.All(report.Projections.Where(p => p.Projection != ConsistencyProjections.TradePrices),
            p => Assert.True(p.Healthy, $"{p.Projection} is unhealthy after the earlier version's delete"));
        var trade = Assert.Single(report.Projections, p => p.Projection == ConsistencyProjections.TradePrices);
        var finding = Assert.Single(trade.Mismatches);
        Assert.Equal(
            (ConsistencyReasons.ValueDiffers, (Guid?)day.SecurityId, (DateOnly?)ImportedDay, day.LaterPrice, day.EarlierPrice),
            (finding.Reason, finding.SecurityId, finding.PriceDate, finding.Stored, finding.Expected));

        var repair = await client.PostAsync(
            $"/api/ledgers/{day.LedgerId}/balances/consistency/{ConsistencyProjections.TradePrices}/repair", null);
        Assert.Equal(HttpStatusCode.OK, repair.StatusCode);

        Assert.Equal(day.EarlierPrice, await TradePriceAsync(day.LedgerId, day.SecurityId, ImportedDay));
        await AssertHealthyAsync(client, day.LedgerId, "after the repair");
    }

    // ---- merging and un-merging ----------------------------------------------------

    /// <summary>
    /// A statement row turned into a purchase on the day of a purchase already entered, at
    /// the same instant, then folded into it: the day takes the winner's price.
    /// </summary>
    [Fact]
    public async Task A_merge_folding_a_trade_into_another_on_its_day_prices_the_day_from_the_winner()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var merged = await MergeATradeIntoAnotherOnItsDayAsync(client, ledger);

        Assert.Equal(10m, await TradePriceAsync(ledger.LedgerId, merged.SecurityId, merged.Day));
        await AssertHealthyAsync(client, ledger.LedgerId, "after the merge");
    }

    /// <summary>
    /// The merge above, then the winner moved to another day: the day it left holds only the
    /// merged-away trade, which prices nothing, so it keeps the winner's price as one no
    /// trade backs (ADR-0084 D4). The check reports nothing; the advisory lists the security
    /// and the price list marks the row.
    /// </summary>
    [Fact]
    public async Task A_day_left_with_only_a_merged_away_trade_keeps_its_price_as_one_no_trade_backs()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var merged = await MergeATradeIntoAnotherOnItsDayAsync(client, ledger);

        var moved = merged.At.AddDays(7);
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{merged.Winner}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = merged.BrokerageId,
                PostedAt = moved,
                Action = "buy",
                SecurityId = merged.SecurityId,
                Shares = 10m,
                Price = 10m,
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: {await patch.Content.ReadAsStringAsync()}");

        Assert.Equal(10m, await TradePriceAsync(ledger.LedgerId, merged.SecurityId, merged.Day));
        Assert.Equal(10m, await TradePriceAsync(ledger.LedgerId, merged.SecurityId, DateOnly.FromDateTime(moved)));
        var report = await AssertHealthyAsync(client, ledger.LedgerId, "after the winner moved");
        Assert.Contains(report.UnbackedPrices, u => u.SecurityId == merged.SecurityId && u.Count == 1);
        var page = (await client.GetFromJsonAsync<SecurityPricesPage>(
            $"/api/ledgers/{ledger.LedgerId}/securities/{merged.SecurityId}/prices"))!;
        Assert.True(Assert.Single(page.Items, i => i.AsOf == merged.Day).Unbacked,
            "the price list does not mark the price of a day only a merged-away trade sits on");
        Assert.False(Assert.Single(page.Items, i => i.AsOf == DateOnly.FromDateTime(moved)).Unbacked);
    }

    private sealed record MergedDay(Guid BrokerageId, Guid SecurityId, Guid Winner, DateTime At, DateOnly Day);

    /// <summary>
    /// A purchase entered by hand, then a statement row of its day turned into a later
    /// purchase at the same instant, which then prices the day, folded into it. The merge
    /// moves no date, so only the loser's merge stamp says the day changed.
    /// </summary>
    private async Task<MergedDay> MergeATradeIntoAnotherOnItsDayAsync(HttpClient client, SyntheticLedger ledger)
    {
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        // The statement's date, at midnight UTC as a date is stored.
        var at = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);
        var winner = await BuyAsync(client, ledger.LedgerId, brokerage.Id, securityId, at, shares: 10m, price: 10m);
        var loser = await ImportStatementRowAsync(client, ledger.LedgerId, brokerage.Id);
        await PatchIntoBuyAsync(client, ledger.LedgerId, brokerage.Id, loser, securityId, at, shares: 5m, price: 12m);

        // Premises: the edited row still awaits review, is the day's later trade, and prices it.
        await using (var db = Db.NewDbContext())
            Assert.True((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).NeedsReview);
        var day = DateOnly.FromDateTime(at);
        Assert.Equal(12m, await TradePriceAsync(ledger.LedgerId, securityId, day));
        await AssertHealthyAsync(client, ledger.LedgerId, "before the merge");

        await MergeAsync(client, ledger.LedgerId, loser, winner);

        // Premise: the merge left the winner's date as it was.
        await using (var db = Db.NewDbContext())
            Assert.Equal(at, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == winner)).PostedAt.ToUniversalTime());
        return new MergedDay(brokerage.Id, securityId, winner, at, day);
    }

    /// <summary>
    /// A row turned into a purchase of a later price, then merged, prices nothing while
    /// merged: its day, which the merge gives the winner, is priced by the winner alone.
    /// Deleting the winner un-merges it, and its day is then priced by it.
    /// The single delete un-merges it in the change tracker (<c>MergeLosers</c>), the bulk
    /// delete reads it itself.
    /// </summary>
    [Theory]
    [InlineData("single")]
    [InlineData("bulk")]
    public async Task Deleting_a_merge_winner_prices_the_day_of_the_trade_it_un_merges(string delete)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger.LedgerId, brokerage.Id, securityId,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc), shares: 10m, price: 10m);
        var loser = await ImportStatementRowAsync(client, ledger.LedgerId, brokerage.Id);
        // Edited into a purchase of a day of its own while it still awaits review: a merged
        // row takes no edit (MergedAway). The merge moves the winner to that day.
        var own = new DateTime(2026, 1, 26, 12, 0, 0, DateTimeKind.Utc);
        await PatchIntoBuyAsync(client, ledger.LedgerId, brokerage.Id, loser, securityId, own, shares: 5m, price: 12m);
        await MergeAsync(client, ledger.LedgerId, loser, winner);

        // Premises: still merged, so its day is priced by the winner alone, and the ledger
        // is clean.
        await using (var db = Db.NewDbContext())
        {
            Assert.Equal(winner, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);
            Assert.Equal(own, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == winner)).PostedAt.ToUniversalTime());
        }
        var day = DateOnly.FromDateTime(own);
        Assert.Equal(10m, await TradePriceAsync(ledger.LedgerId, securityId, day));
        await AssertHealthyAsync(client, ledger.LedgerId, "with the merged row re-entered");

        if (delete == "single")
        {
            var response = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions/{winner}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("hard-deleted", await response.Content.ReadAsStringAsync());
        }
        else
        {
            var response = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-delete",
                new BulkDeleteRequest
                {
                    Selection = new SelectionRequest { Kind = "explicit", AccountId = brokerage.Id, HeaderIds = [winner] },
                });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hardDeleted").GetInt32());
        }

        await using (var db = Db.NewDbContext())
            Assert.Null((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);
        Assert.Equal(12m, await TradePriceAsync(ledger.LedgerId, securityId, day));
        await AssertHealthyAsync(client, ledger.LedgerId, "after the winner was deleted");
    }

    // ---- the import ----------------------------------------------------------------

    /// <summary>
    /// A share movement is no execution (ADR-0084 D6), and the import's own seed of trade
    /// prices reads the rule as every writer does: the purchase prices its day, the
    /// movement prices none.
    /// </summary>
    [Fact]
    public async Task A_moneydance_share_movement_prices_no_day()
    {
        await using var factory = new ApiFactory(Db);
        using var client = factory.CreateClient();
        var ledgerId = await HiddenInvestmentRowHoldingsTests.ImportLedgerAsync(client, ShareMovementExport);
        var securityId = await SecurityOfAsync(ledgerId);

        // Premise: the movement came in, as the share movement it is, and holds shares.
        await using (var db = Db.NewDbContext())
        {
            var movement = await db.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.LedgerId == ledgerId && h.ExternalId == "t-in");
            Assert.Equal("buyx", movement.Action);
            Assert.Contains(await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == movement.Id).ToListAsync(),
                l => l.SecurityId == securityId && l.Quantity == 5m && l.UnitPrice > 0m);
        }

        var row = Assert.Single(await PricesAsync(ledgerId, securityId));
        Assert.Equal((ImportedDay, 10m, PriceSource.Trade), (row.PriceDate, row.Price, row.Source));
        var report = await AssertHealthyAsync(client, ledgerId, "after the import");
        Assert.DoesNotContain(report.UnbackedPrices, u => u.SecurityId == securityId);
    }

    // ---- the states ----------------------------------------------------------------

    private sealed record TradeDay(
        Guid LedgerId, Guid SecurityId, Guid Earlier, decimal EarlierPrice, Guid Later, decimal LaterPrice);

    /// <summary>Import the two purchases and return them in the order the import gave
    /// them, each with its price; the day is priced by the later and the ledger checks
    /// clean.</summary>
    private async Task<TradeDay> ImportTwoBuysAsync(HttpClient client)
    {
        var ledgerId = await HiddenInvestmentRowHoldingsTests.ImportLedgerAsync(client, TwoBuysOneDayExport);
        var securityId = await SecurityOfAsync(ledgerId);
        List<(Guid Id, long Seq, decimal Price)> trades;
        await using (var db = Db.NewDbContext())
        {
            trades = (await (from h in db.TxnHeaders.AsNoTracking()
                             join l in db.TxnLegs.AsNoTracking() on h.Id equals l.HeaderId
                             where h.LedgerId == ledgerId && l.SecurityId == securityId
                             orderby h.Seq
                             select new { h.Id, h.Seq, l.UnitPrice })
                    .ToListAsync())
                .Select(t => (t.Id, t.Seq, Math.Round(t.UnitPrice!.Value, 4)))
                .ToList();
            // An export id is what sends a delete to the soft-hide branch.
            Assert.All(await db.TxnHeaders.AsNoTracking().Where(h => h.LedgerId == ledgerId).ToListAsync(),
                h => Assert.NotNull(h.ExternalId));
        }
        Assert.Equal(2, trades.Count);
        Assert.Equal(new[] { 10m, 12m }, trades.Select(t => t.Price).Order());
        var day = new TradeDay(ledgerId, securityId, trades[0].Id, trades[0].Price, trades[1].Id, trades[1].Price);

        Assert.Equal(day.LaterPrice, await TradePriceAsync(ledgerId, securityId, ImportedDay));
        await AssertHealthyAsync(client, ledgerId, "after the import");
        return day;
    }

    /// <summary>
    /// A context that saves as an earlier version did: the save interceptors a request's
    /// context carries, less <see cref="TradePriceFromLegInterceptor"/>, so the balances, the
    /// positions and the stored notes are recomputed and no trade price is re-derived.
    /// </summary>
    private AppDbContext EarlierVersionContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString)
            .AddInterceptors(
                new LegDerivedRecomputeInterceptor(NullLogger<LegDerivedRecomputeInterceptor>.Instance),
                new HoldingsRecomputeInterceptor(),
                new LegNotesRecomputeInterceptor())
            .Options);

    // ---- writes --------------------------------------------------------------------

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static async Task DeleteAsync(HttpClient client, Guid ledgerId, Guid headerId)
    {
        var delete = await client.DeleteAsync($"/api/ledgers/{ledgerId}/investment-transactions/{headerId}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using var body = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        Assert.Equal("soft-hidden", body.RootElement.GetProperty("kind").GetString());
    }

    private static async Task<Guid> BuyAsync(
        HttpClient client, Guid ledgerId, Guid brokerageId, Guid securityId, DateTime postedAt,
        decimal shares, decimal price)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledgerId}/investment-transactions",
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
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    /// <summary>Turn the row into a purchase of <paramref name="securityId"/> through the
    /// investment edit, which leaves whether it awaits review alone.</summary>
    private static async Task PatchIntoBuyAsync(
        HttpClient client, Guid ledgerId, Guid brokerageId, Guid headerId, Guid securityId,
        DateTime postedAt, decimal shares, decimal price)
    {
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerageId,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = shares,
                Price = price,
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: {await patch.Content.ReadAsStringAsync()}");
    }

    private async Task MergeAsync(HttpClient client, Guid ledgerId, Guid loser, Guid winner)
    {
        var merge = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledgerId}/investment-transactions/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.True(merge.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"merge failed: {(int)merge.StatusCode} {await merge.Content.ReadAsStringAsync()}");
        await using var db = Db.NewDbContext();
        Assert.Equal(winner, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);
    }

    /// <summary>Import a one-row brokerage statement (a cash credit, <c>INVBANKTRAN</c>) and
    /// return the row it writes: awaiting review, with no security.</summary>
    private async Task<Guid> ImportStatementRowAsync(HttpClient client, Guid ledgerId, Guid brokerageId)
    {
        using var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(OfxCashStatement));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(body, "file", "statement.qfx");
        content.Add(new StringContent(brokerageId.ToString()), "accountId");
        content.Add(new StringContent("inv:brokerX:INV-0001"), "providerAccountId");
        var resp = await client.PostAsync($"/api/ledgers/{ledgerId}/ingest/ofx/import", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        var row = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledgerId && h.ExternalId == "INV-FITID-TRADEDAY");
        Assert.True(row.NeedsReview);
        Assert.Null(row.Action);
        return row.Id;
    }

    private const string OfxCashStatement = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE

        <OFX>
        <SIGNONMSGSRSV1>
        <SONRS>
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <DTSERVER>20260201120000
        <LANGUAGE>ENG
        </SONRS>
        </SIGNONMSGSRSV1>
        <INVSTMTMSGSRSV1>
        <INVSTMTTRNRS>
        <TRNUID>0
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <INVSTMTRS>
        <DTASOF>20260131120000
        <CURDEF>USD
        <INVACCTFROM>
        <BROKERID>brokerX
        <ACCTID>INV-0001
        </INVACCTFROM>
        <INVTRANLIST>
        <DTSTART>20260101
        <DTEND>20260131
        <INVBANKTRAN>
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20260120
        <TRNAMT>-60.00
        <FITID>INV-FITID-TRADEDAY
        <NAME>PURCHASE
        </STMTTRN>
        <SUBACCTFUND>CASH
        </INVBANKTRAN>
        </INVTRANLIST>
        </INVSTMTRS>
        </INVSTMTTRNRS>
        </INVSTMTMSGSRSV1>
        </OFX>
        """;

    // ---- reads ---------------------------------------------------------------------

    private async Task<Guid> HeaderOfAsync(Guid ledgerId, string externalId)
    {
        await using var db = Db.NewDbContext();
        return (await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledgerId && h.ExternalId == externalId)).Id;
    }

    private async Task<Guid> SecurityOfAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return (await db.Securities.AsNoTracking().SingleAsync(s => s.LedgerId == ledgerId && s.Ticker == "FUND")).Id;
    }

    private async Task<Guid> BrokerageOfAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return (await db.Accounts.AsNoTracking()
            .SingleAsync(a => a.LedgerId == ledgerId && a.HoldingsAccountId != null)).Id;
    }

    private async Task<List<SecurityPriceRow>> PricesAsync(Guid ledgerId, Guid securityId)
    {
        await using var db = Db.NewDbContext();
        return await db.SecurityPrices.AsNoTracking()
            .Where(p => p.LedgerId == ledgerId && p.SecurityId == securityId)
            .ToListAsync();
    }

    /// <summary>The day's stored trade-source price, or null when it has none.</summary>
    private async Task<decimal?> TradePriceAsync(Guid ledgerId, Guid securityId, DateOnly day)
    {
        var rows = (await PricesAsync(ledgerId, securityId)).Where(p => p.PriceDate == day).ToList();
        if (rows.Count == 0) return null;
        var row = Assert.Single(rows);
        Assert.Equal(PriceSource.Trade, row.Source);
        return row.Price;
    }

    private static async Task<LedgerConsistencyReport> ReportAsync(HttpClient client, Guid ledgerId) =>
        (await (await client.PostAsync($"/api/ledgers/{ledgerId}/balances/consistency", null)).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<LedgerConsistencyReport>())!;

    /// <summary>The consistency check, through its endpoint, reports every projection
    /// healthy; otherwise the failure names each disagreeing figure. Returns the report.</summary>
    private static async Task<LedgerConsistencyReport> AssertHealthyAsync(HttpClient client, Guid ledgerId, string when)
    {
        var report = await ReportAsync(client, ledgerId);
        var failing = report.Projections
            .Where(p => !p.Healthy)
            .Select(p => p.Projection + ": " + string.Join("; ", p.Mismatches.Select(m =>
                $"{m.Scope} {m.Field} stored {m.Stored} expected {m.Expected}")))
            .ToList();
        Assert.True(report.Healthy,
            $"{when}, the consistency check is unhealthy: "
            + (failing.Count > 0 ? string.Join(" | ", failing) : "oversold positions listed"));
        return report;
    }
}
