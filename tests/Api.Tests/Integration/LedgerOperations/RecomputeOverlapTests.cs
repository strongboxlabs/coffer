using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// A save's recompute and a repair, or two saves, of one ledger at once: the later one
/// waits at the ledger write lock for the earlier to commit, then recomputes from what it
/// committed, so both succeed and the stored figures agree with a fresh walk.
/// </summary>
/// <remarks>
/// <para>The balances recompute (<c>fn_recompute_balances_for_account</c>) deletes an
/// account's rows from its anchor on and inserts them again; the holdings recompute
/// (<c>recompute_holdings_cost_basis</c>) deletes a position's realized gains and inserts
/// them again. Two runs over one account or position at once collide: a run that meets
/// rows an earlier uncommitted run deleted waits at its delete and then inserts over the
/// rows that run committed, a unique violation; and a balances run whose anchor is past
/// every committed row waits for nothing and stores a running balance that leaves out the
/// uncommitted run's transaction. So every writer that can recompute takes its ledger's
/// write lock as its first statement (migration 275, ADR-0112), the balances and holdings
/// repairs take it one statement per account or position, and the two functions take it
/// themselves before they read: two runs of one ledger never get past it together.</para>
/// <para>A transaction that recomputes several accounts or positions holds the lock from
/// its first, so another waits at the lock, having written and row-locked nothing, for the
/// whole of it, whatever order either names them in; no wait between the two closes a
/// cycle. Each test asks where the later one waited (<see cref="WaitEventsAsync"/>): at the
/// lock, an <c>advisory</c> wait. Every write is awaited through
/// <see cref="NoDeadlockAsync{T}"/>, so a deadlock fails the test by name.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class RecomputeOverlapTests : DatabaseTest
{
    private static readonly DateTime FirstDay = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondDay = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ThirdDay = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FourthDay = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    public RecomputeOverlapTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private sealed record World(SyntheticLedger Ledger, ApiFactory Factory, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private async Task<World> WorldAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        return new World(ledger, factory, client);
    }

    private static Task<HttpResponseMessage> RepairAsync(World w, string projection) =>
        w.Client.PostAsync($"/api/ledgers/{w.Ledger.LedgerId}/balances/consistency/{projection}/repair", content: null);

    private static Task<HttpResponseMessage> SpendAsync(World w, Guid bank, Guid category, DateTime day, decimal amount, string payee) =>
        w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/transactions", new CreateTransactionRequest
        {
            PostedAt = day,
            Payee = payee,
            SourceAccountId = bank,
            Postings = [new TransactionPosting { CounterpartyAccountId = category, Amount = amount }],
        });

    /// <summary>A spend from <paramref name="bank"/> saved in <paramref name="db"/>'s
    /// transaction, which the caller holds open.</summary>
    private static async Task SpendInAsync(
        AppDbContext db, World w, Guid bank, Guid category, DateTime day, decimal amount, string payee)
    {
        var created = await new TransactionsRepository(db, TimeProvider.System).CreateAsync(
            w.Ledger.LedgerId, bank, day, payee, checkNumber: null, transactedAt: null,
            [new TransactionPosting { CounterpartyAccountId = category, Amount = amount }], notes: null);
        Assert.Null(created.Refusal);
    }

    private static async Task<Guid> BrokerageAsync(World w, string name)
    {
        var made = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = name, AccountType = "investment", CurrencyCode = "USD" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static CreateInvestmentTransactionRequest Trade(
        Guid brokerage, Guid securityId, string action, decimal shares, decimal price, DateTime day) => new()
    {
        BrokerageAccountId = brokerage,
        PostedAt = day,
        Action = action,
        SecurityId = securityId,
        Shares = shares,
        Price = price,
    };

    private static Task<HttpResponseMessage> TradeAsync(
        World w, Guid brokerage, Guid securityId, string action, decimal shares, decimal price, DateTime day) =>
        w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions",
            Trade(brokerage, securityId, action, shares, price, day));

    private static async Task TradedAsync(
        World w, Guid brokerage, Guid securityId, string action, decimal shares, decimal price, DateTime day)
    {
        using var traded = await TradeAsync(w, brokerage, securityId, action, shares, price, day);
        Assert.True(traded.StatusCode == HttpStatusCode.Created, await traded.Content.ReadAsStringAsync());
    }

    private async Task<Guid> HoldingsAccountOfAsync(Guid brokerage)
    {
        await using var db = Db.NewDbContext();
        return (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == brokerage)).HoldingsAccountId!.Value;
    }

    /// <summary>The answer, and whether the transaction it saved on <paramref name="day"/>
    /// (the only one on that day) is stored, for a failure message.</summary>
    private async Task<string> DescribeAsync(World w, HttpResponseMessage response, DateTime day)
    {
        await using var db = Db.NewDbContext();
        var stored = await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.LedgerId == w.Ledger.LedgerId && h.PostedAt == day);
        return $"answered {(int)response.StatusCode} with {stored} transaction(s) stored on its day: "
               + await response.Content.ReadAsStringAsync();
    }

    /// <summary>Every projection healthy: what is stored is what a fresh walk derives.</summary>
    private async Task AssertHealthyAsync(World w)
    {
        await using var db = Db.NewDbContext();
        var report = await new LedgerConsistencyRepository(
                db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db), TimeProvider.System)
            .CheckAsync(w.Ledger.LedgerId);
        Assert.True(report.Healthy, "findings after both writes: " + string.Join(" | ",
            report.Projections.Where(p => !p.Healthy).Select(p => p.Projection + ": " + string.Join("; ",
                p.Mismatches.Select(m => $"{m.Scope}: {m.Field} stored {m.Stored}, expected {m.Expected}")))));
    }

    /// <summary>Block until the monitor's start pass has recorded a verdict for the ledger,
    /// so the pass is not running while a test counts the sessions waiting on a lock.</summary>
    private async Task WaitForStartPassAsync(Guid ledgerId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using (var db = Db.NewDbContext())
            {
                if (await db.LedgerEvents.AsNoTracking()
                        .AnyAsync(e => e.LedgerId == ledgerId && ConsistencyVerdicts.VerdictKeys.Contains(e.EventKey)))
                    return;
            }
            await Task.Delay(100);
        }
        Assert.Fail("The monitor's start pass never recorded a verdict for the ledger.");
    }

    /// <summary>Wait until at least <paramref name="sessions"/> sessions of this test's
    /// database wait on a lock (only <paramref name="pid"/>'s when given), or
    /// <paramref name="running"/> is done. Asked as the superuser: the writes run as the app
    /// role, whose sessions' wait events another role cannot read.</summary>
    private async Task WaitForLockWaitersOrDoneAsync(int sessions, Task running, int? pid = null)
    {
        await using var probe = new NpgsqlConnection(Db.SuperuserConnectionString);
        await probe.OpenAsync();
        for (var i = 0; i < 400 && !running.IsCompleted; i++)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                 WHERE datname = current_database() AND wait_event_type = 'Lock'
                   AND (@pid::int IS NULL OR pid = @pid::int)
                """, probe);
            cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)pid ?? DBNull.Value });
            if ((long)(await cmd.ExecuteScalarAsync())! >= sessions) return;
            await Task.Delay(25);
        }
    }

    /// <summary>What the sessions of this test's database that wait on a lock
    /// (<paramref name="pid"/>'s when given) wait on: <c>advisory</c> at the ledger write
    /// lock, <c>transactionid</c> or <c>tuple</c> at a row. Asked as the superuser, as
    /// above.</summary>
    private async Task<List<string>> WaitEventsAsync(int? pid = null)
    {
        await using var probe = new NpgsqlConnection(Db.SuperuserConnectionString);
        await probe.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT wait_event FROM pg_stat_activity
             WHERE datname = current_database() AND wait_event_type = 'Lock'
               AND (@pid::int IS NULL OR pid = @pid::int)
            """, probe);
        cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)pid ?? DBNull.Value });
        var events = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) events.Add(reader.GetString(0));
        return events;
    }

    /// <summary>Fail unless a session (<paramref name="pid"/>'s when given) waits at the
    /// ledger write lock, an advisory lock, rather than only at rows.</summary>
    private async Task AssertWaitsAtTheLedgerWriteLockAsync(string what, int? pid = null)
    {
        var events = await WaitEventsAsync(pid);
        Assert.True(events.Contains("advisory", StringComparer.Ordinal),
            $"{what} did not wait at the ledger write lock (waits: {string.Join(", ", events)})");
    }

    private static int Pid(AppDbContext db) => ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;

    /// <summary>Await a write, failing the test by name when Postgres chose it as a deadlock
    /// victim.</summary>
    private static async Task<T> NoDeadlockAsync<T>(Task<T> write, string what)
    {
        try
        {
            return await write;
        }
        catch (Exception e) when (Deadlock(e) is { } deadlock)
        {
            Assert.Fail(Victim(what, deadlock));
            throw;
        }
    }

    private static async Task NoDeadlockAsync(Task write, string what)
    {
        try
        {
            await write;
        }
        catch (Exception e) when (Deadlock(e) is { } deadlock)
        {
            Assert.Fail(Victim(what, deadlock));
            throw;
        }
    }

    private static string Victim(string what, PostgresException deadlock) =>
        $"{what} was chosen as a deadlock victim: {deadlock.MessageText} ({deadlock.Detail})";

    private static PostgresException? Deadlock(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            if (e is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected } p) return p;
        return null;
    }

    /// <summary>Runs <c>action</c> once, just before the <paramref name="nth"/> command whose
    /// text contains <paramref name="marker"/> is sent.</summary>
    private sealed class BeforeNthCommand(string marker, int nth, Func<Task> action) : DbCommandInterceptor
    {
        private int _seen;
        private int _fired;

        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal)
                && Interlocked.Increment(ref _seen) == nth
                && Interlocked.Exchange(ref _fired, 1) == 0)
                await action();
            return result;
        }
    }

    // ----- a save beside a repair, and two saves -----

    /// <summary>
    /// A spend saved while the balances repair waits partway through rewriting the same
    /// account, dated before a transaction whose balance both rewrite: the save waits at the
    /// ledger write lock the repair's statement holds, then recomputes from what the repair
    /// committed. Both succeed.
    /// </summary>
    [Fact]
    public async Task A_save_sent_while_the_balances_repair_rewrites_its_account_waits_for_it()
    {
        await using var w = await WorldAsync();
        var bank = await w.Ledger.AddBankAccountAsync("Checking");
        var category = await w.Ledger.AddCategoryAsync("Groceries");
        await w.Ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, ThirdDay);
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        // The bank account's stored balance rows, held so the repair waits at its delete.
        await using var holder = Db.NewDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.TxnHeaderAccountBalances.Where(b => b.AccountId == bank.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.BalanceAfter, b => b.BalanceAfter));

        var repairing = RepairAsync(w, ConsistencyProjections.Balances);
        await WaitForLockWaitersOrDoneAsync(1, repairing);
        Assert.False(repairing.IsCompleted, "the repair did not wait at the held rows, so nothing was tested");

        var saving = SpendAsync(w, bank.Id, category.Id, FirstDay, -10m, "Payee B");
        await WaitForLockWaitersOrDoneAsync(2, saving);
        Assert.False(saving.IsCompleted, "the save did not wait for the repair in flight");
        await AssertWaitsAtTheLedgerWriteLockAsync("the save");

        await held.RollbackAsync();
        using var repaired = await NoDeadlockAsync(repairing, "the repair");
        using var saved = await NoDeadlockAsync(saving, "the save");

        Assert.True(repaired.StatusCode == HttpStatusCode.OK,
            $"the repair answered {(int)repaired.StatusCode}: {await repaired.Content.ReadAsStringAsync()}");
        Assert.True(saved.StatusCode == HttpStatusCode.Created, "the save " + await DescribeAsync(w, saved, FirstDay));
        await AssertHealthyAsync(w);
    }

    /// <summary>
    /// Two spends from one account, the second sent while the first is saved and not yet
    /// committed, both dated before a transaction whose balance both rewrite: the second
    /// waits for the first at the ledger write lock, then recomputes from what it
    /// committed. Both succeed.
    /// </summary>
    [Fact]
    public async Task A_save_sent_while_another_save_of_the_account_is_in_flight_waits_for_it()
    {
        await using var w = await WorldAsync();
        var bank = await w.Ledger.AddBankAccountAsync("Checking");
        var category = await w.Ledger.AddCategoryAsync("Groceries");
        await w.Ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, ThirdDay);
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        await using var first = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await using var held = await first.Database.BeginTransactionAsync();
        await SpendInAsync(first, w, bank.Id, category.Id, FirstDay, -10m, "Payee A");

        var saving = SpendAsync(w, bank.Id, category.Id, SecondDay, -5m, "Payee B");
        await WaitForLockWaitersOrDoneAsync(1, saving);
        Assert.False(saving.IsCompleted, "the second save did not wait for the first in flight");
        await AssertWaitsAtTheLedgerWriteLockAsync("the second save");

        await held.CommitAsync();
        using var saved = await NoDeadlockAsync(saving, "the second save");

        Assert.True(saved.StatusCode == HttpStatusCode.Created, "the second save " + await DescribeAsync(w, saved, SecondDay));
        await AssertHealthyAsync(w);
    }

    /// <summary>
    /// Two spends from one account, the second sent while the first is saved and not yet
    /// committed, the second dated after every committed transaction: it has no stored row
    /// to wait for, but it waits for the first at the ledger write lock all the same, and
    /// its running balance counts the first spend.
    /// </summary>
    [Fact]
    public async Task A_later_save_sent_while_an_earlier_one_is_in_flight_counts_it_in_its_balance()
    {
        await using var w = await WorldAsync();
        var bank = await w.Ledger.AddBankAccountAsync("Checking");
        var category = await w.Ledger.AddCategoryAsync("Groceries");
        await w.Ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, FirstDay);
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        await using var first = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await using var held = await first.Database.BeginTransactionAsync();
        await SpendInAsync(first, w, bank.Id, category.Id, SecondDay, -10m, "Payee A");

        var saving = SpendAsync(w, bank.Id, category.Id, ThirdDay, -5m, "Payee B");
        await WaitForLockWaitersOrDoneAsync(1, saving);
        var waited = !saving.IsCompleted;
        var waits = await WaitEventsAsync();

        await held.CommitAsync();
        using var saved = await NoDeadlockAsync(saving, "the later save");

        Assert.True(saved.StatusCode == HttpStatusCode.Created, "the later save " + await DescribeAsync(w, saved, ThirdDay));
        await AssertHealthyAsync(w);
        Assert.True(waited, "the later save did not wait for the earlier one in flight");
        Assert.True(waits.Contains("advisory", StringComparer.Ordinal),
            $"the later save did not wait at the ledger write lock (waits: {string.Join(", ", waits)})");
    }

    /// <summary>
    /// A sale saved while the realized-gains repair waits partway through rewriting the same
    /// position, which holds an earlier sale: the sale waits at the ledger write lock the
    /// repair's statement holds, then recomputes the position from what the repair
    /// committed. Both succeed.
    /// </summary>
    [Fact]
    public async Task A_sale_sent_while_the_realized_gains_repair_rewrites_its_position_waits_for_it()
    {
        await using var w = await WorldAsync();
        var securityId = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var brokerage = await BrokerageAsync(w, "Brokerage R");
        await TradedAsync(w, brokerage, securityId, "buy", 10m, 10m, FirstDay);
        await TradedAsync(w, brokerage, securityId, "sell", -3m, 12m, SecondDay);
        // The earlier sale's stored gain out of step, so the repair has the position to rewrite.
        await using (var seed = Db.NewDbContext())
        {
            Assert.Equal(1, await seed.RealizedGains.Where(g => g.LedgerId == w.Ledger.LedgerId)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.RealizedGain, g => g.RealizedGain + 1m)));
        }
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        // The position's realized gains, held so the repair waits at its delete.
        await using var holder = Db.NewDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.RealizedGains.Where(g => g.LedgerId == w.Ledger.LedgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RealizedGain, g => g.RealizedGain));

        var repairing = RepairAsync(w, ConsistencyProjections.RealizedGains);
        await WaitForLockWaitersOrDoneAsync(1, repairing);
        Assert.False(repairing.IsCompleted, "the repair did not wait at the held rows, so nothing was tested");

        var selling = TradeAsync(w, brokerage, securityId, "sell", -2m, 13m, ThirdDay);
        await WaitForLockWaitersOrDoneAsync(2, selling);
        Assert.False(selling.IsCompleted, "the sale did not wait for the repair in flight");
        await AssertWaitsAtTheLedgerWriteLockAsync("the sale");

        await held.RollbackAsync();
        using var repaired = await NoDeadlockAsync(repairing, "the repair");
        using var sold = await NoDeadlockAsync(selling, "the sale");

        Assert.True(repaired.StatusCode == HttpStatusCode.OK,
            $"the repair answered {(int)repaired.StatusCode}: {await repaired.Content.ReadAsStringAsync()}");
        Assert.True(sold.StatusCode == HttpStatusCode.Created, "the sale " + await DescribeAsync(w, sold, ThirdDay));
        await AssertHealthyAsync(w);
    }

    /// <summary>
    /// The realized-gains repair sent while a sale of the position it rewrites is saved and
    /// not yet committed: the repair's statement waits at the ledger write lock the sale
    /// holds, before it walks the position, so what it writes counts the sale. Both succeed.
    /// </summary>
    [Fact]
    public async Task The_realized_gains_repair_sent_while_a_sale_of_its_position_is_in_flight_counts_the_sale()
    {
        await using var w = await WorldAsync();
        var securityId = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var brokerage = await BrokerageAsync(w, "Brokerage R");
        await TradedAsync(w, brokerage, securityId, "buy", 10m, 10m, FirstDay);
        await TradedAsync(w, brokerage, securityId, "sell", -3m, 12m, SecondDay);
        // The earlier sale's stored gain out of step, so the repair has the position to rewrite.
        await using (var seed = Db.NewDbContext())
        {
            Assert.Equal(1, await seed.RealizedGains.Where(g => g.LedgerId == w.Ledger.LedgerId)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.RealizedGain, g => g.RealizedGain + 1m)));
        }
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        await using var sale = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await using var held = await sale.Database.BeginTransactionAsync();
        var sold = await new InvestmentTransactionsRepository(sale, TimeProvider.System)
            .CreateAsync(w.Ledger.LedgerId, Trade(brokerage, securityId, "sell", -2m, 13m, ThirdDay));
        Assert.Null(sold.Failure);

        var repairing = RepairAsync(w, ConsistencyProjections.RealizedGains);
        await WaitForLockWaitersOrDoneAsync(1, repairing);
        Assert.False(repairing.IsCompleted, "the repair did not wait for the sale in flight");
        await AssertWaitsAtTheLedgerWriteLockAsync("the repair");

        await held.CommitAsync();
        using var repaired = await NoDeadlockAsync(repairing, "the repair");

        Assert.True(repaired.StatusCode == HttpStatusCode.OK,
            $"the repair answered {(int)repaired.StatusCode}: {await repaired.Content.ReadAsStringAsync()}");
        await AssertHealthyAsync(w);
    }

    /// <summary>
    /// A purchase saved while the holdings repair waits partway through writing back a
    /// position whose holding is not stored: the repair has created the holding and not
    /// committed it, and the purchase, which would create it too, waits at the ledger write
    /// lock the repair's statement holds and then finds it. Both succeed.
    /// </summary>
    [Fact]
    public async Task A_purchase_sent_while_the_holdings_repair_writes_back_its_position_waits_for_it()
    {
        await using var w = await WorldAsync();
        var securityId = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var brokerage = await BrokerageAsync(w, "Brokerage R");
        await TradedAsync(w, brokerage, securityId, "buy", 10m, 10m, FirstDay);
        await TradedAsync(w, brokerage, securityId, "sell", -3m, 12m, SecondDay);
        // The position's holding not stored, as the consistency tests plant it: written
        // through EF past the writers that recompute, so the repair has it to write back.
        await using (var seed = Db.NewDbContext())
        {
            Assert.Equal(1, await seed.Holdings.Where(h => h.LedgerId == w.Ledger.LedgerId).ExecuteDeleteAsync());
        }
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        // The position's realized gains, held so the repair waits at its delete, after it
        // has created the holding.
        await using var holder = Db.NewDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.RealizedGains.Where(g => g.LedgerId == w.Ledger.LedgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RealizedGain, g => g.RealizedGain));

        var repairing = RepairAsync(w, ConsistencyProjections.Holdings);
        await WaitForLockWaitersOrDoneAsync(1, repairing);
        Assert.False(repairing.IsCompleted, "the repair did not wait at the held rows, so nothing was tested");

        var buying = TradeAsync(w, brokerage, securityId, "buy", 2m, 13m, ThirdDay);
        await WaitForLockWaitersOrDoneAsync(2, buying);
        Assert.False(buying.IsCompleted, "the purchase did not wait for the repair in flight");
        await AssertWaitsAtTheLedgerWriteLockAsync("the purchase");

        await held.RollbackAsync();
        using var repaired = await NoDeadlockAsync(repairing, "the repair");
        using var bought = await NoDeadlockAsync(buying, "the purchase");

        Assert.True(repaired.StatusCode == HttpStatusCode.OK,
            $"the repair answered {(int)repaired.StatusCode}: {await repaired.Content.ReadAsStringAsync()}");
        Assert.True(bought.StatusCode == HttpStatusCode.Created, "the purchase " + await DescribeAsync(w, bought, ThirdDay));
        await AssertHealthyAsync(w);
    }

    /// <summary>
    /// A sale saved while the trade-commission flip waits partway through rewriting its
    /// brokerage's positions, the sale's among them: the flip took the ledger write lock
    /// before it wrote the flag, so the sale waits at the lock for the whole flip, then
    /// recomputes the position from what it committed. Both succeed.
    /// </summary>
    [Fact]
    public async Task A_sale_sent_while_the_trade_commission_flip_rewrites_its_position_waits_for_it()
    {
        await using var w = await WorldAsync();
        var securityId = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var brokerage = await BrokerageAsync(w, "Brokerage R");
        await TradedAsync(w, brokerage, securityId, "buy", 10m, 10m, FirstDay);
        await TradedAsync(w, brokerage, securityId, "sell", -3m, 12m, SecondDay);
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        // The position's realized gains, held so the flip's recompute waits at its delete.
        await using var holder = Db.NewDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.RealizedGains.Where(g => g.LedgerId == w.Ledger.LedgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RealizedGain, g => g.RealizedGain));

        var flipping = w.Client.PatchAsJsonAsync(
            $"/api/ledgers/{w.Ledger.LedgerId}/accounts/{brokerage}/trade-commission",
            new PatchAccountTradeCommissionRequest { Enabled = true });
        await WaitForLockWaitersOrDoneAsync(1, flipping);
        Assert.False(flipping.IsCompleted, "the flip did not wait at the held rows, so nothing was tested");

        var selling = TradeAsync(w, brokerage, securityId, "sell", -2m, 13m, ThirdDay);
        await WaitForLockWaitersOrDoneAsync(2, selling);
        Assert.False(selling.IsCompleted, "the sale did not wait for the flip in flight");
        await AssertWaitsAtTheLedgerWriteLockAsync("the sale");

        await held.RollbackAsync();
        using var flipped = await NoDeadlockAsync(flipping, "the flip");
        using var sold = await NoDeadlockAsync(selling, "the sale");

        Assert.True(flipped.StatusCode == HttpStatusCode.NoContent,
            $"the flip answered {(int)flipped.StatusCode}: {await flipped.Content.ReadAsStringAsync()}");
        Assert.True(sold.StatusCode == HttpStatusCode.Created, "the sale " + await DescribeAsync(w, sold, ThirdDay));
        await AssertHealthyAsync(w);
    }

    /// <summary>
    /// Two sales of one position that holds an earlier sale, the second sent while the
    /// first is saved and not yet committed: the second waits for the first at the ledger
    /// write lock, then recomputes the position and the brokerage's balances from what it
    /// committed. Both succeed.
    /// </summary>
    [Fact]
    public async Task A_sale_sent_while_another_sale_of_the_position_is_in_flight_waits_for_it()
    {
        await using var w = await WorldAsync();
        var securityId = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var brokerage = await BrokerageAsync(w, "Brokerage R");
        await TradedAsync(w, brokerage, securityId, "buy", 10m, 10m, FirstDay);
        await TradedAsync(w, brokerage, securityId, "sell", -3m, 12m, SecondDay);
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        await using var first = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await using var held = await first.Database.BeginTransactionAsync();
        var firstSale = await new InvestmentTransactionsRepository(first, TimeProvider.System)
            .CreateAsync(w.Ledger.LedgerId, Trade(brokerage, securityId, "sell", -3m, 13m, ThirdDay));
        Assert.Null(firstSale.Failure);

        var selling = TradeAsync(w, brokerage, securityId, "sell", -2m, 14m, FourthDay);
        await WaitForLockWaitersOrDoneAsync(1, selling);
        Assert.False(selling.IsCompleted, "the second sale did not wait for the first in flight");
        await AssertWaitsAtTheLedgerWriteLockAsync("the second sale");

        await held.CommitAsync();
        using var sold = await NoDeadlockAsync(selling, "the second sale");

        Assert.True(sold.StatusCode == HttpStatusCode.Created, "the second sale " + await DescribeAsync(w, sold, FourthDay));
        await AssertHealthyAsync(w);
    }

    // ----- a run that recomputes several accounts or positions -----

    /// <summary>
    /// Two balance recomputes of the same two accounts in two transactions that take no
    /// lock of their own, named in opposite orders, the second started once the first has
    /// recomputed one account: the function took the ledger write lock at the first's first
    /// account, so the second waits at the lock, before it recomputes or row-locks
    /// anything, for the whole of the first. Neither is a deadlock victim.
    /// </summary>
    [Fact]
    public async Task Two_balance_recomputes_naming_two_accounts_in_opposite_orders_take_turns_at_the_ledger_write_lock()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var one = await ledger.AddBankAccountAsync("Checking");
        var two = await ledger.AddBankAccountAsync("Savings");
        await ledger.AddTransactionPairAsync(one.Id, two.Id, -25m, FirstDay);

        await using var secondDb = Db.NewRequestDbContextAsUser(ledger.UserId);
        await secondDb.Database.OpenConnectionAsync();
        var pid = Pid(secondDb);
        Task? second = null;
        var waited = false;
        List<string> waits = [];
        var hold = new BeforeNthCommand("recompute_balances_for_account", 2, async () =>
        {
            second = InTransactionAsync(secondDb, () =>
                new LegDerivedRecomputeService(secondDb).RecomputeAsync([(two.Id, FirstDay), (one.Id, FirstDay)]));
            await WaitForLockWaitersOrDoneAsync(1, second, pid);
            waited = !second.IsCompleted;
            waits = await WaitEventsAsync(pid);
        });
        await using (var firstDb = Db.NewRequestDbContextAsUser(ledger.UserId, hold))
        {
            await NoDeadlockAsync(InTransactionAsync(firstDb, () =>
                new LegDerivedRecomputeService(firstDb).RecomputeAsync([(one.Id, FirstDay), (two.Id, FirstDay)])),
                "the first recompute");
        }
        Assert.True(hold.Fired, "the second recompute was never started between the two accounts, so nothing was tested");
        await NoDeadlockAsync(second!, "the second recompute");
        Assert.True(waited, "the second recompute did not wait for the first");
        Assert.True(waits.SequenceEqual(["advisory"]),
            $"the second recompute did not wait at the ledger write lock (waits: {string.Join(", ", waits)})");
    }

    /// <summary>
    /// Two holdings recomputes of the same two positions in two transactions that take no
    /// lock of their own, named in opposite orders, the second started once the first has
    /// recomputed one position: the function took the ledger write lock at the first's
    /// first position, so the second waits at the lock, before it reads or creates a
    /// position, for the whole of the first. Neither is a deadlock victim.
    /// </summary>
    [Fact]
    public async Task Two_holdings_recomputes_naming_two_positions_in_opposite_orders_take_turns_at_the_ledger_write_lock()
    {
        await using var w = await WorldAsync();
        var brokerage = await BrokerageAsync(w, "Brokerage R");
        var holdings = await HoldingsAccountOfAsync(brokerage);
        var one = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var two = await w.Ledger.AddSecurityAsync(name: "Security S", ticker: "SECS");
        await TradedAsync(w, brokerage, one, "buy", 10m, 10m, FirstDay);
        await TradedAsync(w, brokerage, two, "buy", 10m, 10m, FirstDay);

        await using var secondDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await secondDb.Database.OpenConnectionAsync();
        var pid = Pid(secondDb);
        Task? second = null;
        var waited = false;
        List<string> waits = [];
        var hold = new BeforeNthCommand("recompute_holdings_for_account_security", 2, async () =>
        {
            second = InTransactionAsync(secondDb, () =>
                new HoldingsRecomputeService(secondDb).RecomputeAsync([(holdings, two), (holdings, one)]));
            await WaitForLockWaitersOrDoneAsync(1, second, pid);
            waited = !second.IsCompleted;
            waits = await WaitEventsAsync(pid);
        });
        await using (var firstDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            await NoDeadlockAsync(InTransactionAsync(firstDb, () =>
                new HoldingsRecomputeService(firstDb).RecomputeAsync([(holdings, one), (holdings, two)])),
                "the first recompute");
        }
        Assert.True(hold.Fired, "the second recompute was never started between the two positions, so nothing was tested");
        await NoDeadlockAsync(second!, "the second recompute");
        Assert.True(waited, "the second recompute did not wait for the first");
        Assert.True(waits.SequenceEqual(["advisory"]),
            $"the second recompute did not wait at the ledger write lock (waits: {string.Join(", ", waits)})");
        await AssertHealthyAsync(w);
    }

    /// <summary>
    /// A brokerage-wide holdings recompute (the trade-commission flip's), run as a statement
    /// of its own and waiting partway through at a holding another transaction holds, and a
    /// recompute of both positions in a transaction sent meanwhile: the brokerage-wide
    /// statement holds the ledger write lock, so the other waits at the lock for the whole
    /// of it, before it reads or row-locks any position, whatever order it names them in.
    /// Neither is a deadlock victim.
    /// </summary>
    [Fact]
    public async Task A_recompute_sent_while_a_brokerage_wide_holdings_recompute_runs_waits_for_all_of_it_at_the_ledger_write_lock()
    {
        await using var w = await WorldAsync();
        var brokerage = await BrokerageAsync(w, "Brokerage R");
        var holdings = await HoldingsAccountOfAsync(brokerage);
        var one = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var two = await w.Ledger.AddSecurityAsync(name: "Security S", ticker: "SECS");
        await TradedAsync(w, brokerage, one, "buy", 10m, 10m, FirstDay);
        await TradedAsync(w, brokerage, two, "buy", 10m, 10m, SecondDay);

        // One position's holdings row, held by a transaction that writes it without the
        // ledger write lock, as a row lock outside the lock's order would: the
        // brokerage-wide recompute waits at its update of that row, holding the lock.
        await using var holder = Db.NewDbContext();
        await using var heldRows = await holder.Database.BeginTransactionAsync();
        Assert.Equal(1, await holder.Holdings.Where(h => h.AccountId == holdings && h.SecurityId == two)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Quantity, h => h.Quantity)));

        await using var wideDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await wideDb.Database.OpenConnectionAsync();
        var widePid = Pid(wideDb);
        var wide = wideDb.RecomputeHoldingsForBrokerage(holdings).Select(r => r.RecomputedCount).SingleAsync();
        await WaitForLockWaitersOrDoneAsync(1, wide, widePid);
        Assert.False(wide.IsCompleted, "the brokerage-wide recompute did not wait at the held row, so nothing was tested");

        await using var pairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await pairDb.Database.OpenConnectionAsync();
        var pairPid = Pid(pairDb);
        var pair = InTransactionAsync(pairDb, () =>
            new HoldingsRecomputeService(pairDb).RecomputeAsync([(holdings, two), (holdings, one)]));
        await WaitForLockWaitersOrDoneAsync(1, pair, pairPid);
        Assert.False(pair.IsCompleted, "the recompute of both positions did not wait for the brokerage-wide one");
        await AssertWaitsAtTheLedgerWriteLockAsync("the recompute of both positions", pairPid);

        await heldRows.RollbackAsync();
        Assert.Equal(2, await NoDeadlockAsync(wide, "the brokerage-wide recompute"));
        await NoDeadlockAsync(pair, "the recompute of both positions");
        await AssertHealthyAsync(w);
    }

    /// <summary>Run <paramref name="work"/> in a transaction of <paramref name="db"/>'s that
    /// takes no lock of its own, and commit it: the recompute functions take the ledger write
    /// lock themselves at the first call.</summary>
    private static async Task InTransactionAsync(AppDbContext db, Func<Task> work)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await work();
        await tx.CommitAsync();
    }
}
