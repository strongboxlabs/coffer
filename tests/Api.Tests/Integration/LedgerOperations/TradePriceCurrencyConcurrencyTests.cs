using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// Two writes at the same moment that write a trade price, or a currency the trade-price
/// rule reads: each ends with the figures the last commit implies, and neither is chosen
/// as a deadlock victim (migration 274's lock order, ADR-0084 D2, 2026-11-04 amendment,
/// with the ledger write lock before it, migration 275, ADR-0112).
/// </summary>
/// <remarks>
/// <para>The lock order: first the ledger write lock, which every path that can recompute
/// balances or holdings, a currency edit and every repair take as their transaction's
/// first statement, so two of them in one ledger take turns at it, the later waiting
/// before it has locked anything (an <c>advisory</c> wait,
/// <see cref="WaitEventAsync"/>). Then (<see cref="AppDbContext.LockTradePriceAccounts"/>)
/// the accounts whose currency the rule reads for the days a path derives, with the
/// account a currency edit writes, in id order, in one statement, before any holdings
/// account or price row, FOR SHARE for a re-derivation and the holdings-currency repair,
/// FOR NO KEY UPDATE for a currency edit and the trade-price repair; then the read; then
/// the price rows, in one order. Those row-lock steps are what keep a re-derivation in a
/// transaction that took no ledger write lock from a cycle, which the tests that run one
/// that way hold them to.</para>
/// <para>Each test holds one side open at the point the other side's read, lock or price
/// row write would get past without the lock or the order under test, lets both finish,
/// and asks the data and the check. Where both sides take the ledger write lock, it also
/// asks that the later one waited there (<c>advisory</c>), since the row-lock steps alone
/// would serialise most of these pairs too; where one side takes none, it asks where the
/// other waited, when the outcome alone cannot tell. Every write is awaited through
/// <see cref="NoDeadlockAsync{T}"/>, so a deadlock fails the test by name rather than as a
/// stray exception.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class TradePriceCurrencyConcurrencyTests : DatabaseTest
{
    private static readonly DateTime FirstDay = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondDay = new(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Regex AccountUpdate =
        new(@"^\s*UPDATE\s+""?accounts""?\s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LegUpdate =
        new(@"^\s*UPDATE\s+""?txn_legs""?\s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public TradePriceCurrencyConcurrencyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private sealed record World(SyntheticLedger Ledger, ApiFactory Factory, HttpClient Client, Guid Brokerage, Guid SecurityId)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }

    /// <summary>A ledger, a security and a dollar brokerage made through the API, with a buy
    /// on the first day unless <paramref name="buy"/> is false.</summary>
    private async Task<World> WorldAsync(bool buy = true)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var securityId = await ledger.AddSecurityAsync(name: "Security A", ticker: "SECA");
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        var world = new World(ledger, factory, client, Guid.Empty, securityId);
        world = world with { Brokerage = await BrokerageAsync(world, "Brokerage A") };
        if (buy) await BuyAsync(world, world.Brokerage, FirstDay, 10m);
        return world;
    }

    private static async Task<Guid> BrokerageAsync(World w, string name, string currency = "USD")
    {
        var made = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = name, AccountType = "investment", CurrencyCode = currency });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static CreateInvestmentTransactionRequest Buy(
        World w, Guid brokerage, DateTime day, decimal price, Guid? feeAccount = null) => new()
    {
        BrokerageAccountId = brokerage,
        PostedAt = day,
        Action = "buy",
        SecurityId = w.SecurityId,
        Shares = 10m,
        Price = price,
        Amount = 10m * price,
        FeeAccountId = feeAccount,
        FeeAmount = feeAccount is null ? null : 1m,
    };

    private static async Task BuyAsync(World w, Guid brokerage, DateTime day, decimal price, Guid? feeAccount = null)
    {
        var resp = await w.Client.PostAsJsonAsync(
            $"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions", Buy(w, brokerage, day, price, feeAccount));
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
    }

    private static async Task EditCurrencyAsync(World w, Guid brokerage, string currency)
    {
        var resp = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/accounts/{brokerage}",
            new UpdateAccountRequest { CurrencyCode = currency });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static AccountsRepository Accounts(AppDbContext db) =>
        new(db, new LegDerivedRecomputeService(db), TimeProvider.System);

    private static LedgerConsistencyRepository Consistency(AppDbContext db) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db),
            TimeProvider.System);

    private async Task<SecurityPriceRow> PriceRowAsync(Guid securityId, DateTime day)
    {
        await using var db = Db.NewDbContext();
        var date = DateOnly.FromDateTime(day);
        return await db.SecurityPrices.AsNoTracking()
            .SingleAsync(p => p.SecurityId == securityId && p.PriceDate == date);
    }

    /// <summary>The earlier edit's write, planted as the consistency tests plant a stale
    /// row: the brokerage's currency alone, its holdings account left as it was.</summary>
    private async Task DriftAsync(Guid brokerage, string currency)
    {
        await using var seed = Db.NewDbContext();
        await seed.Accounts.Where(a => a.Id == brokerage)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrencyCode, currency));
    }

    /// <summary>A stale price planted on a day, so the trade-price repair has one to write.</summary>
    private async Task PlantPriceAsync(Guid securityId, DateTime day, decimal price)
    {
        await using var seed = Db.NewDbContext();
        var date = DateOnly.FromDateTime(day);
        Assert.Equal(1, await seed.SecurityPrices.Where(p => p.SecurityId == securityId && p.PriceDate == date)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, price)));
    }

    /// <summary>Each brokerage and its holdings account in the given currency, and every
    /// projection healthy.</summary>
    private async Task AssertSettledAsync(World w, params (Guid Brokerage, string Currency)[] expected)
    {
        await using (var db = Db.NewDbContext())
        {
            foreach (var (id, currency) in expected)
            {
                var brokerage = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == id);
                var holdings = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == brokerage.HoldingsAccountId);
                Assert.Equal((currency, currency), (brokerage.CurrencyCode, holdings.CurrencyCode));
            }
        }
        await using (var db = Db.NewDbContext())
        {
            var report = await Consistency(db).CheckAsync(w.Ledger.LedgerId);
            Assert.True(report.Healthy, "findings after both writes: " + string.Join(" | ",
                report.Projections.Where(p => !p.Healthy).Select(p => p.Projection + ": " + string.Join("; ",
                    p.Mismatches.Select(m => $"{m.Scope}: {m.Field}")))));
        }
    }

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
        $"{what} was chosen as a deadlock victim: {deadlock.MessageText} ({deadlock.Detail}) at: {deadlock.Where}";

    private static PostgresException? Deadlock(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            if (e is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected } p) return p;
        return null;
    }

    /// <summary>Wait until a session of this test's database waits on a lock, or
    /// <paramref name="running"/> is done. Asked as the superuser: the writes run as the
    /// app role, whose sessions' wait events another role cannot read.</summary>
    private async Task WaitForLockOrDoneAsync(Task running, int? pid = null)
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
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(25);
        }
    }

    /// <summary>What a session of this test's database that waits on a lock
    /// (<paramref name="pid"/>'s when given) waits on: <c>advisory</c> at the ledger write
    /// lock, <c>transactionid</c> or <c>tuple</c> at a row; null when none waits. Asked as
    /// the superuser, as above.</summary>
    private async Task<string?> WaitEventAsync(int? pid = null)
    {
        await using var probe = new NpgsqlConnection(Db.SuperuserConnectionString);
        await probe.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT wait_event FROM pg_stat_activity
             WHERE datname = current_database() AND wait_event_type = 'Lock'
               AND (@pid::int IS NULL OR pid = @pid::int)
            """, probe);
        cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)pid ?? DBNull.Value });
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>Run <paramref name="work"/> in a transaction of <paramref name="db"/>'s that
    /// takes no ledger write lock of its own, and commit it.</summary>
    private static async Task InTransactionAsync(AppDbContext db, Func<Task> work)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await work();
        await tx.CommitAsync();
    }

    private static int Pid(AppDbContext db) => ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;

    /// <summary>Runs <c>action</c> once, just before the context's transaction commits.</summary>
    private sealed class BeforeCommit(Func<Task> action) : DbTransactionInterceptor
    {
        private int _fired;

        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0) await action();
            return result;
        }
    }

    /// <summary>Runs <c>action</c> once, just before the first command <c>at</c> accepts is
    /// sent. <c>at</c> sees every command in order, so it can count.</summary>
    private sealed class BeforeCommand(Func<DbCommand, bool> at, Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await MaybeAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await MaybeAsync(command);
            return result;
        }

        private async Task MaybeAsync(DbCommand command)
        {
            if (at(command) && Interlocked.Exchange(ref _fired, 1) == 0) await action();
        }
    }

    /// <summary>Accepts the <paramref name="nth"/> command whose text matches.</summary>
    private static Func<DbCommand, bool> Nth(int nth, Func<string, bool> matches)
    {
        var seen = 0;
        return c => matches(c.CommandText) && ++seen == nth;
    }

    /// <summary>
    /// A trade saved and not yet committed when the brokerage's currency is edited: the
    /// edit waits for the trade at the ledger write lock, then reads its days and prices
    /// the trade's day in the new currency.
    /// </summary>
    [Fact]
    public async Task A_currency_edit_waits_for_a_trade_in_flight_and_prices_its_day_in_the_new_currency()
    {
        await using var w = await WorldAsync();

        // The trade, held open: saved, its day re-derived in dollars, not committed.
        await using var trade = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await using var held = await trade.Database.BeginTransactionAsync();
        var bought = await new InvestmentTransactionsRepository(trade, TimeProvider.System)
            .CreateAsync(w.Ledger.LedgerId, Buy(w, w.Brokerage, SecondDay, 12m));
        Assert.Null(bought.Failure);

        await using var edit = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await edit.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)edit.Database.GetDbConnection()).ProcessID;
        var editing = Accounts(edit).UpdateAsync(
            w.Ledger.LedgerId, w.Brokerage, new UpdateAccountRequest { CurrencyCode = "EUR" });
        await WaitForLockOrDoneAsync(editing, pid);
        Assert.False(editing.IsCompleted, "the edit did not wait for the trade in flight");
        Assert.Equal("advisory", await WaitEventAsync(pid));
        await held.CommitAsync();
        Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await NoDeadlockAsync(editing, "the edit"));

        var row = await PriceRowAsync(w.SecurityId, SecondDay);
        Assert.Equal((12m, "EUR"), (row.Price, row.CurrencyCode));
        Assert.Equal("EUR", (await PriceRowAsync(w.SecurityId, FirstDay)).CurrencyCode);
        await AssertSettledAsync(w, (w.Brokerage, "EUR"));
    }

    /// <summary>
    /// A trade saved while the currency edit is written and not yet committed: it waits for
    /// the edit at the ledger write lock and prices its day in the new currency.
    /// </summary>
    [Fact]
    public async Task A_trade_saved_while_a_currency_edit_is_in_flight_prices_its_day_in_the_new_currency()
    {
        await using var w = await WorldAsync();

        Task? buying = null;
        var waited = false;
        string? waitedAt = null;
        var hold = new BeforeCommit(async () =>
        {
            buying = BuyAsync(w, w.Brokerage, SecondDay, 12m);
            await WaitForLockOrDoneAsync(buying);
            waited = !buying.IsCompleted;
            waitedAt = await WaitEventAsync();
        });
        await using (var edit = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await NoDeadlockAsync(Accounts(edit).UpdateAsync(
                w.Ledger.LedgerId, w.Brokerage, new UpdateAccountRequest { CurrencyCode = "EUR" }), "the edit"));
        }
        Assert.True(hold.Fired, "the trade was never started, so nothing was tested");
        await NoDeadlockAsync(buying!, "the trade");
        Assert.True(waited, "the trade did not wait for the currency edit in flight");
        Assert.Equal("advisory", waitedAt);

        var row = await PriceRowAsync(w.SecurityId, SecondDay);
        Assert.Equal((12m, "EUR"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "EUR"));
    }

    /// <summary>
    /// The trade-price repair run while the currency edit is written and not yet
    /// committed: it waits for the edit at the ledger write lock, and writes what the edit
    /// commits.
    /// </summary>
    [Fact]
    public async Task The_trade_price_repair_waits_for_a_currency_edit_in_flight_and_writes_its_currency()
    {
        await using var w = await WorldAsync();
        await PlantPriceAsync(w.SecurityId, FirstDay, 11m);

        Task<ProjectionRepairResult>? repairing = null;
        var waited = false;
        string? waitedAt = null;
        await using var repairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await repairDb.Database.OpenConnectionAsync();
        var pid = Pid(repairDb);
        var hold = new BeforeCommit(async () =>
        {
            repairing = Consistency(repairDb).RepairAsync(w.Ledger.LedgerId, ConsistencyProjections.TradePrices);
            await WaitForLockOrDoneAsync(repairing, pid);
            waited = !repairing.IsCompleted;
            waitedAt = await WaitEventAsync(pid);
        });
        await using (var edit = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await NoDeadlockAsync(Accounts(edit).UpdateAsync(
                w.Ledger.LedgerId, w.Brokerage, new UpdateAccountRequest { CurrencyCode = "EUR" }), "the edit"));
        }
        Assert.True(hold.Fired, "the repair was never started, so nothing was tested");
        var repaired = await NoDeadlockAsync(repairing!, "the repair");
        Assert.True(repaired.ProjectionAfter().Healthy, "the repair left trade-price findings");
        Assert.True(waited, "the repair did not wait for the currency edit in flight");
        Assert.Equal("advisory", waitedAt);

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "EUR"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "EUR"));
    }

    /// <summary>
    /// The trade-price repair started between a currency edit's update of the brokerage and
    /// its update of the holdings account, on a brokerage whose fee line carries the
    /// security and whose holdings account sorts first: the repair waits for the edit at the
    /// ledger write lock, before it locks any account, and neither is a deadlock victim. (Its
    /// row locks name only the brokerage, whose currency the rule reads, which keeps the
    /// two apart where no ledger write lock is taken.)
    /// </summary>
    [Fact]
    public async Task The_trade_price_repair_between_a_currency_edits_two_updates_does_not_deadlock()
    {
        await using var w = await WorldAsync(buy: false);
        var fees = await w.Ledger.AddCategoryAsync("Fees");
        // A brokerage whose holdings account sorts before it, as about half do: the order
        // in which locking both would form a cycle with the edit, which writes the
        // brokerage first.
        var brokerage = w.Brokerage;
        for (var i = 2; ; i++)
        {
            await using var db = Db.NewDbContext();
            var holdings = (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == brokerage)).HoldingsAccountId!.Value;
            var first = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == brokerage || a.Id == holdings)
                .OrderBy(a => a.Id).Select(a => a.Id).FirstAsync();
            if (first == holdings) break;
            Assert.True(i < 40, "no brokerage made through the API had its holdings account sort first");
            brokerage = await BrokerageAsync(w, $"Brokerage A{i}");
        }
        await BuyAsync(w, brokerage, FirstDay, 10m, fees.Id);
        await PlantPriceAsync(w.SecurityId, FirstDay, 11m);

        Task<ProjectionRepairResult>? repairing = null;
        var waited = false;
        string? waitedAt = null;
        await using var repairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await repairDb.Database.OpenConnectionAsync();
        var pid = Pid(repairDb);
        var hold = new BeforeCommand(Nth(2, AccountUpdate.IsMatch), async () =>
        {
            repairing = Consistency(repairDb).RepairAsync(w.Ledger.LedgerId, ConsistencyProjections.TradePrices);
            await WaitForLockOrDoneAsync(repairing, pid);
            waited = !repairing.IsCompleted;
            waitedAt = await WaitEventAsync(pid);
        });
        await using (var edit = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await NoDeadlockAsync(Accounts(edit).UpdateAsync(
                w.Ledger.LedgerId, brokerage, new UpdateAccountRequest { CurrencyCode = "EUR" }), "the edit"));
        }
        Assert.True(hold.Fired, "the repair was never started between the two updates, so nothing was tested");
        var repaired = await NoDeadlockAsync(repairing!, "the repair");
        Assert.True(repaired.ProjectionAfter().Healthy, "the repair left trade-price findings");
        Assert.True(waited, "the repair did not wait for the currency edit in flight");
        Assert.Equal("advisory", waitedAt);

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "EUR"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (brokerage, "EUR"));
    }

    /// <summary>
    /// The trade-price repair run while a trade on a day it repairs is saved and not yet
    /// committed: it waits for the trade at the ledger write lock, and the day keeps the
    /// trade's price, which the rule names once the trade commits.
    /// </summary>
    [Fact]
    public async Task The_trade_price_repair_waits_for_a_trade_in_flight_and_leaves_the_day_as_the_trade_priced_it()
    {
        await using var w = await WorldAsync();
        await PlantPriceAsync(w.SecurityId, FirstDay, 11m);

        // A later buy on the same day, held open: the rule's pick once it commits.
        await using var trade = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await using var held = await trade.Database.BeginTransactionAsync();
        var bought = await new InvestmentTransactionsRepository(trade, TimeProvider.System)
            .CreateAsync(w.Ledger.LedgerId, Buy(w, w.Brokerage, FirstDay, 12m));
        Assert.Null(bought.Failure);

        await using var repairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await repairDb.Database.OpenConnectionAsync();
        var pid = Pid(repairDb);
        var repairing = Consistency(repairDb).RepairAsync(w.Ledger.LedgerId, ConsistencyProjections.TradePrices);
        await WaitForLockOrDoneAsync(repairing, pid);
        Assert.False(repairing.IsCompleted, "the repair did not wait for the trade in flight");
        Assert.Equal("advisory", await WaitEventAsync(pid));
        await held.CommitAsync();
        var repaired = await NoDeadlockAsync(repairing, "the repair");
        Assert.True(repaired.ProjectionAfter().Healthy, "the repair left trade-price findings");

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((12m, "USD"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "USD"));
    }

    /// <summary>
    /// A trade saved on a day the trade-price repair writes, once the repair has locked and
    /// read its figures and before it writes them: the trade waits for the repair at the
    /// ledger write lock, and prices the day after the repair commits, so the repair's
    /// figure, read before the trade, does not overwrite the trade's. Neither is a deadlock
    /// victim.
    /// </summary>
    [Fact]
    public async Task A_trade_saved_while_the_trade_price_repair_is_in_flight_prices_its_day_after_it()
    {
        await using var w = await WorldAsync();
        await PlantPriceAsync(w.SecurityId, FirstDay, 11m);

        // Held after the repair's lock and read, before its write: the trade meets no price
        // row the repair holds, only its ledger write lock.
        Task? buying = null;
        var waited = false;
        string? waitedAt = null;
        var hold = new BeforeCommand(Nth(1, sql => sql.Contains("security_price_upsert_from_trade", StringComparison.Ordinal)), async () =>
        {
            buying = BuyAsync(w, w.Brokerage, FirstDay, 12m);
            await WaitForLockOrDoneAsync(buying);
            waited = !buying.IsCompleted;
            waitedAt = await WaitEventAsync();
        });
        await using (var repairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            _ = await NoDeadlockAsync(
                Consistency(repairDb).RepairAsync(w.Ledger.LedgerId, ConsistencyProjections.TradePrices), "the repair");
            Assert.True(hold.Fired, "the trade was never started, so nothing was tested");
        }
        await NoDeadlockAsync(buying!, "the trade");

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((12m, "USD"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "USD"));
        Assert.True(waited, "the trade did not wait for the trade-price repair in flight");
        Assert.Equal("advisory", waitedAt);
    }

    /// <summary>
    /// A re-derivation of two days whose accounts sort the other way round from the order
    /// it derives them in, run in a caller's transaction that took no ledger write lock,
    /// and the trade-price repair of the second day started between them: the
    /// re-derivation took the accounts of both days in one statement before the first, so
    /// the repair, past its ledger write lock, waits for it at those accounts, and neither
    /// is a deadlock victim.
    /// </summary>
    [Fact]
    public async Task A_re_derivation_of_two_days_and_the_trade_price_repair_take_their_locks_in_one_order()
    {
        await using var w = await WorldAsync(buy: false);
        var other = await BrokerageAsync(w, "Brokerage B");
        Guid low, high;
        await using (var db = Db.NewDbContext())
        {
            var ordered = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == w.Brokerage || a.Id == other)
                .OrderBy(a => a.Id).Select(a => a.Id).ToListAsync();
            (low, high) = (ordered[0], ordered[1]);
        }
        await EditCurrencyAsync(w, low, "EUR");
        await EditCurrencyAsync(w, high, "GBP");
        // The first day is the second brokerage's alone; the second day is both
        // brokerages', the later buy (the rule's pick) the second brokerage's.
        await BuyAsync(w, high, FirstDay, 10m);
        await BuyAsync(w, low, SecondDay, 20m);
        await BuyAsync(w, high, SecondDay, 21m);
        await PlantPriceAsync(w.SecurityId, SecondDay, 19m);

        Task<ProjectionRepairResult>? repairing = null;
        await using var repairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        var hold = new BeforeCommand(Nth(2, sql => sql.Contains("fn_trade_price_reseed", StringComparison.Ordinal)), async () =>
        {
            repairing = Consistency(repairDb).RepairAsync(w.Ledger.LedgerId, ConsistencyProjections.TradePrices);
            await WaitForLockOrDoneAsync(repairing);
        });
        await using (var reseedDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            await using var tx = await reseedDb.Database.BeginTransactionAsync();
            await NoDeadlockAsync(new TradePriceRecomputeService(reseedDb).ReseedAsync(
            [
                (w.Ledger.LedgerId, w.SecurityId, DateOnly.FromDateTime(FirstDay)),
                (w.Ledger.LedgerId, w.SecurityId, DateOnly.FromDateTime(SecondDay)),
            ]), "the re-derivation");
            await tx.CommitAsync();
        }
        Assert.True(hold.Fired, "the repair was never started between the two days, so nothing was tested");
        var repaired = await NoDeadlockAsync(repairing!, "the repair");
        Assert.True(repaired.ProjectionAfter().Healthy, "the repair left trade-price findings");

        var first = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "GBP"), (first.Price, first.CurrencyCode));
        var second = await PriceRowAsync(w.SecurityId, SecondDay);
        Assert.Equal((21m, "GBP"), (second.Price, second.CurrencyCode));
        await AssertSettledAsync(w, (low, "EUR"), (high, "GBP"));
    }

    /// <summary>
    /// A brokerage whose holdings account kept the old code: its holdings-currency repair
    /// run while a trade is saved and not yet committed waits for the trade at the ledger
    /// write lock, before it locks the brokerage, then relabels the holdings account; the
    /// trade's day is in the brokerage's currency.
    /// </summary>
    [Fact]
    public async Task The_holdings_currency_repair_waits_for_a_trade_in_flight()
    {
        await using var w = await WorldAsync(buy: false);
        await DriftAsync(w.Brokerage, "EUR");

        await using var trade = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await using var held = await trade.Database.BeginTransactionAsync();
        var bought = await new InvestmentTransactionsRepository(trade, TimeProvider.System)
            .CreateAsync(w.Ledger.LedgerId, Buy(w, w.Brokerage, FirstDay, 10m));
        Assert.Null(bought.Failure);

        await using var repairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await repairDb.Database.OpenConnectionAsync();
        var pid = Pid(repairDb);
        var repairing = Consistency(repairDb).RepairAsync(w.Ledger.LedgerId, ConsistencyProjections.HoldingsCurrency);
        await WaitForLockOrDoneAsync(repairing, pid);
        Assert.False(repairing.IsCompleted, "the holdings-currency repair did not wait for the trade in flight");
        Assert.Equal("advisory", await WaitEventAsync(pid));
        await held.CommitAsync();
        await NoDeadlockAsync(repairing, "the holdings-currency repair");

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "EUR"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "EUR"));
    }

    /// <summary>
    /// A trade saved while the holdings-currency repair of its brokerage is not yet
    /// committed: the trade waits for the repair at the ledger write lock, then prices its
    /// day in the brokerage's currency.
    /// </summary>
    [Fact]
    public async Task A_trade_saved_while_the_holdings_currency_repair_is_in_flight_waits_for_it()
    {
        await using var w = await WorldAsync(buy: false);
        await DriftAsync(w.Brokerage, "EUR");

        Task? buying = null;
        var waited = false;
        string? waitedAt = null;
        var hold = new BeforeCommit(async () =>
        {
            buying = BuyAsync(w, w.Brokerage, FirstDay, 10m);
            await WaitForLockOrDoneAsync(buying);
            waited = !buying.IsCompleted;
            waitedAt = await WaitEventAsync();
        });
        await using (var repairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            await NoDeadlockAsync(
                Consistency(repairDb).RepairAsync(w.Ledger.LedgerId, ConsistencyProjections.HoldingsCurrency),
                "the holdings-currency repair");
        }
        Assert.True(hold.Fired, "the trade was never started, so nothing was tested");
        await NoDeadlockAsync(buying!, "the trade");
        Assert.True(waited, "the trade did not wait for the holdings-currency repair in flight");
        Assert.Equal("advisory", waitedAt);

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "EUR"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "EUR"));
    }

    /// <summary>
    /// The holdings-currency repair run while a currency edit of the same brokerage is not
    /// yet committed: it waits for the edit at the ledger write lock, so it reads the code
    /// the edit commits and leaves the pair agreeing.
    /// </summary>
    [Fact]
    public async Task The_holdings_currency_repair_waits_for_a_currency_edit_in_flight()
    {
        await using var w = await WorldAsync();
        await DriftAsync(w.Brokerage, "EUR");

        Task<ProjectionRepairResult>? repairing = null;
        var waited = false;
        string? waitedAt = null;
        await using var repairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await repairDb.Database.OpenConnectionAsync();
        var pid = Pid(repairDb);
        var hold = new BeforeCommit(async () =>
        {
            repairing = Consistency(repairDb).RepairAsync(w.Ledger.LedgerId, ConsistencyProjections.HoldingsCurrency);
            await WaitForLockOrDoneAsync(repairing, pid);
            waited = !repairing.IsCompleted;
            waitedAt = await WaitEventAsync(pid);
        });
        await using (var edit = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await NoDeadlockAsync(Accounts(edit).UpdateAsync(
                w.Ledger.LedgerId, w.Brokerage, new UpdateAccountRequest { CurrencyCode = "GBP" }), "the edit"));
        }
        Assert.True(hold.Fired, "the repair was never started, so nothing was tested");
        await NoDeadlockAsync(repairing!, "the holdings-currency repair");
        Assert.True(waited, "the holdings-currency repair did not wait for the currency edit in flight");
        Assert.Equal("advisory", waitedAt);

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "GBP"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "GBP"));
    }

    /// <summary>
    /// One security bought on one day in two brokerages, both edited at once, the second
    /// edit started once the first has written its brokerage: the second waits for the
    /// first at the ledger write lock, before it locks either brokerage, neither is a
    /// deadlock victim, and the day ends in the currency of the brokerage whose trade the
    /// rule picks.
    /// </summary>
    [Fact]
    public async Task Two_brokerages_trading_one_day_edited_at_once_take_their_locks_in_one_order()
    {
        await using var w = await WorldAsync();
        var other = await BrokerageAsync(w, "Brokerage B");
        // The later buy, the rule's pick for the day, is the second brokerage's.
        await BuyAsync(w, other, FirstDay, 12m);

        Task<AccountsRepository.UpdateAccountOutcome>? editingOther = null;
        var waited = false;
        string? waitedAt = null;
        await using var otherDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await otherDb.Database.OpenConnectionAsync();
        var pid = Pid(otherDb);
        var seenUpdate = false;
        var hold = new BeforeCommand(c =>
        {
            if (seenUpdate) return true;
            seenUpdate = AccountUpdate.IsMatch(c.CommandText);
            return false;
        }, async () =>
        {
            editingOther = Accounts(otherDb).UpdateAsync(
                w.Ledger.LedgerId, other, new UpdateAccountRequest { CurrencyCode = "GBP" });
            await WaitForLockOrDoneAsync(editingOther, pid);
            waited = !editingOther.IsCompleted;
            waitedAt = await WaitEventAsync(pid);
        });
        await using (var edit = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await NoDeadlockAsync(Accounts(edit).UpdateAsync(
                w.Ledger.LedgerId, w.Brokerage, new UpdateAccountRequest { CurrencyCode = "EUR" }), "the first edit"));
        }
        Assert.True(hold.Fired, "the second edit was never started, so nothing was tested");
        Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await NoDeadlockAsync(editingOther!, "the second edit"));
        Assert.True(waited, "the second edit did not wait for the first");
        Assert.Equal("advisory", waitedAt);

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((12m, "GBP"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "EUR"), (other, "GBP"));
    }

    /// <summary>
    /// A currency edit sent while an edit of the brokerage's other fields is between its
    /// read of the brokerage and its update: the currency edit waits for the other edit at
    /// the ledger write lock, which every account edit takes before it reads the account,
    /// then reads what the other edit committed and writes its currency over it. No edit
    /// writes back a field it read before another edit of the account committed: the
    /// brokerage, its holdings account and the trade prices end in the currency edit's
    /// currency, with the other edit's tax status. <paramref name="loadedCurrency"/> is what
    /// the other edit sends for the currency, nothing (an MCP edit of the tax status alone)
    /// or the code it loaded (the account editor sends it on every save); either way it is
    /// no change.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("USD")]
    public async Task A_currency_edit_sent_while_another_edit_is_between_its_read_and_its_update_waits_for_it(string? loadedCurrency)
    {
        await using var w = await WorldAsync();

        // Held after its read of the brokerage and before its update: the currency edit is
        // sent in between.
        Task? editingCurrency = null;
        var waited = false;
        string? waitedAt = null;
        var hold = new BeforeCommand(Nth(1, AccountUpdate.IsMatch), async () =>
        {
            editingCurrency = EditCurrencyAsync(w, w.Brokerage, "EUR");
            await WaitForLockOrDoneAsync(editingCurrency);
            waited = !editingCurrency.IsCompleted;
            waitedAt = await WaitEventAsync();
        });
        await using (var edit = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await NoDeadlockAsync(Accounts(edit).UpdateAsync(
                w.Ledger.LedgerId, w.Brokerage,
                new UpdateAccountRequest { TaxStatus = "taxable", CurrencyCode = loadedCurrency }), "the other edit"));
        }
        Assert.True(hold.Fired, "the currency edit was never sent between the read and the update, so nothing was tested");
        await NoDeadlockAsync(editingCurrency!, "the currency edit");
        Assert.True(waited, "the currency edit did not wait for the other edit in flight");
        Assert.Equal("advisory", waitedAt);

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "EUR"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "EUR"));
        await using var db = Db.NewDbContext();
        Assert.Equal("taxable", (await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == w.Brokerage)).TaxStatus);
    }

    /// <summary>
    /// Two re-derivations of the same two days of one security, named in opposite orders,
    /// each opening its own transaction, the second started once the first has written its
    /// first day: each opens its transaction with the ledger write lock, so the second waits
    /// for the whole of the first there, before it locks an account or a price row, and
    /// neither is a deadlock victim.
    /// </summary>
    [Fact]
    public async Task Two_re_derivations_of_two_days_named_in_opposite_orders_take_turns_at_the_ledger_write_lock()
    {
        await using var w = await WorldAsync();
        await BuyAsync(w, w.Brokerage, SecondDay, 12m);
        var first = (w.Ledger.LedgerId, w.SecurityId, DateOnly.FromDateTime(FirstDay));
        var second = (w.Ledger.LedgerId, w.SecurityId, DateOnly.FromDateTime(SecondDay));

        Task? other = null;
        var waited = false;
        string? waitedAt = null;
        await using var otherDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await otherDb.Database.OpenConnectionAsync();
        var pid = Pid(otherDb);
        var hold = new BeforeCommand(Nth(2, sql => sql.Contains("fn_trade_price_reseed", StringComparison.Ordinal)), async () =>
        {
            other = new TradePriceRecomputeService(otherDb).ReseedAsync([second, first]);
            await WaitForLockOrDoneAsync(other, pid);
            waited = !other.IsCompleted;
            waitedAt = await WaitEventAsync(pid);
        });
        await using (var reseedDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            await NoDeadlockAsync(new TradePriceRecomputeService(reseedDb).ReseedAsync([first, second]), "the first re-derivation");
        }
        Assert.True(hold.Fired, "the second re-derivation was never started between the two days, so nothing was tested");
        await NoDeadlockAsync(other!, "the second re-derivation");
        Assert.True(waited, "the second re-derivation did not wait for the first");
        Assert.True(waitedAt == "advisory",
            $"the second re-derivation waited at {waitedAt ?? "nothing"}, not at the ledger write lock");

        var firstRow = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "USD"), (firstRow.Price, firstRow.CurrencyCode));
        var secondRow = await PriceRowAsync(w.SecurityId, SecondDay);
        Assert.Equal((12m, "USD"), (secondRow.Price, secondRow.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "USD"));
    }

    /// <summary>
    /// The same two re-derivations, each in a caller's transaction that took no ledger write
    /// lock, where the re-derivation leaves the lock to its caller: both write the days in
    /// one order (<see cref="TradePriceRecomputeService.InWriteOrder"/>), so the second
    /// waits for the first at its first price row instead of holding the row the first is
    /// about to write, and neither is a deadlock victim.
    /// </summary>
    [Fact]
    public async Task Two_re_derivations_outside_the_ledger_write_lock_named_in_opposite_orders_write_them_in_one_order()
    {
        await using var w = await WorldAsync();
        await BuyAsync(w, w.Brokerage, SecondDay, 12m);
        var first = (w.Ledger.LedgerId, w.SecurityId, DateOnly.FromDateTime(FirstDay));
        var second = (w.Ledger.LedgerId, w.SecurityId, DateOnly.FromDateTime(SecondDay));

        Task? other = null;
        var waited = false;
        string? waitedAt = null;
        await using var otherDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await otherDb.Database.OpenConnectionAsync();
        var pid = Pid(otherDb);
        var hold = new BeforeCommand(Nth(2, sql => sql.Contains("fn_trade_price_reseed", StringComparison.Ordinal)), async () =>
        {
            other = InTransactionAsync(otherDb, () =>
                new TradePriceRecomputeService(otherDb).ReseedAsync([second, first]));
            await WaitForLockOrDoneAsync(other, pid);
            waited = !other.IsCompleted;
            waitedAt = await WaitEventAsync(pid);
        });
        await using (var reseedDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            await NoDeadlockAsync(InTransactionAsync(reseedDb, () =>
                new TradePriceRecomputeService(reseedDb).ReseedAsync([first, second])), "the first re-derivation");
        }
        Assert.True(hold.Fired, "the second re-derivation was never started between the two days, so nothing was tested");
        await NoDeadlockAsync(other!, "the second re-derivation");
        Assert.True(waited, "the second re-derivation did not wait for the first");
        Assert.True(waitedAt is "transactionid" or "tuple",
            $"the second re-derivation waited at {waitedAt ?? "nothing"}, not at a price row");

        var firstRow = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "USD"), (firstRow.Price, firstRow.CurrencyCode));
        var secondRow = await PriceRowAsync(w.SecurityId, SecondDay);
        Assert.Equal((12m, "USD"), (secondRow.Price, secondRow.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "USD"));
    }

    /// <summary>
    /// A currency edit started once the trade-price repair has locked and read its figures
    /// and before it writes them: the edit waits for the repair at the ledger write lock,
    /// which both take first, then writes its currency over the repair's figure, so the day
    /// ends in the brokerage's new currency.
    /// </summary>
    [Fact]
    public async Task A_currency_edit_started_while_the_trade_price_repair_is_in_flight_waits_for_it()
    {
        await using var w = await WorldAsync();
        await PlantPriceAsync(w.SecurityId, FirstDay, 11m);

        Task<AccountsRepository.UpdateAccountOutcome>? editing = null;
        var waited = false;
        string? waitedAt = null;
        await using var editDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await editDb.Database.OpenConnectionAsync();
        var pid = Pid(editDb);
        var hold = new BeforeCommand(Nth(1, sql => sql.Contains("security_price_upsert_from_trade", StringComparison.Ordinal)), async () =>
        {
            editing = Accounts(editDb).UpdateAsync(
                w.Ledger.LedgerId, w.Brokerage, new UpdateAccountRequest { CurrencyCode = "EUR" });
            await WaitForLockOrDoneAsync(editing, pid);
            waited = !editing.IsCompleted;
            waitedAt = await WaitEventAsync(pid);
        });
        await using (var repairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            _ = await NoDeadlockAsync(
                Consistency(repairDb).RepairAsync(w.Ledger.LedgerId, ConsistencyProjections.TradePrices), "the repair");
        }
        Assert.True(hold.Fired, "the edit was never started, so nothing was tested");
        Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await NoDeadlockAsync(editing!, "the edit"));

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "EUR"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "EUR"));
        Assert.True(waited, "the currency edit did not wait for the trade-price repair in flight");
        Assert.Equal("advisory", waitedAt);
    }

    /// <summary>
    /// A currency edit started once the holdings-currency repair has locked and read its
    /// brokerage and before it writes the holdings account: the edit waits for the repair at
    /// the ledger write lock, and the pair and the trade prices end in the edit's currency.
    /// </summary>
    [Fact]
    public async Task A_currency_edit_started_while_the_holdings_currency_repair_is_in_flight_waits_for_it()
    {
        await using var w = await WorldAsync();
        await DriftAsync(w.Brokerage, "EUR");

        Task<AccountsRepository.UpdateAccountOutcome>? editing = null;
        var waited = false;
        string? waitedAt = null;
        await using var editDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await editDb.Database.OpenConnectionAsync();
        var pid = Pid(editDb);
        var hold = new BeforeCommand(Nth(1, AccountUpdate.IsMatch), async () =>
        {
            editing = Accounts(editDb).UpdateAsync(
                w.Ledger.LedgerId, w.Brokerage, new UpdateAccountRequest { CurrencyCode = "GBP" });
            await WaitForLockOrDoneAsync(editing, pid);
            waited = !editing.IsCompleted;
            waitedAt = await WaitEventAsync(pid);
        });
        await using (var repairDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            await NoDeadlockAsync(
                Consistency(repairDb).RepairAsync(w.Ledger.LedgerId, ConsistencyProjections.HoldingsCurrency),
                "the holdings-currency repair");
        }
        Assert.True(hold.Fired, "the edit was never started, so nothing was tested");
        Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await NoDeadlockAsync(editing!, "the edit"));
        Assert.True(waited, "the currency edit did not wait for the holdings-currency repair in flight");
        Assert.Equal("advisory", waitedAt);

        var row = await PriceRowAsync(w.SecurityId, FirstDay);
        Assert.Equal((10m, "GBP"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "GBP"));
    }

    /// <summary>
    /// A trade on the source security started while <c>merge_securities</c> has read the
    /// source's lines and not yet moved them: the trade waits for the merge at the ledger
    /// write lock, which the merge took before its lock on the source, so no line moves to
    /// the keeper with its day and holdings un-derived, and every projection agrees once
    /// both commit.
    /// </summary>
    [Fact]
    public async Task A_trade_on_the_source_while_merge_securities_is_in_flight_waits_for_it()
    {
        await using var w = await WorldAsync();
        var keeper = await w.Ledger.AddSecurityAsync(name: "Security B", ticker: "SECB");

        Task? buying = null;
        var waited = false;
        string? waitedAt = null;
        var hold = new BeforeCommand(Nth(1, LegUpdate.IsMatch), async () =>
        {
            buying = BuyAsync(w, w.Brokerage, SecondDay, 12m);
            await WaitForLockOrDoneAsync(buying);
            waited = !buying.IsCompleted;
            waitedAt = await WaitEventAsync();
        });
        await using (var mergeDb = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            var merged = await NoDeadlockAsync(new SecuritiesRepository(mergeDb, TimeProvider.System)
                .MergeSecuritiesAsync(w.Ledger.LedgerId, w.SecurityId, keeper, dryRun: false), "the merge");
            Assert.Equal(SecuritiesRepository.MergeSecuritiesResult.Ok, merged.Result);
        }
        Assert.True(hold.Fired, "the trade was never started before the merge moved the lines, so nothing was tested");
        await NoDeadlockAsync(buying!, "the trade");

        var row = await PriceRowAsync(keeper, FirstDay);
        Assert.Equal((10m, "USD"), (row.Price, row.CurrencyCode));
        await AssertSettledAsync(w, (w.Brokerage, "USD"));
        Assert.True(waited, "the trade on the source did not wait for the merge in flight");
        Assert.Equal("advisory", waitedAt);
    }
}
