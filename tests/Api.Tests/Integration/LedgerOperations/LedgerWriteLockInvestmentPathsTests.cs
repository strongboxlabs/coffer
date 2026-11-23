using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// The investment writers, <c>merge_securities</c> and a trade-price re-derivation that
/// opens its own transaction take the ledger write lock (migration 275) as their first
/// statement: each waits for another write of the ledger before it reads anything, and
/// two that meet take turns, whatever steps either has saved.
/// </summary>
/// <remarks>
/// <para>Each path is run while another transaction holds the ledger's write lock: it must
/// wait, and the only command it has sent while it waits is the lock. Without the lock,
/// a path that recomputes is refused by the recompute functions (it wrote first), and one
/// that does not (the trade-price re-derivation) runs past the holder.</para>
/// <para>Then the shapes where a writer is part way through when another arrives: a
/// security merge holding its source's row lock against the holdings repair of that
/// source's position, and an edit or an in-kind transfer between its saves against a
/// purchase of another position in the same brokerage. Every write is awaited through
/// <see cref="NoDeadlockAsync{T}"/>, so a deadlock fails the test by name, and each test
/// ends on the stored figures and a clean consistency check.</para>
/// <para>And the positions a merge recomputes: those whose lines carry shares of the
/// source on a transaction (not a reminder's template), and those holding it. A line
/// that names the source with no shares, or a template's line, holds nothing, and
/// recomputing its account would store a holding there.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerWriteLockInvestmentPathsTests : DatabaseTest
{
    private static readonly DateTime FirstDay = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondDay = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ThirdDay = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string LedgerWriteLockFunction = "fn_ledger_write_lock";
    private const string HoldingsRecompute = "recompute_holdings_for_account_security";

    private static readonly Regex LegUpdate =
        new(@"^\s*UPDATE\s+""?txn_legs""?\s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LotInsert =
        new(@"^\s*INSERT\s+INTO\s+""?lots""?\s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public LedgerWriteLockInvestmentPathsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // ----- the world -----

    private sealed record World(SyntheticLedger Ledger, Guid Brokerage, Guid Holdings, Guid Security)
    {
        public Guid LedgerId => Ledger.LedgerId;
        public Guid UserId => Ledger.UserId;
    }

    /// <summary>A ledger with one brokerage, its holdings account and one security.</summary>
    private async Task<World> WorldAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var security = await ledger.AddSecurityAsync(name: "Security A", ticker: "SECA");
        return new World(ledger, brokerage.Id, brokerage.HoldingsAccountId!.Value, security);
    }

    private static InvestmentTransactionsRepository Investments(AppDbContext db) => new(db, TimeProvider.System);

    private static SecuritiesRepository Securities(AppDbContext db) => new(db, TimeProvider.System);

    private static LedgerConsistencyRepository Consistency(AppDbContext db) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db), TimeProvider.System);

    private static CreateInvestmentTransactionRequest Trade(
        Guid brokerage, Guid security, string action, decimal shares, decimal price, DateTime day) => new()
    {
        BrokerageAccountId = brokerage,
        PostedAt = day,
        Action = action,
        SecurityId = security,
        Shares = shares,
        Price = price,
    };

    private static CreateInvestmentTransactionRequest InKind(
        Guid from, Guid to, Guid security, decimal shares, DateTime day) => new()
    {
        BrokerageAccountId = from,
        PostedAt = day,
        Action = "transfer_shares",
        SecurityId = security,
        Shares = shares,
        TransferAccountId = to,
    };

    /// <summary>A transaction saved through the investment create, on a request's context.</summary>
    private async Task<Guid> SavedAsync(World w, CreateInvestmentTransactionRequest request)
    {
        await using var db = Db.NewRequestDbContextAsUser(w.UserId);
        var created = await Investments(db).CreateAsync(w.LedgerId, request);
        Assert.True(created.Failure is null && created.Refusal is null,
            $"the seed was refused: {created.Failure} {created.Refusal}");
        return created.HeaderId;
    }

    /// <summary>
    /// An investment reminder whose template buys <paramref name="transaction"/>'s shares,
    /// created by the reminders repository. It runs on a context without the save
    /// interceptors, and the template's stored notes and posting counts are then written
    /// as the upgrade writes them: the balance and holdings walks leave a template out, so
    /// nothing else an interceptor derives differs.
    /// </summary>
    private async Task ReminderAsync(World w, CreateInvestmentTransactionRequest transaction)
    {
        Guid templateHeaderId;
        await using (var db = Db.NewDbContext())
        {
            var created = await new RemindersRepository(
                    db, new RecurrenceExpander(), new InvestmentTransactionsRepository(db, TimeProvider.System),
                    new TransactionsRepository(db, TimeProvider.System), TimeProvider.System)
                .CreateInvestmentAsync(w.LedgerId, new CreateInvestmentReminderRequest
                {
                    Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                    StartDate = new DateOnly(2026, 7, 1),
                    Transaction = transaction,
                });
            Assert.True(created.Outcome == RemindersRepository.CreateOutcome.Ok,
                $"the reminder was refused: {created.Outcome} {created.InvestmentFailure}");
            templateHeaderId = await db.RecurringTransactions.AsNoTracking()
                .Where(r => r.Id == created.ReminderId)
                .Select(r => r.TemplateHeaderId!.Value)
                .SingleAsync();
        }
        await w.Ledger.RecomputeNotesAsync(templateHeaderId);
        await w.Ledger.RecomputePostingCountsAsync();
    }

    private async Task<decimal?> QuantityAsync(Guid account, Guid security)
    {
        await using var db = Db.NewDbContext();
        return await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == account && h.SecurityId == security)
            .Select(h => (decimal?)h.Quantity)
            .SingleOrDefaultAsync();
    }

    /// <summary>The securities <paramref name="account"/> has a stored holding of.</summary>
    private async Task<List<Guid>> HoldingsOnAsync(Guid account)
    {
        await using var db = Db.NewDbContext();
        return await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == account)
            .Select(h => h.SecurityId)
            .ToListAsync();
    }

    private async Task<SecurityPriceRow> PriceRowAsync(Guid security, DateTime day)
    {
        await using var db = Db.NewDbContext();
        var date = DateOnly.FromDateTime(day);
        return await db.SecurityPrices.AsNoTracking()
            .SingleAsync(p => p.SecurityId == security && p.PriceDate == date);
    }

    /// <summary>Every projection healthy: what is stored is what a fresh walk derives.</summary>
    private async Task AssertHealthyAsync(World w)
    {
        await using var db = Db.NewDbContext();
        var report = await Consistency(db).CheckAsync(w.LedgerId);
        Assert.True(report.Healthy, "findings: " + string.Join(" | ",
            report.Projections.Where(p => !p.Healthy).Select(p => p.Projection + ": " + string.Join("; ",
                p.Mismatches.Select(m => $"{m.Scope}: {m.Field} stored {m.Stored}, expected {m.Expected}")))));
    }

    // ----- waiting, and deadlocks -----

    private static int Pid(AppDbContext db) => ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;

    /// <summary>Wait until <paramref name="pid"/> waits on a lock, or <paramref name="running"/>
    /// is done. Asked as the superuser: the writes run as the app role, whose sessions' wait
    /// events another role cannot read.</summary>
    private async Task WaitForLockWaitOrDoneAsync(int pid, Task running)
    {
        await using var probe = new NpgsqlConnection(Db.SuperuserConnectionString);
        await probe.OpenAsync();
        for (var i = 0; i < 400 && !running.IsCompleted; i++)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock'", probe);
            cmd.Parameters.AddWithValue("pid", pid);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(25);
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

    private static string Victim(string what, PostgresException deadlock) =>
        $"{what} was chosen as a deadlock victim: {deadlock.MessageText} ({deadlock.Detail}) at: {deadlock.Where}";

    private static PostgresException? Deadlock(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            if (e is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected } p) return p;
        return null;
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

    /// <summary>Let <paramref name="started"/>, a write a test started beside another, finish
    /// before the context it runs on is disposed, within a bound: when the other write
    /// fails, this one must not be left running on a context being disposed.</summary>
    private static async Task SettleAsync(Task? started)
    {
        if (started is not null) await Task.WhenAny(started, Task.Delay(TimeSpan.FromSeconds(30)));
    }

    /// <summary>Accepts the <paramref name="nth"/> command whose text matches.</summary>
    private static Func<DbCommand, bool> Nth(int nth, Func<string, bool> matches)
    {
        var seen = 0;
        return c => matches(c.CommandText) && ++seen == nth;
    }

    /// <summary>The text of every command a context sends, in order.</summary>
    private sealed class SentCommands : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _texts = new();

        public IReadOnlyList<string> Texts => [.. _texts];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _texts.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _texts.Enqueue(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            _texts.Enqueue(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Run <paramref name="write"/> on a request's context while another transaction holds
    /// the ledger's write lock: it must wait, having sent nothing but the lock, and once the
    /// holder ends it must finish. Returns what it returned.
    /// </summary>
    private async Task<T> TakesTheLockFirstAsync<T>(World w, string what, Func<AppDbContext, Task<T>> write)
    {
        await using var holderDb = Db.NewDbContext();
        var held = await holderDb.BeginLedgerWriteAsync(w.LedgerId);
        var sent = new SentCommands();
        await using var db = Db.NewRequestDbContextAsUser(w.UserId, sent);
        await db.Database.OpenConnectionAsync();
        Task<T>? writing = null;
        try
        {
            writing = write(db);
            await WaitForLockWaitOrDoneAsync(Pid(db), writing);
            if (writing.IsCompleted)
            {
                // A refusal surfaces here as itself.
                await writing;
                Assert.Fail($"{what} ran to the end past another write holding the ledger's write lock");
            }
            var texts = sent.Texts;
            Assert.True(texts.Count == 1 && texts[0].Contains(LedgerWriteLockFunction, StringComparison.Ordinal),
                $"{what} sent {texts.Count} command(s) before it waited, not the lock alone:\n"
                + string.Join("\n---\n", texts));
        }
        finally
        {
            await held.DisposeAsync();
            await SettleAsync(writing);
        }
        return await NoDeadlockAsync(writing, what);
    }

    // ----- each path takes the lock first -----

    [Fact]
    public async Task A_purchase_takes_the_ledger_write_lock_before_it_reads_anything()
    {
        var w = await WorldAsync();

        var bought = await TakesTheLockFirstAsync(w, "the purchase", db =>
            Investments(db).CreateAsync(w.LedgerId, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay)));

        Assert.Null(bought.Failure);
        Assert.Equal(10m, await QuantityAsync(w.Holdings, w.Security));
        await AssertHealthyAsync(w);
    }

    [Fact]
    public async Task An_in_kind_transfer_takes_the_ledger_write_lock_before_it_reads_anything()
    {
        var w = await WorldAsync();
        var dest = await w.Ledger.AddInvestmentAccountAsync("Brokerage B");
        await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));

        var moved = await TakesTheLockFirstAsync(w, "the in-kind transfer", db =>
            Investments(db).CreateAsync(w.LedgerId, InKind(w.Brokerage, dest.Id, w.Security, 4m, SecondDay)));

        Assert.Null(moved.Failure);
        Assert.Equal(6m, await QuantityAsync(w.Holdings, w.Security));
        Assert.Equal(4m, await QuantityAsync(dest.HoldingsAccountId!.Value, w.Security));
        await AssertHealthyAsync(w);
    }

    [Fact]
    public async Task An_edit_takes_the_ledger_write_lock_before_it_reads_anything()
    {
        var w = await WorldAsync();
        var bought = await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));

        var edited = await TakesTheLockFirstAsync(w, "the edit", db =>
            Investments(db).PatchAsync(w.LedgerId, bought, new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = w.Brokerage,
                PostedAt = SecondDay,
                Action = "buy",
                SecurityId = w.Security,
                Shares = 12m,
                Price = 10m,
            }));

        Assert.True(edited is { PatchFail: null, CreateFail: null, Refusal: null }, $"the edit was refused: {edited}");
        Assert.Equal(12m, await QuantityAsync(w.Holdings, w.Security));
        await AssertHealthyAsync(w);
    }

    [Fact]
    public async Task A_delete_takes_the_ledger_write_lock_before_it_reads_anything()
    {
        var w = await WorldAsync();
        await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));
        var sold = await SavedAsync(w, Trade(w.Brokerage, w.Security, "sell", -3m, 12m, SecondDay));

        var deleted = await TakesTheLockFirstAsync(w, "the delete", db =>
            Investments(db).DeleteAsync(w.LedgerId, sold));

        Assert.Equal(InvestmentTransactionsRepository.DeleteOutcome.HardDeleted, deleted);
        await using (var db = Db.NewDbContext())
            Assert.False(await db.TxnHeaders.AsNoTracking().AnyAsync(h => h.Id == sold));
        Assert.Equal(10m, await QuantityAsync(w.Holdings, w.Security));
        await AssertHealthyAsync(w);
    }

    [Fact]
    public async Task Converting_a_sale_and_a_purchase_to_an_in_kind_transfer_takes_the_lock_before_it_reads_anything()
    {
        var w = await WorldAsync();
        var dest = await w.Ledger.AddInvestmentAccountAsync("Brokerage B");
        await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));
        var sold = await SavedAsync(w, Trade(w.Brokerage, w.Security, "sell", -4m, 12m, SecondDay));
        var bought = await SavedAsync(w, Trade(dest.Id, w.Security, "buy", 4m, 12m, SecondDay));

        var converted = await TakesTheLockFirstAsync(w, "the conversion", db =>
            Investments(db).ConvertInKindTransferAsync(w.LedgerId, sold, bought));

        Assert.Equal(InvestmentTransactionsRepository.ConvertInKindResult.Ok, converted.Result);
        await using (var db = Db.NewDbContext())
        {
            Assert.False(await db.TxnHeaders.AsNoTracking().AnyAsync(h => h.Id == sold || h.Id == bought));
            Assert.Equal("transfer_shares",
                (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == converted.HeaderId)).Action);
        }
        Assert.Equal(6m, await QuantityAsync(w.Holdings, w.Security));
        Assert.Equal(4m, await QuantityAsync(dest.HoldingsAccountId!.Value, w.Security));
        await AssertHealthyAsync(w);
    }

    [Fact]
    public async Task A_security_merge_takes_the_ledger_write_lock_before_it_reads_anything()
    {
        var w = await WorldAsync();
        var keeper = await w.Ledger.AddSecurityAsync(name: "Security B", ticker: "SECB");
        await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));

        var merged = await TakesTheLockFirstAsync(w, "the merge", db =>
            Securities(db).MergeSecuritiesAsync(w.LedgerId, w.Security, keeper, dryRun: false));

        Assert.Equal(SecuritiesRepository.MergeSecuritiesResult.Ok, merged.Result);
        Assert.Equal(10m, await QuantityAsync(w.Holdings, keeper));
        Assert.Equal(0m, await QuantityAsync(w.Holdings, w.Security));
        await using (var db = Db.NewDbContext())
            Assert.False((await db.Securities.AsNoTracking().SingleAsync(s => s.Id == w.Security)).IsActive);
        await AssertHealthyAsync(w);
    }

    /// <summary>A re-derivation called with no transaction open opens its own with the
    /// lock first; called in a caller's, it leaves the lock to the caller.</summary>
    [Fact]
    public async Task A_trade_price_rederivation_with_no_transaction_takes_the_ledger_write_lock_first()
    {
        var w = await WorldAsync();
        await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));
        // A stale price planted on the trade's day, as the consistency tests plant one, so
        // the re-derivation has a figure to write.
        await using (var seed = Db.NewDbContext())
        {
            var date = DateOnly.FromDateTime(FirstDay);
            Assert.Equal(1, await seed.SecurityPrices.Where(p => p.SecurityId == w.Security && p.PriceDate == date)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 11m)));
        }

        await TakesTheLockFirstAsync(w, "the re-derivation", async db =>
        {
            await new TradePriceRecomputeService(db)
                .ReseedAsync([(w.LedgerId, w.Security, DateOnly.FromDateTime(FirstDay))]);
            return true;
        });

        Assert.Equal(10m, (await PriceRowAsync(w.Security, FirstDay)).Price);
        await AssertHealthyAsync(w);
    }

    // ----- two writes that meet -----

    /// <summary>
    /// The holdings repair of a security's position sent while a merge of that security
    /// holds the ledger's write lock and its row lock on the source, and is about to move
    /// the source's lines: the repair's recompute waits for the merge at the lock, holding
    /// nothing, and then writes the position back from what the merge committed. Neither is
    /// a deadlock victim.
    /// </summary>
    [Fact]
    public async Task The_holdings_repair_of_a_merged_securitys_position_waits_for_the_merge_in_flight()
    {
        var w = await WorldAsync();
        var keeper = await w.Ledger.AddSecurityAsync(name: "Security B", ticker: "SECB");
        await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));
        await SavedAsync(w, Trade(w.Brokerage, w.Security, "sell", -3m, 12m, SecondDay));
        // The source's holding not stored, as the consistency tests plant it, so the repair
        // has the merge's own position to write back.
        await using (var seed = Db.NewDbContext())
        {
            Assert.Equal(1, await seed.Holdings.Where(h => h.AccountId == w.Holdings && h.SecurityId == w.Security)
                .ExecuteDeleteAsync());
        }

        await using var repairDb = Db.NewRequestDbContextAsUser(w.UserId);
        await repairDb.Database.OpenConnectionAsync();
        var repairPid = Pid(repairDb);
        Task<ProjectionRepairResult>? repairing = null;
        var waited = false;
        var hold = new BeforeCommand(Nth(1, LegUpdate.IsMatch), async () =>
        {
            repairing = Consistency(repairDb).RepairAsync(w.LedgerId, ConsistencyProjections.Holdings);
            await WaitForLockWaitOrDoneAsync(repairPid, repairing);
            waited = !repairing.IsCompleted;
        });
        SecuritiesRepository.MergeSecuritiesOutcome merged;
        try
        {
            await using var mergeDb = Db.NewRequestDbContextAsUser(w.UserId, hold);
            merged = await NoDeadlockAsync(
                Securities(mergeDb).MergeSecuritiesAsync(w.LedgerId, w.Security, keeper, dryRun: false), "the merge");
        }
        finally
        {
            await SettleAsync(repairing);
        }
        Assert.True(hold.Fired, "the repair was never started before the merge moved the lines, so nothing was tested");
        var repaired = await NoDeadlockAsync(repairing!, "the holdings repair");
        Assert.True(waited, "the holdings repair did not wait for the merge in flight");

        Assert.Equal(SecuritiesRepository.MergeSecuritiesResult.Ok, merged.Result);
        Assert.Equal(2, merged.LegsMoved);
        Assert.True(repaired.ProjectionAfter().Healthy, "the repair left holdings findings");
        Assert.Equal(7m, await QuantityAsync(w.Holdings, keeper));
        Assert.Equal(0m, await QuantityAsync(w.Holdings, w.Security));
        await AssertHealthyAsync(w);
    }

    /// <summary>
    /// A purchase of one position sent while an edit of another position in the same
    /// brokerage is between its saves (its first has re-dated the transaction and
    /// recomputed, its second has not run): the purchase waits for the whole edit at the
    /// lock, then saves. Neither is a deadlock victim.
    /// </summary>
    [Fact]
    public async Task A_purchase_sent_while_an_edit_in_the_same_brokerage_is_between_saves_waits_for_it()
    {
        var w = await WorldAsync();
        var other = await w.Ledger.AddSecurityAsync(name: "Security B", ticker: "SECB");
        var edited = await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));
        await SavedAsync(w, Trade(w.Brokerage, other, "buy", 10m, 20m, FirstDay));

        await using var buyDb = Db.NewRequestDbContextAsUser(w.UserId);
        await buyDb.Database.OpenConnectionAsync();
        var buyPid = Pid(buyDb);
        Task<InvestmentTransactionsRepository.CreateResult>? buying = null;
        var waited = false;
        // At the edit's first holdings recompute: its first save has written the new date
        // and recomputed the brokerage's balances.
        var hold = new BeforeCommand(Nth(1, sql => sql.Contains(HoldingsRecompute, StringComparison.Ordinal)), async () =>
        {
            buying = Investments(buyDb).CreateAsync(w.LedgerId, Trade(w.Brokerage, other, "buy", 5m, 21m, SecondDay));
            await WaitForLockWaitOrDoneAsync(buyPid, buying);
            waited = !buying.IsCompleted;
        });
        InvestmentTransactionsRepository.PatchResult patched;
        try
        {
            await using var editDb = Db.NewRequestDbContextAsUser(w.UserId, hold);
            patched = await NoDeadlockAsync(Investments(editDb).PatchAsync(w.LedgerId, edited,
                new PatchInvestmentTransactionRequest
                {
                    BrokerageAccountId = w.Brokerage,
                    PostedAt = ThirdDay,
                    Action = "buy",
                    SecurityId = w.Security,
                    Shares = 12m,
                    Price = 10m,
                }), "the edit");
        }
        finally
        {
            await SettleAsync(buying);
        }
        Assert.True(hold.Fired, "the purchase was never started between the edit's saves, so nothing was tested");
        var bought = await NoDeadlockAsync(buying!, "the purchase");
        Assert.True(waited, "the purchase did not wait for the edit in flight");

        Assert.True(patched is { PatchFail: null, CreateFail: null, Refusal: null }, $"the edit was refused: {patched}");
        Assert.Null(bought.Failure);
        Assert.Equal(12m, await QuantityAsync(w.Holdings, w.Security));
        Assert.Equal(15m, await QuantityAsync(w.Holdings, other));
        await AssertHealthyAsync(w);
    }

    /// <summary>
    /// A purchase of one position sent while an in-kind transfer of another position out of
    /// the same brokerage is between its saves (its lines are saved and recomputed, its
    /// destination lots and their recompute are not): the purchase waits for the whole
    /// transfer at the lock, then saves. Neither is a deadlock victim.
    /// </summary>
    [Fact]
    public async Task A_purchase_sent_while_an_in_kind_transfer_out_of_the_brokerage_is_between_saves_waits_for_it()
    {
        var w = await WorldAsync();
        var dest = await w.Ledger.AddInvestmentAccountAsync("Brokerage B");
        var other = await w.Ledger.AddSecurityAsync(name: "Security B", ticker: "SECB");
        await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));
        await SavedAsync(w, Trade(w.Brokerage, other, "buy", 10m, 20m, FirstDay));

        await using var buyDb = Db.NewRequestDbContextAsUser(w.UserId);
        await buyDb.Database.OpenConnectionAsync();
        var buyPid = Pid(buyDb);
        Task<InvestmentTransactionsRepository.CreateResult>? buying = null;
        var waited = false;
        var hold = new BeforeCommand(Nth(1, LotInsert.IsMatch), async () =>
        {
            buying = Investments(buyDb).CreateAsync(w.LedgerId, Trade(w.Brokerage, other, "buy", 5m, 21m, SecondDay));
            await WaitForLockWaitOrDoneAsync(buyPid, buying);
            waited = !buying.IsCompleted;
        });
        InvestmentTransactionsRepository.CreateResult moved;
        try
        {
            await using var moveDb = Db.NewRequestDbContextAsUser(w.UserId, hold);
            moved = await NoDeadlockAsync(
                Investments(moveDb).CreateAsync(w.LedgerId, InKind(w.Brokerage, dest.Id, w.Security, 4m, SecondDay)),
                "the in-kind transfer");
        }
        finally
        {
            await SettleAsync(buying);
        }
        Assert.True(hold.Fired, "the purchase was never started between the transfer's saves, so nothing was tested");
        var bought = await NoDeadlockAsync(buying!, "the purchase");
        Assert.True(waited, "the purchase did not wait for the transfer in flight");

        Assert.Null(moved.Failure);
        Assert.Null(bought.Failure);
        Assert.Equal(6m, await QuantityAsync(w.Holdings, w.Security));
        Assert.Equal(4m, await QuantityAsync(dest.HoldingsAccountId!.Value, w.Security));
        Assert.Equal(15m, await QuantityAsync(w.Holdings, other));
        await AssertHealthyAsync(w);
    }

    // ----- the positions a merge recomputes -----

    /// <summary>
    /// A dividend's cash line on the brokerage names the security and carries no shares.
    /// The merge moves it with the rest and recomputes only the holdings account, so the
    /// brokerage gets no holding of either security.
    /// </summary>
    [Fact]
    public async Task A_security_merge_stores_no_holding_on_the_brokerage_from_a_dividend_line()
    {
        var w = await WorldAsync();
        var keeper = await w.Ledger.AddSecurityAsync(name: "Security B", ticker: "SECB");
        var dividends = await w.Ledger.AddCategoryAsync("Dividends", "income");
        await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));
        await SavedAsync(w, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = w.Brokerage,
            PostedAt = SecondDay,
            Action = "dividend_cash",
            SecurityId = w.Security,
            Amount = 5m,
            CategoryAccountId = dividends.Id,
        });
        Assert.Empty(await HoldingsOnAsync(w.Brokerage));

        await using var db = Db.NewRequestDbContextAsUser(w.UserId);
        var planned = await Securities(db).MergeSecuritiesAsync(w.LedgerId, w.Security, keeper, dryRun: true);
        var merged = await Securities(db).MergeSecuritiesAsync(w.LedgerId, w.Security, keeper, dryRun: false);

        Assert.Equal(SecuritiesRepository.MergeSecuritiesResult.Ok, merged.Result);
        Assert.Empty(await HoldingsOnAsync(w.Brokerage));
        // The purchase's holdings line and the dividend's cash line move; one account holds shares.
        Assert.Equal((2, 1), (planned.LegsMoved, planned.AccountsRecomputed));
        Assert.Equal((2, 1), (merged.LegsMoved, merged.AccountsRecomputed));
        Assert.Equal(10m, await QuantityAsync(w.Holdings, keeper));
        Assert.Equal(0m, await QuantityAsync(w.Holdings, w.Security));
        await AssertHealthyAsync(w);
    }

    /// <summary>
    /// A reminder's template buying the security in a brokerage that holds none of it: its
    /// holdings line carries shares, but the walk leaves a template out. The merge moves the
    /// line with the rest and recomputes only the position a transaction holds, so the
    /// template's brokerage gets no holding of either security.
    /// </summary>
    [Fact]
    public async Task A_security_merge_stores_no_holding_from_a_reminder_templates_line()
    {
        var w = await WorldAsync();
        var keeper = await w.Ledger.AddSecurityAsync(name: "Security B", ticker: "SECB");
        var planned = await w.Ledger.AddInvestmentAccountAsync("Brokerage B");
        var plannedHoldings = planned.HoldingsAccountId!.Value;
        await SavedAsync(w, Trade(w.Brokerage, w.Security, "buy", 10m, 10m, FirstDay));
        await ReminderAsync(w, Trade(planned.Id, w.Security, "buy", 2m, 10m, FirstDay));
        Assert.Empty(await HoldingsOnAsync(plannedHoldings));

        await using var db = Db.NewRequestDbContextAsUser(w.UserId);
        var merged = await Securities(db).MergeSecuritiesAsync(w.LedgerId, w.Security, keeper, dryRun: false);

        Assert.Equal(SecuritiesRepository.MergeSecuritiesResult.Ok, merged.Result);
        Assert.Empty(await HoldingsOnAsync(plannedHoldings));
        Assert.Empty(await HoldingsOnAsync(planned.Id));
        // The purchase's holdings line and the template's move; one account holds shares.
        Assert.Equal((2, 1), (merged.LegsMoved, merged.AccountsRecomputed));
        Assert.Equal(10m, await QuantityAsync(w.Holdings, keeper));
        await AssertHealthyAsync(w);
    }
}
