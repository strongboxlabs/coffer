using System.Data.Common;
using System.Net;
using System.Runtime.ExceptionServices;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// The bank transaction, bulk and reminder writers take the ledger write lock
/// (migration 275) as the first statement of their transaction, so two of them, or one
/// and a consistency repair, over one ledger take turns instead of waiting on each
/// other's rows.
/// </summary>
/// <remarks>
/// <para>Each converted path is run while another transaction holds the ledger's lock:
/// it waits with the lock as the only statement it has sent, then finishes once the
/// holder commits and leaves every projection what a fresh walk derives
/// (<see cref="TakesTheLockFirstAsync{T}"/>). A path that wrote before taking the lock
/// would be refused by the recompute functions, so those tests fail on a path that
/// misses it as well as on one that takes it late.</para>
/// <para>The rest are the interleavings that deadlocked before the lock: a hard delete
/// beside the balances repair and beside a save of the same account, a postings PATCH
/// saved in two steps beside another PATCH, and the auto-post beside a person's post of
/// the same reminder occurrence. Every write is awaited through
/// <see cref="NoDeadlockAsync{T}"/>, so a deadlock fails the test by name.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerWriteLockBankPathsTests : DatabaseTest
{
    private static readonly DateTime FirstDay = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondDay = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ThirdDay = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly Occurrence = new(2026, 2, 1);

    public LedgerWriteLockBankPathsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // ----- helpers -----

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

    private static TransactionsRepository Transactions(AppDbContext db) => new(db, TimeProvider.System);

    private static BulkTransactionsRepository Bulk(AppDbContext db) =>
        new(db, new LegDerivedRecomputeService(db), new HoldingsRecomputeService(db), TimeProvider.System);

    private static RemindersRepository Reminders(AppDbContext db) =>
        new(db, new RecurrenceExpander(), new InvestmentTransactionsRepository(db, TimeProvider.System), Transactions(db), TimeProvider.System);

    private static TransactionPosting[] Posting(Guid counterparty, decimal amount, Guid? legId = null) =>
        [new TransactionPosting { LegId = legId, CounterpartyAccountId = counterparty, Amount = amount }];

    private static int Pid(AppDbContext db) => ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;

    private static string Brief(string sql)
    {
        var flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 120 ? flat : flat[..120] + "...";
    }

    /// <summary>The header a raw pair seed made, from the leg id it returned.</summary>
    private static Task<Guid> HeaderOfAsync(SyntheticLedger ledger, Guid legId) => ledger.ResolveHeaderIdAsync(legId);

    private async Task<Guid> LegOnAsync(Guid headerId, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == accountId).Select(l => l.Id).SingleAsync();
    }

    private async Task<List<Guid>> CounterpartiesAsync(Guid headerId, Guid sourceAccountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId != sourceAccountId).Select(l => l.AccountId).ToListAsync();
    }

    private async Task<TxnHeaderRow?> HeaderAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking().SingleOrDefaultAsync(h => h.Id == headerId);
    }

    /// <summary>Every projection healthy: what is stored is what a fresh walk derives.</summary>
    private async Task AssertHealthyAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        var report = await new LedgerConsistencyRepository(
                db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db), TimeProvider.System)
            .CheckAsync(ledgerId);
        Assert.True(report.Healthy, "findings after the writes: " + string.Join(" | ",
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

    /// <summary>Wait for a write started beside the one under test to end, whatever its
    /// outcome, so a test that fails never leaves it running on a context or a host the
    /// test is about to dispose. Its outcome is asserted, when the test gets that far, by
    /// awaiting it again.</summary>
    private static async Task DrainAsync(Task? write)
    {
        if (write is null) return;
        try
        {
            await write;
        }
        catch (Exception)
        {
            // Asserted by the caller, or superseded by the failure already in flight.
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

    /// <summary>The text of every command a context sends, in order.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent
        {
            get { lock (_sent) return [.. _sent]; }
        }

        private void Add(DbCommand command)
        {
            lock (_sent) _sent.Add(command.CommandText);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Add(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Add(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Add(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Add(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Add(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Add(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Run <paramref name="path"/> on a context wired as a request's while another
    /// transaction holds the ledger's write lock. It must wait, with the lock as the only
    /// statement it has sent, and finish once the holder commits; returns what it returned.
    /// </summary>
    private async Task<T> TakesTheLockFirstAsync<T>(SyntheticLedger ledger, string what, Func<AppDbContext, Task<T>> path)
    {
        var log = new CommandLog();
        await using var db = Db.NewRequestDbContextAsUser(ledger.UserId, log);
        await db.Database.OpenConnectionAsync();
        var pid = Pid(db);
        return await TakesTheLockFirstAsync(ledger, what, log, pid, () => path(db));
    }

    /// <summary>
    /// <see cref="TakesTheLockFirstAsync{T}(SyntheticLedger, string, Func{AppDbContext, Task{T}})"/>
    /// for a path that makes its own service context from the factory it is given.
    /// </summary>
    private async Task<T> TakesTheLockFirstOnAServiceContextAsync<T>(
        SyntheticLedger ledger, string what, Func<ServiceDbContextFactory, Task<T>> path)
    {
        var log = new CommandLog();
        var factory = Db.NewServiceFactoryWith(log);
        return await TakesTheLockFirstAsync(ledger, what, log, pid: null, () => path(factory));
    }

    private async Task<T> TakesTheLockFirstAsync<T>(
        SyntheticLedger ledger, string what, CommandLog log, int? pid, Func<Task<T>> path)
    {
        await using var holder = new NpgsqlConnection(Db.ServiceConnectionString);
        await holder.OpenAsync();
        await using var held = await holder.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand("SELECT locked FROM fn_ledger_write_lock(@l)", holder, held))
        {
            take.Parameters.AddWithValue("l", ledger.LedgerId);
            Assert.True((bool)(await take.ExecuteScalarAsync())!);
        }

        var running = path();
        ExceptionDispatchInfo? failure = null;
        try
        {
            await WaitForLockWaitersOrDoneAsync(1, running, pid);
            Assert.False(running.IsCompleted, running.IsFaulted
                ? $"{what} failed instead of waiting for the ledger write lock: {running.Exception!.GetBaseException().Message}"
                : $"{what} finished without waiting for the ledger write lock another transaction held");
            var sent = log.Sent;
            Assert.True(sent.Count == 1 && sent[0].Contains("fn_ledger_write_lock", StringComparison.Ordinal),
                $"{what} sent {sent.Count} statement(s) before it waited for the ledger write lock: "
                + string.Join(" | ", sent.Select(Brief)));
        }
        catch (Exception e)
        {
            failure = ExceptionDispatchInfo.Capture(e);
        }

        await held.CommitAsync();
        if (failure is not null)
        {
            try
            {
                await running;
            }
            catch (Exception)
            {
                // The failure above is the one reported.
            }
            failure.Throw();
        }
        return await NoDeadlockAsync(running, what);
    }

    // ----- seeds -----

    private async Task<Guid> BankReminderAsync(SyntheticLedger ledger, Guid bank, Guid category)
    {
        await using var db = Db.NewServiceFactory().Create();
        var created = await Reminders(db).CreateBankAsync(ledger.LedgerId, new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = Start,
            Payee = "Landlord",
            SourceAccountId = bank,
            Postings = Posting(category, -900m),
        });
        Assert.Equal(RemindersRepository.CreateOutcome.Ok, created.Outcome);
        return created.ReminderId!.Value;
    }

    private static CreateInvestmentTransactionRequest Dividend(Guid brokerage, Guid security, Guid category, decimal amount, DateOnly day) => new()
    {
        BrokerageAccountId = brokerage,
        PostedAt = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        Action = "dividend_cash",
        SecurityId = security,
        Amount = amount,
        CategoryAccountId = category,
    };

    private async Task<(Guid Brokerage, Guid Security, Guid Dividends)> InvestmentAccountsAsync(SyntheticLedger ledger)
    {
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var dividends = await ledger.AddCategoryAsync("Dividends", "income");
        var security = await ledger.AddSecurityAsync("Index Fund", ticker: "IDXF");
        return (brokerage.Id, security, dividends.Id);
    }

    private async Task<Guid> InvestmentReminderAsync(SyntheticLedger ledger, Guid brokerage, Guid security, Guid dividends)
    {
        await using var db = Db.NewServiceFactory().Create();
        var created = await Reminders(db).CreateInvestmentAsync(ledger.LedgerId, new CreateInvestmentReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = Start,
            Transaction = Dividend(brokerage, security, dividends, 12m, Start),
        });
        Assert.Equal(RemindersRepository.CreateOutcome.Ok, created.Outcome);
        return created.ReminderId!.Value;
    }

    private async Task<int> OccurrencesAsync(Guid reminderId, DateOnly occurrenceDate)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking().CountAsync(h =>
            h.RecurringTransactionId == reminderId && h.OccurrenceDate == occurrenceDate && !h.IsRecurringTemplate);
    }

    // ----- each path takes the lock first: TransactionsRepository -----

    [Fact]
    public async Task A_create_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");

        var created = await TakesTheLockFirstAsync(ledger, "the create", db => Transactions(db).CreateAsync(
            ledger.LedgerId, bank.Id, FirstDay, "Market", checkNumber: null, transactedAt: null,
            Posting(category.Id, -10m), notes: null));

        Assert.Null(created.Refusal);
        Assert.NotNull(await HeaderAsync(created.HeaderId));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_header_patch_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        var header = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, FirstDay)).FromTxnId);
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, SecondDay);

        var patched = await TakesTheLockFirstAsync(ledger, "the header PATCH", db => Transactions(db).PatchAsync(
            ledger.LedgerId, header, new PatchTransactionRequest { PostedAt = ThirdDay }));

        Assert.Equal(TransactionsRepository.PatchResult.Ok, patched.Result);
        Assert.Equal(ThirdDay, (await HeaderAsync(header))!.PostedAt);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_postings_patch_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var dining = await ledger.AddCategoryAsync("Dining");
        var header = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -10m, FirstDay)).FromTxnId);
        var source = await LegOnAsync(header, bank.Id);

        var patched = await TakesTheLockFirstAsync(ledger, "the postings PATCH", db => Transactions(db).PatchAsync(
            ledger.LedgerId, header, new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings { SourceAccountId = bank.Id, Items = Posting(dining.Id, -12m, source) },
            }));

        Assert.Equal(TransactionsRepository.PatchResult.Ok, patched.Result);
        Assert.Equal([dining.Id], await CounterpartiesAsync(header, bank.Id));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_recategorize_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var dining = await ledger.AddCategoryAsync("Dining");
        var header = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -10m, FirstDay)).FromTxnId);

        var moved = await TakesTheLockFirstAsync(ledger, "the recategorize", db => Transactions(db).RecategorizeAsync(
            ledger.LedgerId, header, dining.Id, dryRun: false));

        Assert.Equal(TransactionsRepository.RecategorizeResult.Ok, moved.Result);
        Assert.Equal([dining.Id], await CounterpartiesAsync(header, bank.Id));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_split_posting_recategorize_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var dining = await ledger.AddCategoryAsync("Dining");
        var fuel = await ledger.AddCategoryAsync("Fuel");
        var (_, header) = await ledger.AddMultiSplitAsync(bank.Id, [(groceries.Id, -4m), (fuel.Id, -6m)], FirstDay);

        var moved = await TakesTheLockFirstAsync(ledger, "the split-posting recategorize", db => Transactions(db)
            .RecategorizeSplitPostingsAsync(ledger.LedgerId, header, groceries.Id, dining.Id, dryRun: false));

        Assert.Equal(TransactionsRepository.SplitPostingRecategorizeResult.Ok, moved.Result);
        Assert.Equal(new[] { dining.Id, fuel.Id }.Order(), (await CounterpartiesAsync(header, bank.Id)).Order());
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_hard_delete_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        var header = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, FirstDay)).FromTxnId);
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, SecondDay);

        var outcome = await TakesTheLockFirstAsync(ledger, "the hard delete", db => Transactions(db)
            .DeleteAsync(ledger.LedgerId, header));

        Assert.Equal(TransactionsRepository.DeleteOutcome.HardDeleted, outcome);
        Assert.Null(await HeaderAsync(header));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_soft_delete_takes_the_ledger_write_lock_first()
    {
        // A reminder occurrence is never hard-deleted: its stamp is the slot's key.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var rent = await ledger.AddCategoryAsync("Rent");
        var reminder = await BankReminderAsync(ledger, bank.Id, rent.Id);
        var occurrence = await ReminderCloneFire.PostedAsync(Db, ledger.LedgerId, reminder, Occurrence);

        var outcome = await TakesTheLockFirstAsync(ledger, "the soft delete", db => Transactions(db)
            .DeleteAsync(ledger.LedgerId, occurrence));

        Assert.Equal(TransactionsRepository.DeleteOutcome.SoftHidden, outcome);
        Assert.True((await HeaderAsync(occurrence))!.IsHidden);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    // ----- each path takes the lock first: BulkTransactionsRepository -----

    [Fact]
    public async Task A_bulk_delete_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        var one = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, FirstDay)).FromTxnId);
        var two = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, SecondDay)).FromTxnId);
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -5m, ThirdDay);

        var (hard, soft) = await TakesTheLockFirstAsync(ledger, "the bulk delete", db => Bulk(db).BulkDeleteAsync(
            ledger.LedgerId, new SelectionRequest { Kind = "explicit", HeaderIds = [one, two], AccountId = bank.Id }));

        Assert.Equal((2, 0), (hard, soft));
        Assert.Null(await HeaderAsync(one));
        Assert.Null(await HeaderAsync(two));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task An_undo_import_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        var one = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, FirstDay)).FromTxnId);
        var two = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, SecondDay)).FromTxnId);
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -5m, ThirdDay);
        // The two rows an import wrote, stamped with its operation as the import stamps them.
        var operation = Guid.NewGuid();
        await using (var seed = Db.NewDbContext())
        {
            seed.LedgerOperations.Add(new LedgerOperationRow
            {
                Id = operation, LedgerId = ledger.LedgerId, Family = "ingest", ProviderKey = "csv",
                TriggeredVia = "file-upload", StartedAt = FirstDay,
            });
            await seed.SaveChangesAsync();
            Assert.Equal(2, await seed.TxnHeaders.Where(h => h.Id == one || h.Id == two)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.LedgerOperationId, operation)));
        }

        var undone = await TakesTheLockFirstAsync(ledger, "the undo import", db => Bulk(db)
            .UndoImportAsync(ledger.LedgerId, operation, dryRun: false));

        Assert.Equal((2, 2, false), (undone.Found, undone.Deleted, undone.TooLarge));
        Assert.Null(await HeaderAsync(one));
        Assert.Null(await HeaderAsync(two));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_bulk_unhide_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        var leg = (await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, FirstDay)).FromTxnId;
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, SecondDay);
        await ledger.HideTransactionAsync(leg);
        await ledger.RecomputeBalancesAsync([bank.Id, category.Id]);
        var header = await HeaderOfAsync(ledger, leg);

        var unhidden = await TakesTheLockFirstAsync(ledger, "the bulk unhide", db => Bulk(db).BulkUnhideAsync(
            ledger.LedgerId, new SelectionRequest { Kind = "explicit", HeaderIds = [header], AccountId = bank.Id }));

        Assert.Equal(1, unhidden);
        Assert.False((await HeaderAsync(header))!.IsHidden);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_bulk_move_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Checking");
        var savings = await ledger.AddBankAccountAsync("Savings");
        var category = await ledger.AddCategoryAsync("Groceries");
        var header = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(checking.Id, category.Id, -10m, FirstDay)).FromTxnId);
        await ledger.AddTransactionPairAsync(checking.Id, category.Id, -25m, SecondDay);

        var (outcome, moved) = await TakesTheLockFirstAsync(ledger, "the bulk move", db => Bulk(db).BulkMoveAccountAsync(
            ledger.LedgerId, new SelectionRequest { Kind = "explicit", HeaderIds = [header], AccountId = checking.Id }, savings.Id));

        Assert.Equal((BulkTransactionsRepository.BulkMoveOutcome.Moved, 1), (outcome, moved));
        Assert.Equal([category.Id], await CounterpartiesAsync(header, savings.Id));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    // ----- each path takes the lock first: RemindersRepository -----

    [Fact]
    public async Task An_auto_post_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var rent = await ledger.AddCategoryAsync("Rent");
        var reminder = await BankReminderAsync(ledger, bank.Id, rent.Id);

        var fired = await TakesTheLockFirstAsync(ledger, "the auto-post", db => Reminders(db)
            .FireAsync(ledger.LedgerId, reminder, Occurrence));

        Assert.Equal(RemindersRepository.FireOutcome.Ok, fired.Outcome);
        Assert.Equal(1, await OccurrencesAsync(reminder, Occurrence));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_bank_fire_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var rent = await ledger.AddCategoryAsync("Rent");
        var reminder = await BankReminderAsync(ledger, bank.Id, rent.Id);

        var fired = await TakesTheLockFirstAsync(ledger, "the bank fire", db => Reminders(db).FireBankAsync(
            ledger.LedgerId, reminder, Occurrence,
            new FireBankReminderRequest { OccurrenceDate = Occurrence, SourceAccountId = bank.Id, Postings = Posting(rent.Id, -950m) },
            createdByUserId: ledger.UserId));

        Assert.Equal(RemindersRepository.FireOutcome.Ok, fired.Outcome);
        Assert.Equal(1, await OccurrencesAsync(reminder, Occurrence));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task An_investment_fire_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var (brokerage, security, dividends) = await InvestmentAccountsAsync(ledger);
        var reminder = await InvestmentReminderAsync(ledger, brokerage, security, dividends);

        var fired = await TakesTheLockFirstAsync(ledger, "the investment fire", db => Reminders(db).FireInvestmentAsync(
            ledger.LedgerId, reminder, Occurrence, Dividend(brokerage, security, dividends, 14m, Occurrence),
            createdByUserId: ledger.UserId));

        Assert.Equal(RemindersRepository.FireOutcome.Ok, fired.Outcome);
        Assert.Equal(1, await OccurrencesAsync(reminder, Occurrence));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_bank_reminder_create_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var rent = await ledger.AddCategoryAsync("Rent");

        var created = await TakesTheLockFirstAsync(ledger, "the bank reminder create", db => Reminders(db).CreateBankAsync(
            ledger.LedgerId, new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = Start, Payee = "Landlord",
                SourceAccountId = bank.Id, Postings = Posting(rent.Id, -900m),
            }));

        Assert.Equal(RemindersRepository.CreateOutcome.Ok, created.Outcome);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_managed_loan_reminder_create_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Checking");
        var interest = await ledger.AddCategoryAsync("Mortgage interest", "expense");
        var escrow = await ledger.AddBankAccountAsync("Escrow");
        var loan = Guid.NewGuid();
        await using (var seed = Db.NewDbContext())
        {
            seed.Accounts.Add(new AccountRow
            {
                Id = loan, LedgerId = ledger.LedgerId, Name = "Mortgage", AccountType = "loan",
                CurrencyCode = "USD", OpeningBalance = -200000m, IsActive = true,
            });
            await seed.SaveChangesAsync();
            seed.LoanTerms.Add(new LoanTermsRow
            {
                AccountId = loan, LedgerId = ledger.LedgerId,
                OriginalPrincipal = 250000m, AnnualInterestRate = 4.00m, Points = 0m,
                PaymentCount = 360, PaymentsPerYear = 12,
                EscrowAmount = 300m, InterestAccountId = interest.Id, EscrowAccountId = escrow.Id,
                PaymentIsComputed = true, FixedPayment = null,
            });
            await seed.SaveChangesAsync();
        }

        var created = await TakesTheLockFirstAsync(ledger, "the managed loan reminder create", db => Reminders(db)
            .CreateManagedLoanReminderAsync(ledger.LedgerId, loan, checking.Id, Start));

        Assert.Equal(RemindersRepository.ManagedLoanReminderResult.Ok, created.Result);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task An_investment_reminder_create_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var (brokerage, security, dividends) = await InvestmentAccountsAsync(ledger);

        var created = await TakesTheLockFirstAsync(ledger, "the investment reminder create", db => Reminders(db)
            .CreateInvestmentAsync(ledger.LedgerId, new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = Start,
                Transaction = Dividend(brokerage, security, dividends, 12m, Start),
            }));

        Assert.Equal(RemindersRepository.CreateOutcome.Ok, created.Outcome);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_bank_reminder_edit_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var rent = await ledger.AddCategoryAsync("Rent");
        var utilities = await ledger.AddCategoryAsync("Utilities");
        var reminder = await BankReminderAsync(ledger, bank.Id, rent.Id);

        var edited = await TakesTheLockFirstAsync(ledger, "the bank reminder edit", db => Reminders(db).EditBankAsync(
            ledger.LedgerId, reminder, new EditReminderRequest
            {
                StartDate = Start.AddMonths(1),
                Postings = new PatchReminderPostings { SourceAccountId = bank.Id, Items = Posting(utilities.Id, -120m) },
            }));

        Assert.Equal(RemindersRepository.EditOutcome.Ok, edited.Outcome);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task An_investment_reminder_edit_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var (brokerage, security, dividends) = await InvestmentAccountsAsync(ledger);
        var reminder = await InvestmentReminderAsync(ledger, brokerage, security, dividends);

        var edited = await TakesTheLockFirstAsync(ledger, "the investment reminder edit", db => Reminders(db)
            .EditInvestmentAsync(ledger.LedgerId, reminder, new EditInvestmentReminderRequest
            {
                StartDate = Start.AddMonths(1),
                Transaction = Dividend(brokerage, security, dividends, 15m, Start.AddMonths(1)),
            }));

        Assert.Equal(RemindersRepository.EditOutcome.Ok, edited.Outcome);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    // ----- the interleavings that deadlocked before the lock -----

    /// <summary>
    /// The balances repair sent while a hard delete waits between its cascade, which
    /// row-locks the deleted header's stored balances, and its recompute of the same
    /// accounts: the delete took the ledger write lock first, so the repair waits for it at
    /// the start of its recompute, holding nothing. Both succeed.
    /// </summary>
    [Fact]
    public async Task A_hard_delete_beside_the_balances_repair_takes_turns_with_it()
    {
        await using var w = await WorldAsync();
        var bank = await w.Ledger.AddBankAccountAsync("Checking");
        var category = await w.Ledger.AddCategoryAsync("Groceries");
        var doomed = await HeaderOfAsync(w.Ledger, (await w.Ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, FirstDay)).FromTxnId);
        await w.Ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, ThirdDay);
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        Task<HttpResponseMessage>? repairing = null;
        var waited = false;
        var hold = new BeforeNthCommand("recompute_balances_for_account", 1, async () =>
        {
            repairing = RepairAsync(w, ConsistencyProjections.Balances);
            await WaitForLockWaitersOrDoneAsync(1, repairing);
            waited = !repairing.IsCompleted;
        });
        TransactionsRepository.DeleteOutcome outcome;
        try
        {
            await using var deleter = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold);
            outcome = await NoDeadlockAsync(Transactions(deleter).DeleteAsync(w.Ledger.LedgerId, doomed), "the hard delete");
        }
        finally
        {
            await DrainAsync(repairing);
        }

        Assert.True(hold.Fired, "the repair was never started, so nothing was tested");
        using var repaired = await NoDeadlockAsync(repairing!, "the repair");
        Assert.True(waited, "the repair did not wait for the delete in flight");
        Assert.Equal(TransactionsRepository.DeleteOutcome.HardDeleted, outcome);
        Assert.True(repaired.StatusCode == HttpStatusCode.OK,
            $"the repair answered {(int)repaired.StatusCode}: {await repaired.Content.ReadAsStringAsync()}");
        Assert.Null(await HeaderAsync(doomed));
        await AssertHealthyAsync(w.Ledger.LedgerId);
    }

    /// <summary>
    /// A spend from the same account saved while a hard delete waits between its cascade
    /// and its recompute, dated before the deleted transaction so its own recompute rewrites
    /// the rows the cascade holds: the save waits for the delete at its first statement,
    /// holding nothing. Both succeed.
    /// </summary>
    [Fact]
    public async Task A_hard_delete_beside_a_save_of_the_same_account_takes_turns_with_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        var doomed = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, SecondDay)).FromTxnId);
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, ThirdDay);

        await using var saver = Db.NewRequestDbContextAsUser(ledger.UserId);
        await saver.Database.OpenConnectionAsync();
        var saverPid = Pid(saver);
        Task<TransactionsRepository.CreateOutcome>? saving = null;
        var waited = false;
        var hold = new BeforeNthCommand("recompute_balances_for_account", 1, async () =>
        {
            saving = Transactions(saver).CreateAsync(
                ledger.LedgerId, bank.Id, FirstDay, "Payee B", checkNumber: null, transactedAt: null,
                Posting(category.Id, -5m), notes: null);
            await WaitForLockWaitersOrDoneAsync(1, saving, saverPid);
            waited = !saving.IsCompleted;
        });
        TransactionsRepository.DeleteOutcome outcome;
        try
        {
            await using var deleter = Db.NewRequestDbContextAsUser(ledger.UserId, hold);
            outcome = await NoDeadlockAsync(Transactions(deleter).DeleteAsync(ledger.LedgerId, doomed), "the hard delete");
        }
        finally
        {
            await DrainAsync(saving);
        }

        Assert.True(hold.Fired, "the save was never started, so nothing was tested");
        var created = await NoDeadlockAsync(saving!, "the save");
        Assert.True(waited, "the save did not wait for the delete in flight");
        Assert.Equal(TransactionsRepository.DeleteOutcome.HardDeleted, outcome);
        Assert.Null(created.Refusal);
        Assert.Null(await HeaderAsync(doomed));
        Assert.NotNull(await HeaderAsync(created.HeaderId));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    /// <summary>
    /// A postings PATCH saves in two steps (its legs' indexes shifted, then the reshape),
    /// recomputing after each. A second PATCH, whose new counterparty is the first one's
    /// current account and sorts before every account of its own transaction, is sent
    /// between the two steps: it waits for the first at its first statement, holding
    /// nothing, rather than taking the account the first's second step needs. Both succeed.
    /// </summary>
    [Fact]
    public async Task A_two_step_postings_patch_beside_a_patch_whose_new_counterparty_sorts_first_takes_turns_with_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Checking");
        var savings = await ledger.AddBankAccountAsync("Savings");
        var dining = await ledger.AddCategoryAsync("Dining");
        // The first PATCH's current counterparty, which the second makes its own: an
        // account that sorts before every account of the second's transaction.
        var groceries = await ledger.AddCategoryAsync("Groceries");
        for (var i = 0; groceries.Id.CompareTo(savings.Id) > 0 || groceries.Id.CompareTo(dining.Id) > 0; i++)
        {
            Assert.True(i < 64, "no category sorted first in 64 tries");
            groceries = await ledger.AddCategoryAsync($"Groceries {i}");
        }
        var first = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(checking.Id, groceries.Id, -10m, FirstDay)).FromTxnId);
        var second = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(savings.Id, dining.Id, -25m, SecondDay)).FromTxnId);
        var firstSource = await LegOnAsync(first, checking.Id);
        var secondSource = await LegOnAsync(second, savings.Id);

        await using var secondDb = Db.NewRequestDbContextAsUser(ledger.UserId);
        await secondDb.Database.OpenConnectionAsync();
        var secondPid = Pid(secondDb);
        Task<TransactionsRepository.PatchOutcome>? patchingSecond = null;
        var waited = false;
        // The first step recomputes the two accounts its kept legs sit on; the third
        // recompute is the second step's first.
        var hold = new BeforeNthCommand("recompute_balances_for_account", 3, async () =>
        {
            patchingSecond = Transactions(secondDb).PatchAsync(ledger.LedgerId, second, new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings { SourceAccountId = savings.Id, Items = Posting(groceries.Id, -25m, secondSource) },
            });
            await WaitForLockWaitersOrDoneAsync(1, patchingSecond, secondPid);
            waited = !patchingSecond.IsCompleted;
        });
        TransactionsRepository.PatchOutcome firstPatched;
        try
        {
            await using var firstDb = Db.NewRequestDbContextAsUser(ledger.UserId, hold);
            firstPatched = await NoDeadlockAsync(Transactions(firstDb).PatchAsync(ledger.LedgerId, first, new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings { SourceAccountId = checking.Id, Items = Posting(dining.Id, -10m, firstSource) },
            }), "the first PATCH");
        }
        finally
        {
            await DrainAsync(patchingSecond);
        }

        Assert.True(hold.Fired, "the second PATCH was never started between the first's two steps, so nothing was tested");
        var secondPatched = await NoDeadlockAsync(patchingSecond!, "the second PATCH");
        Assert.True(waited, "the second PATCH did not wait for the first in flight");
        Assert.Equal(TransactionsRepository.PatchResult.Ok, firstPatched.Result);
        Assert.Equal(TransactionsRepository.PatchResult.Ok, secondPatched.Result);
        Assert.Equal([dining.Id], await CounterpartiesAsync(first, checking.Id));
        Assert.Equal([groceries.Id], await CounterpartiesAsync(second, savings.Id));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    /// <summary>
    /// The auto-post of an occurrence sent while a person's post of the same occurrence
    /// holds both its locks and has not yet created the transaction: every fire takes the
    /// ledger write lock before the slot lock, so the auto-post waits at the ledger lock,
    /// holding neither, and once the person's post commits it finds the occurrence posted
    /// and answers with it. One occurrence is stored.
    /// </summary>
    [Fact]
    public async Task An_auto_post_beside_a_persons_post_of_the_same_occurrence_takes_the_locks_in_the_same_order()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var rent = await ledger.AddCategoryAsync("Rent");
        var reminder = await BankReminderAsync(ledger, bank.Id, rent.Id);

        // The auto-post job's context: the production-wired service factory.
        await using var jobDb = Db.NewServiceFactory().Create();
        await jobDb.Database.OpenConnectionAsync();
        var jobPid = Pid(jobDb);
        Task<RemindersRepository.FireResult>? autoPosting = null;
        var waited = false;
        // The series read comes once the person's post holds both locks, before its create.
        var hold = new BeforeNthCommand("recurring_transactions", 1, async () =>
        {
            autoPosting = Reminders(jobDb).FireAsync(ledger.LedgerId, reminder, Occurrence);
            await WaitForLockWaitersOrDoneAsync(1, autoPosting, jobPid);
            waited = !autoPosting.IsCompleted;
        });
        RemindersRepository.FireResult posted;
        try
        {
            await using var personDb = Db.NewRequestDbContextAsUser(ledger.UserId, hold);
            posted = await NoDeadlockAsync(Reminders(personDb).FireBankAsync(
                ledger.LedgerId, reminder, Occurrence,
                new FireBankReminderRequest { OccurrenceDate = Occurrence, SourceAccountId = bank.Id, Postings = Posting(rent.Id, -950m) },
                createdByUserId: ledger.UserId), "the person's post");
        }
        finally
        {
            await DrainAsync(autoPosting);
        }

        Assert.True(hold.Fired, "the auto-post was never started while the person's post held its locks, so nothing was tested");
        var autoPosted = await NoDeadlockAsync(autoPosting!, "the auto-post");
        Assert.True(waited, "the auto-post did not wait for the person's post in flight");
        Assert.Equal(RemindersRepository.FireOutcome.Ok, posted.Outcome);
        Assert.Equal(RemindersRepository.FireOutcome.Ok, autoPosted.Outcome);
        Assert.Equal(posted.HeaderId, autoPosted.HeaderId);
        Assert.Equal(1, await OccurrencesAsync(reminder, Occurrence));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    // ----- writers that recompute nothing but row-lock what a holder of the lock needs -----

    /// <summary>What a session of this test's database that waits on a lock
    /// (<paramref name="pid"/>'s) waits on: <c>advisory</c> at the ledger write lock,
    /// <c>transactionid</c> or <c>tuple</c> at a row; null when it waits on none. Asked as
    /// the superuser, as <see cref="WaitForLockWaitersOrDoneAsync"/> asks.</summary>
    private async Task<string?> WaitEventAsync(int pid)
    {
        await using var probe = new NpgsqlConnection(Db.SuperuserConnectionString);
        await probe.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT wait_event FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock'", probe);
        cmd.Parameters.AddWithValue("pid", pid);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>Runs <c>action</c> once, just before the first command whose text
    /// <paramref name="matches"/> accepts is sent, as a reader or a non-query.</summary>
    private sealed class BeforeFirstCommand(Func<string, bool> matches, Func<Task> action) : DbCommandInterceptor
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
            if (matches(command.CommandText) && Interlocked.Exchange(ref _fired, 1) == 0) await action();
        }
    }

    /// <summary>A transaction carrying <paramref name="tag"/> on its header: the header and the tag.</summary>
    private static async Task<(Guid Header, Guid Tag)> TaggedTransactionAsync(
        SyntheticLedger ledger, Guid bank, Guid category, string tag, DateTime day)
    {
        var header = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank, category, -10m, day)).FromTxnId);
        return (header, await ledger.AddTagAsync(header, tag));
    }

    [Fact]
    public async Task A_tag_merge_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        var (header, source) = await TaggedTransactionAsync(ledger, bank.Id, category.Id, "t-source", FirstDay);
        var target = await ledger.AddBareTagAsync("t-target");
        await using var read = Db.NewDbContext();

        var merged = await TakesTheLockFirstAsync(ledger, "the tag merge",
            db => new TagsRepository(db).MergeAsync(ledger.LedgerId, source, target));

        Assert.Equal(TagsRepository.MergeTagResult.Ok, merged.Result);
        Assert.True(await read.TxnHeaderTags.AsNoTracking().AnyAsync(t => t.HeaderId == header && t.TagId == target));
        Assert.False(await read.Tags.AsNoTracking().AnyAsync(t => t.Id == source));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_tag_delete_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        var (header, tag) = await TaggedTransactionAsync(ledger, bank.Id, category.Id, "t-gone", FirstDay);
        await using var read = Db.NewDbContext();

        var deleted = await TakesTheLockFirstAsync(ledger, "the tag delete",
            db => new TagsRepository(db).DeleteAsync(ledger.LedgerId, tag));

        Assert.Equal(TagsRepository.DeleteTagResult.Ok, deleted);
        Assert.False(await read.TxnHeaderTags.AsNoTracking().AnyAsync(t => t.HeaderId == header));
        await AssertHealthyAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_tag_cleanup_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var unused = await ledger.AddBareTagAsync("t-unused");

        var removed = await TakesTheLockFirstAsync(ledger, "the tag clean-up",
            db => new TagsRepository(db).CleanupUnusedAsync(ledger.LedgerId));

        Assert.Equal(1, removed);
        await using var read = Db.NewDbContext();
        Assert.False(await read.Tags.AsNoTracking().AnyAsync(t => t.Id == unused));
    }

    [Fact]
    public async Task A_budget_fill_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var month = new DateOnly(2026, 5, 1);

        var outcomes = await TakesTheLockFirstAsync(ledger, "the budget fill",
            db => new BudgetTargetsRepository(db, TimeProvider.System).SetManyAsync(
                ledger.LedgerId, month, [(groceries.Id, 120m)]));

        Assert.Equal(BudgetTargetsRepository.SetResult.Ok, Assert.Single(outcomes).Result);
        await using var read = Db.NewDbContext();
        Assert.Equal(120m, await read.BudgetTargets.AsNoTracking()
            .Where(t => t.CategoryId == groceries.Id && t.TargetMonth == month).Select(t => t.Amount).SingleAsync());
    }

    [Fact]
    public async Task A_feed_connection_delete_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        FeedConnectionRow connection;
        await using (var seed = Db.NewDbContext())
            connection = await new FeedConnectionsRepository(seed).CreateSimpleFinAsync(ledger.LedgerId, ledger.UserId, [1, 2, 3], "Bank A");

        var deleted = await TakesTheLockFirstAsync(ledger, "the feed connection delete",
            db => new FeedConnectionsRepository(db).DeleteAsync(ledger.LedgerId, connection.Id));

        Assert.Equal(1, deleted);
        await using var read = Db.NewDbContext();
        Assert.False(await read.FeedConnections.AsNoTracking().AnyAsync(c => c.Id == connection.Id));
    }

    [Fact]
    public async Task A_ledger_delete_takes_the_ledger_write_lock_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -10m, FirstDay);

        await TakesTheLockFirstOnAServiceContextAsync(ledger, "the ledger delete", async factory =>
        {
            await using var db = Db.NewRequestDbContextAsUser(ledger.UserId);
            await new LedgersRepository(db, factory, Db.NewLedgerKeyService(), TimeProvider.System)
                .DeleteAsync(ledger.LedgerId);
            return true;
        });

        await using var read = Db.NewDbContext();
        Assert.False(await read.Ledgers.AsNoTracking().AnyAsync(l => l.Id == ledger.LedgerId));
    }

    /// <summary>
    /// A tag merge sent while a PATCH that tags a transaction with the merge's source is
    /// about to write that link: the merge waits for the PATCH at the ledger write lock,
    /// before it locks the source, then moves the PATCH's link with the others. Neither is a
    /// deadlock victim, and neither fails.
    /// </summary>
    /// <remarks>
    /// Without the lock on the merge, the merge locks the source and the headers it
    /// recomputes while the PATCH's link waits for the lock on the source: a cycle when the
    /// PATCH holds one of those headers, and otherwise a link written to a tag the merge has
    /// deleted.
    /// </remarks>
    [Fact]
    public async Task A_tag_merge_sent_while_a_patch_tags_with_its_source_waits_for_it_at_the_ledger_write_lock()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        var (tagged, source) = await TaggedTransactionAsync(ledger, bank.Id, category.Id, "t-source", FirstDay);
        var target = await ledger.AddBareTagAsync("t-target");
        var patched = await HeaderOfAsync(ledger, (await ledger.AddTransactionPairAsync(bank.Id, category.Id, -20m, SecondDay)).FromTxnId);

        Task<TagsRepository.MergeTagOutcome>? merging = null;
        var waited = false;
        string? waitedAt = null;
        await using var mergeDb = Db.NewRequestDbContextAsUser(ledger.UserId);
        await mergeDb.Database.OpenConnectionAsync();
        var pid = Pid(mergeDb);
        // Just before the PATCH's save, which writes its link to the source (at the leg or
        // the header), holding the ledger's lock: the first insert that names a tag link.
        var hold = new BeforeFirstCommand(
            sql => sql.Contains("INSERT INTO", StringComparison.Ordinal)
                   && (sql.Contains("txn_leg_tags", StringComparison.Ordinal)
                       || sql.Contains("txn_header_tags", StringComparison.Ordinal)),
            async () =>
            {
                merging = new TagsRepository(mergeDb).MergeAsync(ledger.LedgerId, source, target);
                await WaitForLockWaitersOrDoneAsync(1, merging, pid);
                waited = !merging.IsCompleted;
                waitedAt = await WaitEventAsync(pid);
            });
        TransactionsRepository.PatchOutcome patchedOutcome;
        try
        {
            await using var patchDb = Db.NewRequestDbContextAsUser(ledger.UserId, hold);
            patchedOutcome = await NoDeadlockAsync(Transactions(patchDb).PatchAsync(ledger.LedgerId, patched,
                new PatchTransactionRequest { Notes = new TransactionNotesRequest { Side = bank.Id, Tags = ["t-source"] } }),
                "the PATCH");
        }
        finally
        {
            await DrainAsync(merging);
        }

        Assert.True(hold.Fired, "the merge was never sent while the PATCH was writing its link, so nothing was tested");
        var merged = await NoDeadlockAsync(merging!, "the merge");
        Assert.Equal(TransactionsRepository.PatchResult.Ok, patchedOutcome.Result);
        Assert.Equal(TagsRepository.MergeTagResult.Ok, merged.Result);
        Assert.True(waited, "the merge did not wait for the PATCH in flight");
        Assert.Equal("advisory", waitedAt);

        // Both transactions carry the target, at whichever level each was tagged, and the
        // source is gone.
        await using (var read = Db.NewDbContext())
        {
            Assert.False(await read.Tags.AsNoTracking().AnyAsync(t => t.Id == source));
            foreach (var header in new[] { tagged, patched })
            {
                var onHeader = await read.TxnHeaderTags.AsNoTracking().AnyAsync(t => t.HeaderId == header && t.TagId == target);
                var onLeg = await (from lt in read.TxnLegTags.AsNoTracking()
                                   join l in read.TxnLegs.AsNoTracking() on lt.LegId equals l.Id
                                   where l.HeaderId == header && lt.TagId == target
                                   select lt).AnyAsync();
                Assert.True(onHeader || onLeg, $"transaction {header} does not carry the merge's target");
            }
        }
        await AssertHealthyAsync(ledger.LedgerId);
    }
}
