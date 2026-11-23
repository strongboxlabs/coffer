using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

using Coffer.Api.Auth;
using Coffer.Api.Configuration;
using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Ingest;
using Coffer.Api.Ingest.Qif;
using Coffer.Api.Mcp;
using Coffer.Api.Notifications;
using Coffer.Api.Quotes;
using Coffer.Api.Reminders;
using Coffer.Api.Scheduling;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;
using Coffer.Domain.Investment;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// The ledger write lock (migration 275) on the account edits, the ingest runs, the
/// consistency repairs, MCP and the scheduled writers: every transaction that can
/// recompute a ledger's balances or holdings takes it first, so two of them take turns
/// and neither waits on the other's rows, and a write that finds it held past the wait is
/// refused as a business answer that loses nothing.
/// </summary>
/// <remarks>
/// <para>Every write two tests run side by side is awaited through
/// <see cref="NoDeadlockAsync{T}"/>, so a deadlock fails the test by name, and every such
/// test ends on the stored state a fresh walk derives (<see cref="AssertHealthyAsync"/>) or
/// the figures it names.</para>
/// <para>A busy refusal is provoked by holding the ledger's lock in another session while
/// the write runs on a context whose command timeout is short: the lock's wait is half the
/// command timeout (<see cref="LedgerWriteLock"/>), so a two-second timeout refuses after one
/// second.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerWriteLockAccountsRepairsTests : DatabaseTest
{
    private static readonly DateTime FirstDay = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondDay = new(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ThirdDay = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The command timeout, in seconds, of the contexts a busy refusal is provoked
    /// on: the lock waits half of it.</summary>
    private const int ShortCommandTimeoutSeconds = 2;

    public LedgerWriteLockAccountsRepairsTests(PostgresFixture fixture, ITestOutputHelper output)
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

    private static string WithShortCommandTimeout(string connectionString) =>
        $"{connectionString};Command Timeout={ShortCommandTimeoutSeconds}";

    /// <summary>A ledger and a host on this test's database with a session for its owner;
    /// with <paramref name="shortCommandTimeout"/>, the host's request contexts run with
    /// the short command timeout, and with <paramref name="interceptor"/>, they run it.</summary>
    private async Task<World> WorldAsync(bool shortCommandTimeout = false, IInterceptor? interceptor = null)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var factory = new ApiFactory(Db).WithoutDevAuth();
        if (shortCommandTimeout)
            factory = factory.WithConfig("Api:ConnectionString", WithShortCommandTimeout(Db.AppConnectionString));
        if (interceptor is not null)
            factory = factory.WithServices(s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(interceptor)));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        return new World(ledger, factory, client);
    }

    /// <summary>A service-context factory wired as production's, whose contexts run with the
    /// short command timeout, and with <paramref name="extra"/> after production's own
    /// interceptors.</summary>
    private ServiceDbContextFactory ShortTimeoutServiceFactory(params IInterceptor[] extra) =>
        PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
        {
            ConnectionString = Db.AppConnectionString,
            ServiceConnectionString = WithShortCommandTimeout(Db.ServiceConnectionString),
        }), extra);

    /// <summary>What <paramref name="pid"/>'s session waits on, when it waits on a lock:
    /// <c>advisory</c> at the ledger write lock, <c>transactionid</c> or <c>tuple</c> at a
    /// row; null when it waits on none. Asked as the superuser, as
    /// <see cref="WaitForLockWaitersOrDoneAsync"/> asks.</summary>
    private async Task<string?> WaitEventAsync(int pid)
    {
        await using var probe = new NpgsqlConnection(Db.SuperuserConnectionString);
        await probe.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT wait_event FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock'", probe);
        cmd.Parameters.AddWithValue("pid", pid);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>Take <paramref name="ledgerId"/>'s write lock in a session of its own and
    /// hold it until the returned scope is disposed (which rolls back).</summary>
    private async Task<(AppDbContext Db, LedgerWriteScope Held)> HoldLedgerAsync(Guid ledgerId)
    {
        var holder = Db.NewDbContext();
        return (holder, await holder.BeginLedgerWriteAsync(ledgerId));
    }

    private static Task<HttpResponseMessage> RepairAsync(World w, string projection) =>
        w.Client.PostAsync($"/api/ledgers/{w.Ledger.LedgerId}/balances/consistency/{projection}/repair", content: null);

    private static Task<HttpResponseMessage> EditAccountAsync(World w, Guid accountId, UpdateAccountRequest request) =>
        w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/accounts/{accountId}", request);

    /// <summary>A spend from <paramref name="from"/> to <paramref name="to"/>, saved through a
    /// context wired as production's (its interceptors recompute), with the ledger's write
    /// lock taken first.</summary>
    private async Task<Guid> SeedSpendAsync(Guid ledgerId, Guid from, Guid to, DateTime day, decimal amount)
    {
        var headerId = Guid.NewGuid();
        await using var db = Db.NewServiceFactory().Create();
        var header = TxnHeaderRow.NewTransaction(headerId, ledgerId, from);
        header.Payee = "Payee A";
        header.PostedAt = day;
        header.TransactedAt = day;
        db.AddHeader(header, createdAt: day);
        db.Add(new TxnLegRow { Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId, AccountId = from, PostingIndex = 0, Amount = amount });
        db.Add(new TxnLegRow { Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId, AccountId = to, PostingIndex = 0, Amount = -amount });
        await db.SaveChangesUnderLedgerLockAsync(ledgerId);
        return headerId;
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

    /// <summary>A trade saved in <paramref name="db"/>'s transaction, which took the ledger's
    /// write lock and which the caller holds open.</summary>
    private static async Task TradeInAsync(AppDbContext db, Guid ledgerId, CreateInvestmentTransactionRequest trade)
    {
        var made = await new InvestmentTransactionsRepository(db, TimeProvider.System).CreateAsync(ledgerId, trade);
        Assert.Null(made.Failure);
        Assert.Null(made.Refusal);
    }

    /// <summary>A trade saved and committed in a transaction of its own that takes the
    /// ledger's write lock first.</summary>
    private async Task TradedAsync(World w, CreateInvestmentTransactionRequest trade)
    {
        await using var db = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await using var write = await db.BeginLedgerWriteAsync(w.Ledger.LedgerId);
        await TradeInAsync(db, w.Ledger.LedgerId, trade);
        await write.CommitAsync();
    }

    private LedgerConsistencyRepository Consistency(AppDbContext db) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db), TimeProvider.System);

    private async Task<LedgerConsistencyReport> CheckAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await Consistency(db).CheckAsync(ledgerId);
    }

    private static string Findings(LedgerConsistencyReport report) => string.Join(" | ",
        report.Projections.Where(p => !p.Healthy).Select(p => p.Projection + ": " + string.Join("; ",
            p.Mismatches.Select(m => $"{m.Scope}: {m.Field} stored {m.Stored}, expected {m.Expected}"))));

    /// <summary>Every projection healthy: what is stored is what a fresh walk derives.</summary>
    private async Task AssertHealthyAsync(Guid ledgerId)
    {
        var report = await CheckAsync(ledgerId);
        Assert.True(report.Healthy, "findings: " + Findings(report));
    }

    private async Task<bool> ProjectionHealthyAsync(Guid ledgerId, string projection) =>
        Assert.Single((await CheckAsync(ledgerId)).Projections, p => p.Projection == projection).Healthy;

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

    /// <summary>A repair's answer, failing the test by name when it was a deadlock.</summary>
    private static async Task AssertRepairedAsync(HttpResponseMessage repaired)
    {
        var body = await repaired.Content.ReadAsStringAsync();
        Assert.False(body.Contains(PostgresErrorCodes.DeadlockDetected, StringComparison.Ordinal),
            "the repair was chosen as a deadlock victim: " + body);
        Assert.True(repaired.StatusCode == HttpStatusCode.OK, $"the repair answered {(int)repaired.StatusCode}: {body}");
    }

    /// <summary>The busy refusal as the API answers it: a 422 with code
    /// <c>ledger-write-busy</c> and <paramref name="detail"/>, by default the sentence every
    /// busy refusal but a repair's reads.</summary>
    private static async Task AssertBusyAsync(
        HttpResponseMessage response, string what, string detail = LedgerWriteLock.BusyDetail)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"{what} answered {(int)response.StatusCode}, not the busy refusal: {body}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(BusinessError.Codes.LedgerWriteBusy, problem.GetProperty("code").GetString());
        Assert.Equal(detail, problem.GetProperty("detail").GetString());
    }

    /// <summary>Once armed, runs its action just before the <paramref name="nth"/> command
    /// whose text contains <paramref name="marker"/> is sent, counting from the arming.</summary>
    private sealed class ArmedBeforeNthCommand(string marker, int nth) : DbCommandInterceptor
    {
        private Func<Task>? _action;
        private int _seen;
        private int _fired;

        public bool Fired => _fired == 1;

        public void Arm(Func<Task> action) => _action = action;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_action is { } action
                && command.CommandText.Contains(marker, StringComparison.Ordinal)
                && Interlocked.Increment(ref _seen) == nth
                && Interlocked.Exchange(ref _fired, 1) == 0)
                await action();
            return result;
        }
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

    /// <summary>Fails the first command whose text contains <paramref name="marker"/>, before
    /// it is sent.</summary>
    private sealed class FailCommand(string marker) : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => _fired == 1;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
                throw new InvalidOperationException($"The command naming {marker} failed (injected).");
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Once armed, fails the first command whose text contains
    /// <paramref name="marker"/>, before it is sent.</summary>
    private sealed class ArmedFailCommand(string marker) : DbCommandInterceptor
    {
        private int _armed;
        private int _fired;

        public bool Fired => _fired == 1;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed == 1
                && command.CommandText.Contains(marker, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
                throw new InvalidOperationException($"The command naming {marker} failed (injected).");
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Runs <c>action</c> once, just before the first UPDATE of the accounts table
    /// is sent; an ExecuteUpdate sends it as a non-query.</summary>
    private sealed class BeforeAccountUpdate(Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var text = command.CommandText.TrimStart();
            if ((text.StartsWith("UPDATE accounts ", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("UPDATE \"accounts\" ", StringComparison.OrdinalIgnoreCase))
                && Interlocked.Exchange(ref _fired, 1) == 0)
                await action();
            return result;
        }
    }

    private static AccountsRepository AccountsOf(AppDbContext db) =>
        new(db, new LegDerivedRecomputeService(db), TimeProvider.System);

    // ----- the account edits -----

    /// <summary>
    /// The active flag flipped while an edit of the account's notes is between its read of
    /// the account and its update: the flip takes no ledger write lock, so it commits in
    /// between, and the edit, which does not name the flag, leaves it as the flip left it.
    /// </summary>
    [Fact]
    public async Task An_account_edit_that_does_not_name_the_active_flag_keeps_a_flip_committed_since_it_read()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");

        var flip = new BeforeAccountUpdate(async () =>
        {
            await using var flipDb = Db.NewRequestDbContextAsUser(ledger.UserId);
            Assert.Equal(AccountsRepository.SetIsActiveResult.Ok,
                await AccountsOf(flipDb).SetIsActiveAsync(ledger.LedgerId, bank.Id, isActive: false));
        });
        await using (var edit = Db.NewRequestDbContextAsUser(ledger.UserId, flip))
        {
            Assert.Equal(AccountsRepository.UpdateAccountResult.Ok, await AccountsOf(edit).UpdateAsync(
                ledger.LedgerId, bank.Id, new UpdateAccountRequest { Notes = "edited" }));
        }
        Assert.True(flip.Fired, "the flip never ran between the edit's read and its update, so nothing was tested");

        await using var read = Db.NewDbContext();
        var account = await read.Accounts.AsNoTracking().SingleAsync(a => a.Id == bank.Id);
        Assert.Equal(("edited", false), (account.Notes, account.IsActive));
    }

    /// <summary>
    /// A category merge and the balances repair sent while the merge is about to recompute:
    /// the merge took the ledger's lock before it locked the source's row, so the repair
    /// waits for the whole merge rather than for that row, and neither is a deadlock victim.
    /// </summary>
    [Fact]
    public async Task A_category_merge_and_the_balances_repair_take_turns_and_neither_is_a_deadlock_victim()
    {
        await using var w = await WorldAsync();
        var bank = await w.Ledger.AddBankAccountAsync("Checking");
        var source = await w.Ledger.AddCategoryAsync("Category S");
        var target = await w.Ledger.AddCategoryAsync("Category T");
        await SeedSpendAsync(w.Ledger.LedgerId, bank.Id, source.Id, FirstDay, -10m);
        await SeedSpendAsync(w.Ledger.LedgerId, bank.Id, target.Id, ThirdDay, -25m);
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        Task<HttpResponseMessage>? repairing = null;
        var waited = false;
        var hold = new BeforeNthCommand("recompute_balances_for_account", 1, async () =>
        {
            repairing = RepairAsync(w, ConsistencyProjections.Balances);
            await WaitForLockWaitersOrDoneAsync(1, repairing);
            waited = !repairing.IsCompleted;
        });
        AccountsRepository.MergeCategoryOutcome merged;
        await using (var merger = Db.NewRequestDbContextAsUser(w.Ledger.UserId, hold))
        {
            merged = await NoDeadlockAsync(
                new AccountsRepository(merger, new LegDerivedRecomputeService(merger), TimeProvider.System)
                    .MergeCategoryAsync(w.Ledger.LedgerId, source.Id, target.Id, dryRun: false),
                "the merge");
        }

        Assert.True(hold.Fired, "the repair was never started while the merge was in flight, so nothing was tested");
        using var repaired = await NoDeadlockAsync(repairing!, "the repair");
        Assert.True(waited, "the repair did not wait for the merge in flight, so nothing was tested");
        Assert.Equal(AccountsRepository.MergeCategoryResult.Ok, merged.Result);
        await AssertRepairedAsync(repaired);
        await AssertHealthyAsync(w.Ledger.LedgerId);
    }

    /// <summary>
    /// An opening-balance edit of a brokerage sent while a trade on it is saved and not yet
    /// committed: the edit waits for the ledger's lock, so it never holds the brokerage's
    /// row while the trade holds what its recompute needs. Both succeed.
    /// </summary>
    [Fact]
    public async Task An_opening_balance_edit_sent_while_a_trade_is_saved_waits_for_it()
    {
        await using var w = await WorldAsync();
        var securityId = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var brokerage = await BrokerageAsync(w, "Brokerage R");
        await TradedAsync(w, Trade(brokerage, securityId, "buy", 10m, 10m, FirstDay));
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        await using var trader = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await using var held = await trader.BeginLedgerWriteAsync(w.Ledger.LedgerId);
        await TradeInAsync(trader, w.Ledger.LedgerId, Trade(brokerage, securityId, "sell", -3m, 12m, SecondDay));

        var editing = EditAccountAsync(w, brokerage, new UpdateAccountRequest { OpeningBalance = 500m });
        await WaitForLockWaitersOrDoneAsync(1, editing);
        Assert.False(editing.IsCompleted, "the edit did not wait for the trade in flight");

        await held.CommitAsync();
        using var edited = await NoDeadlockAsync(editing, "the edit");

        Assert.True(edited.StatusCode == HttpStatusCode.OK,
            $"the edit answered {(int)edited.StatusCode}: {await edited.Content.ReadAsStringAsync()}");
        await AssertHealthyAsync(w.Ledger.LedgerId);
    }

    /// <summary>
    /// An edit that changes only an account's currency takes the ledger's lock too: it waits
    /// for a write holding it, and then is saved.
    /// </summary>
    [Fact]
    public async Task A_currency_only_edit_waits_for_a_write_holding_the_ledgers_lock()
    {
        await using var w = await WorldAsync();
        var bank = await w.Ledger.AddBankAccountAsync("Checking");
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        var (holder, held) = await HoldLedgerAsync(w.Ledger.LedgerId);
        await using (holder)
        {
            var editing = EditAccountAsync(w, bank.Id, new UpdateAccountRequest { CurrencyCode = "EUR" });
            await WaitForLockWaitersOrDoneAsync(1, editing);
            var waited = !editing.IsCompleted;
            await held.DisposeAsync();
            using var edited = await editing;

            Assert.True(waited, "the currency edit did not wait for the write holding the ledger's lock");
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        }
        await using var read = Db.NewDbContext();
        Assert.Equal("EUR", await read.Accounts.AsNoTracking().Where(a => a.Id == bank.Id)
            .Select(a => a.CurrencyCode).SingleAsync());
    }

    /// <summary>
    /// The trade-commission flip takes the ledger's lock before it writes the flag: it waits
    /// for a write holding it, and then is saved with its holdings recomputed.
    /// </summary>
    [Fact]
    public async Task A_trade_commission_flip_waits_for_a_write_holding_the_ledgers_lock()
    {
        await using var w = await WorldAsync();
        var securityId = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var brokerage = await BrokerageAsync(w, "Brokerage R");
        await TradedAsync(w, Trade(brokerage, securityId, "buy", 10m, 10m, FirstDay));
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        var (holder, held) = await HoldLedgerAsync(w.Ledger.LedgerId);
        await using (holder)
        {
            var flipping = w.Client.PatchAsJsonAsync(
                $"/api/ledgers/{w.Ledger.LedgerId}/accounts/{brokerage}/trade-commission",
                new PatchAccountTradeCommissionRequest { Enabled = true });
            await WaitForLockWaitersOrDoneAsync(1, flipping);
            var waited = !flipping.IsCompleted;
            await held.DisposeAsync();
            using var flipped = await flipping;

            Assert.True(waited, "the flip did not wait for the write holding the ledger's lock");
            Assert.True(flipped.StatusCode == HttpStatusCode.NoContent,
                $"the flip answered {(int)flipped.StatusCode}: {await flipped.Content.ReadAsStringAsync()}");
        }
        await using (var read = Db.NewDbContext())
        {
            Assert.True(await read.Accounts.AsNoTracking().Where(a => a.Id == brokerage)
                .Select(a => a.IsTradeCommission).SingleAsync());
        }
        await AssertHealthyAsync(w.Ledger.LedgerId);
    }

    /// <summary>
    /// The trade-commission flip writes the flag and recomputes the brokerage's holdings in
    /// one transaction: a recompute that fails leaves the flag as it was.
    /// </summary>
    [Fact]
    public async Task A_trade_commission_flip_whose_recompute_fails_leaves_the_flag_as_it_was()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        Guid brokerage;
        await using (var setup = Db.NewRequestDbContextAsUser(ledger.UserId))
        {
            var created = await new AccountsRepository(setup, new LegDerivedRecomputeService(setup), TimeProvider.System)
                .CreateAsync(ledger.LedgerId, new CreateAccountRequest
                {
                    Name = "Brokerage R", AccountType = "investment", CurrencyCode = "USD",
                });
            Assert.Equal(AccountsRepository.CreateAccountFailure.None, created.Failure);
            brokerage = created.Account!.Id;
        }

        var fault = new FailCommand("recompute_holdings_for_brokerage");
        await using (var db = Db.NewRequestDbContextAsUser(ledger.UserId, fault))
        {
            var repo = new AccountsRepository(db, new LegDerivedRecomputeService(db), TimeProvider.System);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repo.SetIsTradeCommissionAsync(ledger.LedgerId, brokerage, isTradeCommission: true));
        }

        Assert.True(fault.Fired, "the recompute was never reached, so nothing was tested");
        await using var read = Db.NewDbContext();
        Assert.False(await read.Accounts.AsNoTracking().Where(a => a.Id == brokerage)
            .Select(a => a.IsTradeCommission).SingleAsync(), "the flag was written although its recompute failed");
    }

    // ----- the repairs -----

    /// <summary>
    /// The trade-price repair sent between two saves of one transaction that re-derive trade
    /// prices on different days, one brokerage's then another's (as a multi-step trade write
    /// does): the repair waits for the ledger's lock, so it never holds one brokerage's row
    /// while waiting for the other's. Neither is a deadlock victim, and the repaired day is
    /// stored.
    /// </summary>
    [Fact]
    public async Task The_trade_price_repair_sent_between_the_steps_of_a_multi_step_trade_write_waits_for_it()
    {
        await using var w = await WorldAsync();
        var securityId = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var one = await BrokerageAsync(w, "Brokerage R");
        var two = await BrokerageAsync(w, "Brokerage S");
        var (low, high) = one.CompareTo(two) < 0 ? (one, two) : (two, one);
        // Both brokerages trade the security on the repaired day, so the repair locks both,
        // the one that sorts first first.
        await TradedAsync(w, Trade(low, securityId, "buy", 5m, 10m, SecondDay));
        await TradedAsync(w, Trade(high, securityId, "buy", 5m, 10m, SecondDay));
        // That day's trade price not stored, as the consistency tests plant drift: written
        // past the writers that derive it, so the repair has the day to write back.
        await using (var seed = Db.NewDbContext())
        {
            Assert.Equal(1, await seed.SecurityPrices
                .Where(p => p.SecurityId == securityId && p.PriceDate == DateOnly.FromDateTime(SecondDay))
                .ExecuteDeleteAsync());
        }
        await WaitForStartPassAsync(w.Ledger.LedgerId);

        // The write's first step re-derives a day only the later-sorting brokerage trades on,
        // and holds that brokerage's row.
        await using var writer = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await using var held = await writer.BeginLedgerWriteAsync(w.Ledger.LedgerId);
        await TradeInAsync(writer, w.Ledger.LedgerId, Trade(high, securityId, "buy", 1m, 11m, FirstDay));

        await using var repairDb = Db.NewDbContext();
        await repairDb.Database.OpenConnectionAsync();
        var repairing = Consistency(repairDb).RewriteAsync(
            w.Ledger.LedgerId, ConsistencyProjections.TradePrices, w.Ledger.UserId, CancellationToken.None);
        await WaitForLockWaitersOrDoneAsync(1, repairing, Pid(repairDb));
        Assert.False(repairing.IsCompleted, "the repair did not wait for the write in flight, so nothing was tested");

        // Its second step re-derives a day only the earlier-sorting brokerage trades on.
        await NoDeadlockAsync(
            TradeInAsync(writer, w.Ledger.LedgerId, Trade(low, securityId, "buy", 1m, 12m, ThirdDay)),
            "the write's second step");
        await held.CommitAsync();
        var repaired = await NoDeadlockAsync(repairing, "the repair");

        Assert.Equal(1, repaired.MismatchedCount);
        await AssertHealthyAsync(w.Ledger.LedgerId);
    }

    /// <summary>
    /// The posting-count repair of a header sent while a save that added a posting to the
    /// header holds its legs: the repair waits for the save, then counts what it committed.
    /// The recompute's counts are subqueries that keep their statement's snapshot, so a
    /// statement begun before the save committed would wait for the legs and then write the
    /// counts it read before, over the save's.
    /// </summary>
    [Fact]
    public async Task The_posting_count_repair_sent_while_a_save_holds_the_headers_legs_stores_what_the_save_committed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var first = await ledger.AddCategoryAsync("Category A");
        var second = await ledger.AddCategoryAsync("Category B");
        var headerId = await SeedSpendAsync(ledger.LedgerId, bank.Id, first.Id, FirstDay, -10m);
        // The header's stored counts out of step, as the consistency tests plant drift:
        // written past the writers that recompute, so the repair has the header to rewrite.
        await using (var seed = Db.NewDbContext())
        {
            Assert.Equal(2, await seed.TxnLegs.Where(l => l.HeaderId == headerId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.HeaderTotalPostings, 5)));
        }

        // A save adding a second posting to the header, held open: its recompute has
        // rewritten every leg's counts, and it holds those legs.
        await using var writer = Db.NewServiceFactory().Create();
        await using var held = await writer.BeginLedgerWriteAsync(ledger.LedgerId);
        writer.Add(new TxnLegRow { Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledger.LedgerId, AccountId = bank.Id, PostingIndex = 1, Amount = -5m });
        writer.Add(new TxnLegRow { Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledger.LedgerId, AccountId = second.Id, PostingIndex = 1, Amount = 5m });
        await writer.SaveChangesAsync();

        await using var repairDb = Db.NewDbContext();
        await repairDb.Database.OpenConnectionAsync();
        var repairing = Consistency(repairDb).RewriteAsync(
            ledger.LedgerId, ConsistencyProjections.PostingCounts, ledger.UserId, CancellationToken.None);
        await WaitForLockWaitersOrDoneAsync(1, repairing, Pid(repairDb));
        Assert.False(repairing.IsCompleted, "the repair did not wait for the save in flight, so nothing was tested");

        await held.CommitAsync();
        var rewritten = await NoDeadlockAsync(repairing, "the repair");
        Assert.Equal(1, rewritten.MismatchedCount);

        await using var read = Db.NewDbContext();
        var legs = await read.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId)
            .Select(l => new { l.AccountId, l.HeaderTotalPostings, l.AccountPostingsOnHeader })
            .ToListAsync();
        Assert.Equal(4, legs.Count);
        Assert.All(legs, l => Assert.Equal(2, l.HeaderTotalPostings));
        Assert.All(legs, l => Assert.Equal(l.AccountId == bank.Id ? 2 : 1, l.AccountPostingsOnHeader));
    }

    /// <summary>What each repair that takes the lock through the helper has to rewrite: a
    /// drift planted past the writers, as the consistency tests plant it.</summary>
    private async Task PlantDriftAsync(World w, string projection)
    {
        var ledgerId = w.Ledger.LedgerId;
        switch (projection)
        {
            case ConsistencyProjections.PostingCounts:
            {
                var bank = await w.Ledger.AddBankAccountAsync("Checking");
                var category = await w.Ledger.AddCategoryAsync("Category A");
                var headerId = await SeedSpendAsync(ledgerId, bank.Id, category.Id, FirstDay, -10m);
                await using var seed = Db.NewDbContext();
                await seed.TxnLegs.Where(l => l.HeaderId == headerId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.HeaderTotalPostings, 5));
                break;
            }
            case ConsistencyProjections.StoredNotes:
            {
                var bank = await w.Ledger.AddBankAccountAsync("Checking");
                var category = await w.Ledger.AddCategoryAsync("Category A");
                var headerId = await SeedSpendAsync(ledgerId, bank.Id, category.Id, FirstDay, -10m);
                await using var seed = Db.NewDbContext();
                await seed.TxnLegs.Where(l => l.HeaderId == headerId && l.AccountId == bank.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.LegMemo, "written past the API"));
                // The drift is what the stored-equals-computed check reads, as the stored-notes
                // consistency tests plant it; the repair under test puts it right.
                Assert.NotEmpty(await LegNotes.DifferencesAsync(seed, ledgerId, CancellationToken.None));
                break;
            }
            case ConsistencyProjections.TradePrices:
            {
                var securityId = await w.Ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
                var brokerage = await BrokerageAsync(w, "Brokerage R");
                await TradedAsync(w, Trade(brokerage, securityId, "buy", 10m, 10m, FirstDay));
                await using var seed = Db.NewDbContext();
                await seed.SecurityPrices.Where(p => p.SecurityId == securityId).ExecuteDeleteAsync();
                break;
            }
            case ConsistencyProjections.HoldingsCurrency:
            {
                var brokerage = await BrokerageAsync(w, "Brokerage R");
                await using var seed = Db.NewDbContext();
                await seed.Accounts.Where(a => a.Id == brokerage)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrencyCode, "EUR"));
                break;
            }
            case ConsistencyProjections.ReminderAutoPost:
            {
                // A reminder saved with Auto-post, then its job removed: the state a ledger
                // imported before the import turned the job on is left in.
                var bank = await w.Ledger.AddBankAccountAsync("Checking");
                var category = await w.Ledger.AddCategoryAsync("Category A");
                var made = await w.Client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/reminders",
                    new CreateReminderRequest
                    {
                        Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                        StartDate = SeriesStart,
                        AutoCommitDaysBefore = 3,
                        Payee = "Payee R",
                        SourceAccountId = bank.Id,
                        Postings = new[] { new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -150m } },
                    });
                Assert.Equal(HttpStatusCode.Created, made.StatusCode);
                await using var seed = Db.NewServiceDbContext();
                Assert.Equal(1, await seed.ScheduledJobs
                    .Where(j => j.LedgerId == ledgerId && j.JobType == JobTypes.ReminderAutoPost)
                    .ExecuteDeleteAsync());
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(projection), projection, null);
        }
    }

    /// <summary>
    /// Each repair that takes the ledger's lock as the first statement of its transaction:
    /// sent while another write holds it past the wait, it is refused with the busy refusal,
    /// a 422 and never a 500, in the repair's words, and rewrites nothing; sent again once
    /// the write is done, it repairs.
    /// </summary>
    [Theory]
    [InlineData(ConsistencyProjections.PostingCounts)]
    [InlineData(ConsistencyProjections.StoredNotes)]
    [InlineData(ConsistencyProjections.TradePrices)]
    [InlineData(ConsistencyProjections.HoldingsCurrency)]
    [InlineData(ConsistencyProjections.ReminderAutoPost)]
    public async Task A_repair_whose_ledger_is_written_past_the_wait_is_refused_as_busy_and_rewrites_nothing(string projection)
    {
        await using var w = await WorldAsync(shortCommandTimeout: true);
        await PlantDriftAsync(w, projection);
        Assert.False(await ProjectionHealthyAsync(w.Ledger.LedgerId, projection), "the drift was not planted");

        var (holder, held) = await HoldLedgerAsync(w.Ledger.LedgerId);
        await using (holder)
        {
            using var refused = await RepairAsync(w, projection);
            await AssertBusyAsync(refused, "the repair", LedgerWriteLock.RepairBusyDetail);
            await held.DisposeAsync();
        }
        Assert.False(await ProjectionHealthyAsync(w.Ledger.LedgerId, projection), "the refused repair rewrote the drift");

        using var repaired = await RepairAsync(w, projection);
        await AssertRepairedAsync(repaired);
        Assert.True(await ProjectionHealthyAsync(w.Ledger.LedgerId, projection), "the repair sent again left the drift");
    }

    /// <summary>
    /// The balances repair, whose recompute takes the ledger's lock itself, one account at a
    /// time: sent while another write holds it past the function's own wait, it is refused
    /// with the busy refusal, a 422 and never a 500, in the repair's words.
    /// </summary>
    [Fact]
    public async Task The_balances_repair_whose_ledger_is_written_past_the_wait_is_refused_as_busy()
    {
        await using var w = await WorldAsync();
        var bank = await w.Ledger.AddBankAccountAsync("Checking");
        var category = await w.Ledger.AddCategoryAsync("Category A");
        await SeedSpendAsync(w.Ledger.LedgerId, bank.Id, category.Id, FirstDay, -10m);

        var (holder, held) = await HoldLedgerAsync(w.Ledger.LedgerId);
        await using (holder)
        {
            using var refused = await RepairAsync(w, ConsistencyProjections.Balances);
            await AssertBusyAsync(refused, "the repair", LedgerWriteLock.RepairBusyDetail);
            await held.DisposeAsync();
        }

        using var repaired = await RepairAsync(w, ConsistencyProjections.Balances);
        await AssertRepairedAsync(repaired);
    }

    /// <summary>
    /// The posting-count repair of two headers, refused as busy at the second: another write
    /// takes the ledger's lock between the repair's two pieces and holds it past the wait.
    /// The header repaired before the refusal stays repaired, the other is left as it was,
    /// and the 422 says the repair stopped and kept what it repaired, not that nothing was
    /// changed. Repairing again finishes it.
    /// </summary>
    [Fact]
    public async Task A_repair_refused_as_busy_partway_keeps_what_it_repaired_and_says_so()
    {
        var between = new ArmedBeforeNthCommand("fn_ledger_write_lock", 2);
        await using var w = await WorldAsync(shortCommandTimeout: true, between);
        var bank = await w.Ledger.AddBankAccountAsync("Checking");
        var category = await w.Ledger.AddCategoryAsync("Category A");
        var first = await SeedSpendAsync(w.Ledger.LedgerId, bank.Id, category.Id, FirstDay, -10m);
        var second = await SeedSpendAsync(w.Ledger.LedgerId, bank.Id, category.Id, SecondDay, -20m);
        // Both headers' stored counts out of step, as the consistency tests plant drift, so
        // the repair has two pieces, one header per transaction.
        await using (var seed = Db.NewDbContext())
        {
            Assert.Equal(4, await seed.TxnLegs.Where(l => l.HeaderId == first || l.HeaderId == second)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.HeaderTotalPostings, 5)));
        }

        AppDbContext? holder = null;
        LedgerWriteScope? held = null;
        between.Arm(async () => (holder, held) = await HoldLedgerAsync(w.Ledger.LedgerId));
        try
        {
            using var refused = await RepairAsync(w, ConsistencyProjections.PostingCounts);
            Assert.True(between.Fired, "no write took the ledger's lock between the repair's pieces, so nothing was tested");
            await AssertBusyAsync(refused, "the repair", LedgerWriteLock.RepairBusyDetail);
        }
        finally
        {
            if (held is not null) await held.DisposeAsync();
            if (holder is not null) await holder.DisposeAsync();
        }

        await using (var read = Db.NewDbContext())
        {
            var counts = await read.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == first || l.HeaderId == second)
                .Select(l => new { l.HeaderId, l.HeaderTotalPostings })
                .ToListAsync();
            var byHeader = counts.GroupBy(c => c.HeaderId)
                .ToDictionary(g => g.Key, g => g.Select(c => c.HeaderTotalPostings).Distinct().Single());
            // One header repaired (its one posting), the other as the drift left it.
            Assert.Equal(new[] { 1, 5 }, byHeader.Values.Order().ToArray());
        }

        using var repaired = await RepairAsync(w, ConsistencyProjections.PostingCounts);
        await AssertRepairedAsync(repaired);
        await AssertHealthyAsync(w.Ledger.LedgerId);
    }

    // ----- MCP -----

    private static int _rpcId;

    private static HttpRequestMessage ToolCall(string tool, object arguments) =>
        new(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref _rpcId),
                method = "tools/call",
                @params = new { name = tool, arguments },
            }),
        };

    /// <summary>The JSON-RPC result of an MCP answer, as streamable HTTP sends it (one SSE
    /// event carrying the response, or the response itself).</summary>
    private static JsonElement McpResult(string body)
    {
        var json = body.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal)) is { } data
            ? data["data:".Length..]
            : body;
        return JsonDocument.Parse(json).RootElement.GetProperty("result");
    }

    /// <summary>A client holding a connected-apps bearer that may read and write for
    /// <paramref name="ledger"/>'s owner, with an initialized MCP session.</summary>
    private static async Task<HttpClient> McpClientAsync(TestDatabase database, ApiFactory factory, SyntheticLedger ledger)
    {
        var (token, hash) = McpTokenService.Generate();
        await new McpTokensRepository(database.NewServiceFactory(), TimeProvider.System)
            .IssueAsync(ledger.UserId, "ledger-write-lock", hash, $"{McpScopes.Read} {McpScopes.Write}", expiresAt: null);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var init = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _rpcId),
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "coffer-tests", version = "1.0" },
            },
        });
        init.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("Mcp-Session-Id", init.Headers.GetValues("Mcp-Session-Id").Single());
        using var initialized = await client.PostAsJsonAsync(
            "/mcp", new { jsonrpc = "2.0", method = "notifications/initialized" });
        initialized.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>
    /// An MCP write sent while another write holds the ledger's lock past the wait answers
    /// the busy refusal by name, not the SDK's internal error, and writes nothing; sent again
    /// once the write is done, it is saved.
    /// </summary>
    [Fact]
    public async Task An_mcp_write_refused_because_the_ledger_is_being_written_answers_the_busy_refusal()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddCategoryAsync("Category S");
        var target = await ledger.AddCategoryAsync("Category T");
        await using var factory = new ApiFactory(Db).WithMcpEnabled().WithoutDevAuth()
            .WithConfig("Api:ConnectionString", WithShortCommandTimeout(Db.AppConnectionString));
        using var client = await McpClientAsync(Db, factory, ledger);
        factory.Services.GetRequiredService<McpRuntimeState>().WritesEnabled = true;
        var merge = new { ledgerId = ledger.LedgerId, sourceId = source.Id, targetId = target.Id };

        var (holder, held) = await HoldLedgerAsync(ledger.LedgerId);
        await using (holder)
        {
            using var refused = await client.SendAsync(ToolCall("merge_category", merge));
            var result = McpResult(await refused.Content.ReadAsStringAsync());
            Assert.True(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
                "the refused write did not answer an error: " + result);
            Assert.Equal($"{BusinessError.Codes.LedgerWriteBusy}: {LedgerWriteLock.BusyDetail}",
                result.GetProperty("content")[0].GetProperty("text").GetString());
            await held.DisposeAsync();
        }
        await using (var read = Db.NewDbContext())
        {
            Assert.True(await read.Accounts.AsNoTracking().Where(a => a.Id == source.Id)
                .Select(a => a.IsActive).SingleAsync(), "the refused merge deactivated its source");
        }

        using var merged = await client.SendAsync(ToolCall("merge_category", merge));
        var again = McpResult(await merged.Content.ReadAsStringAsync());
        Assert.False(again.TryGetProperty("isError", out var stillError) && stillError.GetBoolean(),
            "the merge sent again was refused: " + again);
        await using (var read = Db.NewDbContext())
        {
            Assert.False(await read.Accounts.AsNoTracking().Where(a => a.Id == source.Id)
                .Select(a => a.IsActive).SingleAsync(), "the merge sent again did not deactivate its source");
        }
    }

    /// <summary>Before the first ledger write lock each transaction of a context sends (the
    /// transactions in the order they open), runs that transaction's action, when it has
    /// one.</summary>
    private sealed class BeforeEachTransaction(params Func<Task>?[] actions) : DbCommandInterceptor
    {
        private readonly HashSet<Guid> _seen = [];

        public int Transactions
        {
            get { lock (_seen) return _seen.Count; }
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.Database.CurrentTransaction is { } transaction
                && command.CommandText.Contains("fn_ledger_write_lock", StringComparison.Ordinal))
            {
                int index;
                lock (_seen)
                {
                    index = _seen.Add(transaction.TransactionId) ? _seen.Count - 1 : -1;
                }
                if (index >= 0 && index < actions.Length && actions[index] is { } action) await action();
            }
            return result;
        }
    }

    /// <summary>
    /// An MCP bulk recategorize of three transactions whose second is refused because
    /// another write holds the ledger's lock past the wait: each transaction is saved on its
    /// own, so the first stays moved, the second is rejected with <c>ledger-write-busy</c> and
    /// left as it was, and the third, reached once the lock is free, is moved. The answer
    /// counts the two moved and names the one rejected, where the busy refusal used to end the
    /// call with "Nothing was changed". For simple transactions (<c>set_transaction_category</c>)
    /// and splits (<c>set_split_posting_category</c>).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_mcp_bulk_recategorize_refused_as_busy_partway_moves_the_others_and_rejects_that_one(bool splits)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var from = await ledger.AddCategoryAsync("Category F");
        var to = await ledger.AddCategoryAsync("Category T");
        var other = await ledger.AddCategoryAsync("Category O");
        var headers = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            if (splits)
            {
                var (_, split) = await ledger.AddMultiSplitAsync(
                    bank.Id, new[] { (from.Id, 10m + i), (other.Id, 5m) }, FirstDay.AddDays(i));
                headers.Add(split);
            }
            else
            {
                var (leg, _) = await ledger.AddTransactionPairAsync(bank.Id, from.Id, -10m - i, FirstDay.AddDays(i));
                headers.Add(await ledger.ResolveHeaderIdAsync(leg));
            }
        }

        // The ledger's lock held from just before the second transaction's own to just
        // before the third's.
        AppDbContext? holder = null;
        LedgerWriteScope? held = null;
        var gate = new BeforeEachTransaction(
            null,
            async () => (holder, held) = await HoldLedgerAsync(ledger.LedgerId),
            async () =>
            {
                await held!.DisposeAsync();
                await holder!.DisposeAsync();
                (held, holder) = (null, null);
            });
        int movedCount;
        IReadOnlyList<McpRecategorizeReject> rejects;
        try
        {
            await using var db = ShortTimeoutServiceFactory(gate).Create();
            var transactions = new TransactionsRepository(db, TimeProvider.System);
            if (splits)
            {
                var result = await McpWriteTools.SetSplitPostingCategory(
                    McpTestGuard.Writable(), transactions, ledger.LedgerId, [.. headers], from.Id, to.Id);
                Assert.True(result.Ok, result.Error);
                (movedCount, rejects) = (result.Moved, result.Rejects);
            }
            else
            {
                var result = await McpWriteTools.SetTransactionCategory(
                    McpTestGuard.Writable(), transactions, ledger.LedgerId, [.. headers], to.Id);
                Assert.True(result.Ok, result.Error);
                (movedCount, rejects) = (result.Recategorized, result.Rejects);
            }
        }
        finally
        {
            if (held is not null) await held.DisposeAsync();
            if (holder is not null) await holder.DisposeAsync();
        }
        Assert.Equal(3, gate.Transactions);

        Assert.Equal(2, movedCount);
        var rejected = Assert.Single(rejects);
        Assert.Equal((headers[1], BusinessError.Codes.LedgerWriteBusy), (rejected.HeaderId, rejected.Reason));
        await using (var read = Db.NewDbContext())
        {
            var onFrom = await read.TxnLegs.AsNoTracking()
                .Where(l => headers.Contains(l.HeaderId) && l.AccountId == from.Id)
                .Select(l => l.HeaderId).ToListAsync();
            Assert.Equal([headers[1]], onFrom);
            Assert.Equal(2, await read.TxnLegs.AsNoTracking()
                .CountAsync(l => headers.Contains(l.HeaderId) && l.AccountId == to.Id));
        }
        await AssertHealthyAsync(ledger.LedgerId);
    }

    // ----- ingest -----

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(impl(request));
    }

    private static string SetupTokenFor(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string SimpleFinBody(params (string Id, long Posted, string Amount)[] transactions) =>
        """{"connections":[{"conn_id":"c-A","name":"Bank A","org_id":"banka","sfin_url":"https://sfin/banka"}],"errlist":[],"accounts":[{"id":"sf-1","conn_id":"c-A","name":"Checking","currency":"USD","balance":"0.00","transactions":["""
        + string.Join(",", transactions.Select(t =>
            $$"""{"id":"{{t.Id}}","posted":{{t.Posted}},"amount":"{{t.Amount}}","description":"PAYEE","pending":false}"""))
        + "]}]}";

    /// <summary>A SimpleFIN client that claims each setup token as an access URL named for its
    /// claim URL, and answers every fetch with <paramref name="body"/>.</summary>
    private static SimpleFinClient StubClient(StrongBox<string> body) =>
        new(new HttpClient(new StubHandler(req => req.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "https://u:p@bridge.simplefin.org/simplefin/access/" + req.RequestUri!.AbsoluteUri.Split('/')[^1]),
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.Value!) })), TimeProvider.System);

    private static async Task<FeedConnectionSummary> ConnectAsync(HttpClient client, Guid ledgerId, string name)
    {
        using var created = await client.PostAsJsonAsync($"/api/ledgers/{ledgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupTokenFor("https://bridge.simplefin.org/simplefin/claim/" + name) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
    }

    private static async Task MapAsync(HttpClient client, Guid ledgerId, Guid accountId, Guid connectionId, string externalId)
    {
        using var mapped = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledgerId}/accounts/{accountId}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connectionId,
                SimpleFinAccountId = externalId,
            }),
        });
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);
    }

    /// <summary>Runs <paramref name="first"/> just before the first command naming the ledger
    /// write lock, and <paramref name="second"/> just before the second.</summary>
    private sealed class AroundLockCommands(Func<Task> first, Func<Task> second) : DbCommandInterceptor
    {
        private int _seen;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("fn_ledger_write_lock", StringComparison.Ordinal))
            {
                var seen = Interlocked.Increment(ref _seen);
                if (seen == 1) await first();
                else if (seen == 2) await second();
            }
            return result;
        }
    }

    private async Task LekAsync(SyntheticLedger ledger)
    {
        var keys = Db.NewLedgerKeyService();
        await using var db = Db.NewDbContext();
        var row = await db.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
        row.WrappedLek = keys.CreateWrappedLek();
        row.LekKekId = keys.CurrentKekId;
        row.LekCreatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>Whether the ledger's Uncategorized category, which a ledger's first ingest
    /// creates, exists.</summary>
    private async Task<bool> UncategorizedExistsAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        return await db.Accounts.AsNoTracking().AnyAsync(a => a.LedgerId == ledgerId
            && a.IsSystem
            && a.AccountType == UncategorizedCategory.AccountType
            && a.Name == UncategorizedCategory.Name);
    }

    private async Task<(DateTime? Watermark, int Headers, LedgerOperationRow LastRun)> FeedStateAsync(
        Guid ledgerId, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        var watermark = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId)
            .Select(a => a.LastSimpleFinSyncAt).SingleAsync();
        var headers = await db.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.LedgerId == ledgerId && h.ProviderKey == "simplefin");
        var lastRun = await db.LedgerOperations.AsNoTracking()
            .Where(o => o.LedgerId == ledgerId && o.Family == "ingest")
            .OrderByDescending(o => o.StartedAt)
            .FirstAsync();
        return (watermark, headers, lastRun);
    }

    /// <summary>
    /// The scheduled feed sync, run while another write holds the ledger's lock past the
    /// wait: first on the ledger's first ingest (whose save creates Uncategorized), then on a
    /// later one (the run's final save). Each refused run is degraded rather than thrown,
    /// closes its run as failed, saves nothing it fetched and leaves the watermark where it
    /// was, so the next run fetches and saves the same rows. A sync a person sends while the
    /// lock is held answers the busy refusal.
    /// </summary>
    [Fact]
    public async Task A_scheduled_sync_refused_because_the_ledger_is_being_written_loses_nothing_and_the_next_run_saves_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await LekAsync(ledger);
        var body = new StrongBox<string>(SimpleFinBody(("lock-sf-1", 1715000000, "-12.34")));
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient(body))
            .WithConfig("Api:ConnectionString", WithShortCommandTimeout(Db.AppConnectionString));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        var connection = await ConnectAsync(client, ledger.LedgerId, "bank-a");
        var bank = await ledger.AddBankAccountAsync("Checking");
        await MapAsync(client, ledger.LedgerId, bank.Id, connection.Id, "sf-1");

        var services = ShortTimeoutServiceFactory();
        async Task<JobRunOutcome> RunAsync()
        {
            using var scope = factory.Services.CreateScope();
            var handler = scope.ServiceProvider.GetServices<IScheduledJobHandler>()
                .Single(h => h.JobType == JobTypes.FeedSync);
            await using var db = services.Create();
            return await handler.RunAsync(db, ledger.LedgerId, ledger.UserId, new JobRunClock(DateTime.UtcNow, "UTC"), default);
        }

        async Task<JobRunOutcome> RunWhileHeldAsync()
        {
            var (holder, held) = await HoldLedgerAsync(ledger.LedgerId);
            await using (holder)
            await using (held)
                return await RunAsync();
        }

        void AssertRefused(JobRunOutcome outcome, (DateTime? Watermark, int Headers, LedgerOperationRow LastRun) state,
            DateTime? watermark, int headers)
        {
            Assert.Equal(JobRunResult.Degraded, outcome.Result);
            // The job names each connection that did not sync with its failure reason.
            Assert.Contains("(LedgerBusy)", outcome.Message, StringComparison.Ordinal);
            Assert.Equal("failed", state.LastRun.Status);
            Assert.Equal(LedgerWriteLock.BusyDetail, state.LastRun.ErrorMessage);
            Assert.Equal(headers, state.Headers);
            Assert.Equal(watermark, state.Watermark);
        }

        // The first ingest: refused at the save that creates Uncategorized.
        var refusedFirst = await RunWhileHeldAsync();
        AssertRefused(refusedFirst, await FeedStateAsync(ledger.LedgerId, bank.Id), watermark: null, headers: 0);
        Assert.False(await UncategorizedExistsAsync(ledger.LedgerId), "the refused run saved its Uncategorized category");

        var savedFirst = await RunAsync();
        Assert.Equal(JobRunResult.Ok, savedFirst.Result);
        var afterFirst = await FeedStateAsync(ledger.LedgerId, bank.Id);
        Assert.Equal(1, afterFirst.Headers);
        Assert.NotNull(afterFirst.Watermark);

        // A later one: refused at the run's final save.
        body.Value = SimpleFinBody(("lock-sf-1", 1715000000, "-12.34"), ("lock-sf-2", 1715100000, "-20.00"));
        var refusedLater = await RunWhileHeldAsync();
        AssertRefused(refusedLater, await FeedStateAsync(ledger.LedgerId, bank.Id), afterFirst.Watermark, headers: 1);

        var (manualHolder, manualHeld) = await HoldLedgerAsync(ledger.LedgerId);
        await using (manualHolder)
        await using (manualHeld)
        {
            using var manual = await client.PostAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync", content: null);
            await AssertBusyAsync(manual, "the sync a person sent");

            using var all = await client.PostAsync($"/api/ledgers/{ledger.LedgerId}/sync-all", content: null);
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            var entry = Assert.Single((await all.Content.ReadFromJsonAsync<SyncAllResultDto>())!.Connections);
            Assert.Equal(BusinessError.Codes.LedgerWriteBusy, entry.FailureCode);
        }

        var savedLater = await RunAsync();
        Assert.Equal(JobRunResult.Ok, savedLater.Result);
        var afterLater = await FeedStateAsync(ledger.LedgerId, bank.Id);
        Assert.Equal(2, afterLater.Headers);
        Assert.True(afterLater.Watermark > afterFirst.Watermark, "the watermark did not move with the saved rows");
        await AssertHealthyAsync(ledger.LedgerId);
    }

    /// <summary>
    /// A scheduled pass over two connections on one context, the first refused as busy at
    /// its final save: the pass goes on to the second, which saves its own rows and none of
    /// the refused run's.
    /// </summary>
    [Fact]
    public async Task A_scheduled_pass_whose_first_connection_is_refused_as_busy_saves_only_the_next_connections_rows()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await LekAsync(ledger);
        var body = new StrongBox<string>("""
            {"connections":[{"conn_id":"c-A","name":"Bank A","org_id":"banka","sfin_url":"https://sfin/banka"}],"errlist":[],"accounts":[
              {"id":"sf-1","conn_id":"c-A","name":"Checking","currency":"USD","balance":"0.00","transactions":[
                {"id":"pass-sf-1","posted":1715000000,"amount":"-12.34","description":"PAYEE","pending":false}]},
              {"id":"sf-2","conn_id":"c-A","name":"Savings","currency":"USD","balance":"0.00","transactions":[
                {"id":"pass-sf-2","posted":1715100000,"amount":"-20.00","description":"PAYEE","pending":false}]}
            ]}
            """);
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient(body));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        var first = await ConnectAsync(client, ledger.LedgerId, "bank-a");
        var second = await ConnectAsync(client, ledger.LedgerId, "bank-b");
        var refusedBank = await ledger.AddBankAccountAsync("Checking");
        var savedBank = await ledger.AddBankAccountAsync("Savings");
        await MapAsync(client, ledger.LedgerId, refusedBank.Id, first.Id, "sf-1");
        await MapAsync(client, ledger.LedgerId, savedBank.Id, second.Id, "sf-2");
        // The ledger's first ingest, through a file import, so each connection's only lock is
        // its final save's.
        var imported = await ledger.AddBankAccountAsync("Imported");
        using (var import = await client.PostAsync(
                   $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import", QifUpload(QifFirst, imported.Id)))
        {
            Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        }

        // The ledger's lock held from just before the first connection's final save to just
        // before the second's.
        AppDbContext? holder = null;
        LedgerWriteScope? held = null;
        var gate = new AroundLockCommands(
            first: async () =>
            {
                holder = Db.NewDbContext();
                held = await holder.BeginLedgerWriteAsync(ledger.LedgerId);
            },
            second: async () =>
            {
                await held!.DisposeAsync();
                await holder!.DisposeAsync();
                held = null;
                holder = null;
            });
        var services = PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
        {
            ConnectionString = Db.AppConnectionString,
            ServiceConnectionString = WithShortCommandTimeout(Db.ServiceConnectionString),
        }), gate);

        JobRunOutcome outcome;
        try
        {
            using var scope = factory.Services.CreateScope();
            var handler = scope.ServiceProvider.GetServices<IScheduledJobHandler>()
                .Single(h => h.JobType == JobTypes.FeedSync);
            await using var db = services.Create();
            outcome = await handler.RunAsync(db, ledger.LedgerId, ledger.UserId, new JobRunClock(DateTime.UtcNow, "UTC"), default);
        }
        finally
        {
            if (held is not null) await held.DisposeAsync();
            if (holder is not null) await holder.DisposeAsync();
        }

        Assert.Equal(JobRunResult.Degraded, outcome.Result);
        Assert.Contains($"{first.Id:N} (LedgerBusy)", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"{second.Id:N}", outcome.Message, StringComparison.Ordinal);
        await using var read = Db.NewDbContext();
        var headersByAccount = await read.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledger.LedgerId && (l.AccountId == refusedBank.Id || l.AccountId == savedBank.Id))
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.AccountId, g => g.Count);
        Assert.False(headersByAccount.ContainsKey(refusedBank.Id), "the refused run's rows were saved");
        Assert.Equal(1, headersByAccount.GetValueOrDefault(savedBank.Id));
        var runs = await read.LedgerOperations.AsNoTracking()
            .Where(o => o.LedgerId == ledger.LedgerId && o.Family == "ingest" && o.FeedConnectionId != null)
            .ToDictionaryAsync(o => o.FeedConnectionId!.Value, o => o.Status);
        Assert.Equal("failed", runs[first.Id]);
        Assert.Equal("completed", runs[second.Id]);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    /// <summary>A ledger with a key, a host whose SimpleFIN client answers <paramref name="body"/>,
    /// one connection with a bank account mapped to its <c>sf-1</c>, and, unless
    /// <paramref name="firstIngest"/>, a file import already made, so the ledger's
    /// Uncategorized exists and a sync's only save is its final one.</summary>
    private async Task<FeedWorld> FeedWorldAsync(StrongBox<string> body, bool firstIngest)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await LekAsync(ledger);
        var factory = new ApiFactory(Db).WithoutDevAuth().WithService<SimpleFinClient>(_ => StubClient(body));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        var world = new World(ledger, factory, client);
        var connection = await ConnectAsync(client, ledger.LedgerId, "bank-a");
        var bank = await ledger.AddBankAccountAsync("Checking");
        await MapAsync(client, ledger.LedgerId, bank.Id, connection.Id, "sf-1");
        if (!firstIngest)
        {
            var imported = await ledger.AddBankAccountAsync("Imported");
            using var import = await client.PostAsync(
                $"/api/ledgers/{ledger.LedgerId}/ingest/qif/import", QifUpload(QifFirst, imported.Id));
            Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        }
        return new FeedWorld(world, connection.Id, bank.Id);
    }

    private sealed record FeedWorld(World World, Guid ConnectionId, Guid Bank) : IAsyncDisposable
    {
        public SyntheticLedger Ledger => World.Ledger;

        public ApiFactory Factory => World.Factory;

        public ValueTask DisposeAsync() => World.DisposeAsync();
    }

    private static IScheduledJobHandler FeedSyncHandler(IServiceScope scope) =>
        scope.ServiceProvider.GetServices<IScheduledJobHandler>().Single(h => h.JobType == JobTypes.FeedSync);

    /// <summary>
    /// A feed connection deleted while its sync's final save runs, the save holding the
    /// ledger's lock and the mapped account's watermark row and about to insert the
    /// connection's bank account, listed for the first time: the delete waits for the save
    /// at the ledger write lock, before it locks the connection, then deletes it. Neither is
    /// a deadlock victim.
    /// </summary>
    /// <remarks>
    /// Without the lock on the delete, it would lock the connection and wait at the account
    /// for its cascade, while the save's insert waited at the connection for its foreign key.
    /// </remarks>
    [Fact]
    public async Task A_feed_connection_deleted_while_its_syncs_final_save_runs_waits_for_it_at_the_ledger_write_lock()
    {
        var body = new StrongBox<string>(SimpleFinBody(("del-sf-1", 1715000000, "-12.34")));
        await using var feed = await FeedWorldAsync(body, firstIngest: false);
        var (ledger, bank) = (feed.Ledger, feed.Bank);

        Task<int>? deleting = null;
        var waited = false;
        string? waitedAt = null;
        await using var deleteDb = Db.NewRequestDbContextAsUser(ledger.UserId);
        await deleteDb.Database.OpenConnectionAsync();
        var pid = Pid(deleteDb);
        // Just before the final save's statements, once it holds the lock and the account.
        var hold = new BeforeNthCommand("INSERT INTO feed_connection_accounts", 1, async () =>
        {
            deleting = new FeedConnectionsRepository(deleteDb).DeleteAsync(ledger.LedgerId, feed.ConnectionId);
            await WaitForLockWaitersOrDoneAsync(1, deleting, pid);
            waited = !deleting.IsCompleted;
            waitedAt = await WaitEventAsync(pid);
        });
        var services = PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
        {
            ConnectionString = Db.AppConnectionString,
            ServiceConnectionString = Db.ServiceConnectionString,
        }), hold);

        JobRunOutcome outcome;
        try
        {
            using var scope = feed.Factory.Services.CreateScope();
            await using var db = services.Create();
            outcome = await NoDeadlockAsync(FeedSyncHandler(scope).RunAsync(
                db, ledger.LedgerId, ledger.UserId, new JobRunClock(DateTime.UtcNow, "UTC"), default), "the sync");
        }
        finally
        {
            if (deleting is not null)
            {
                try { await deleting; }
                catch (Exception) { /* asserted below, or superseded by the failure in flight */ }
            }
        }

        Assert.True(hold.Fired, "the delete was never sent while the sync's final save ran, so nothing was tested");
        Assert.Equal(1, await NoDeadlockAsync(deleting!, "the delete"));
        Assert.Equal(JobRunResult.Ok, outcome.Result);
        Assert.True(waited, "the delete did not wait for the sync's final save");
        Assert.Equal("advisory", waitedAt);

        // The sync saved what it fetched before the delete ran; the connection is gone and
        // the account it fed is no longer linked to it.
        var state = await FeedStateAsync(ledger.LedgerId, bank);
        Assert.Equal(1, state.Headers);
        await using (var read = Db.NewDbContext())
        {
            Assert.False(await read.FeedConnections.AsNoTracking().AnyAsync(c => c.Id == feed.ConnectionId));
            Assert.Null(await read.Accounts.AsNoTracking().Where(a => a.Id == bank).Select(a => a.FeedConnectionId).SingleAsync());
        }
        await AssertHealthyAsync(ledger.LedgerId);
    }

    /// <summary>
    /// A scheduled feed sync whose save of what it fetched fails for a reason other than the
    /// busy refusal, on the ledger's first ingest (the save that creates Uncategorized) and on
    /// a later one (the run's final save), run by the scheduler: the run is closed as failed
    /// and what it had tracked is dropped, so the scheduler's save of the job's outcome, on
    /// the context the sync ran on, succeeds and counts the failure. Nothing the run fetched
    /// is saved and the watermark does not move, so the next run saves the rows.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_scheduled_sync_whose_save_fails_drops_what_it_tracked_so_the_schedulers_save_succeeds(bool firstIngest)
    {
        var body = new StrongBox<string>(SimpleFinBody(("fail-sf-1", 1715000000, "-12.34")));
        await using var feed = await FeedWorldAsync(body, firstIngest);
        var (ledger, factory, bank) = (feed.Ledger, feed.Factory, feed.Bank);

        await using (var seed = Db.NewDbContext())
        {
            seed.ScheduledJobs.Add(new ScheduledJobRow
            {
                LedgerId = ledger.LedgerId,
                JobType = JobTypes.FeedSync,
                Enabled = true,
                HourLocal = 3,
                MinuteLocal = 0,
                ConfiguredByUserId = ledger.UserId,
                NextRunAt = DateTime.UtcNow.AddMinutes(-1),
            });
            await seed.SaveChangesAsync();
        }

        // Failed before it is sent: the first ingest's save at its Uncategorized row, a later
        // one's (its final save, the only one) at the connection's bank account, which it
        // lists for the first time.
        var fault = new FailCommand(firstIngest ? "INSERT INTO accounts" : "INSERT INTO feed_connection_accounts");
        var services = PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
        {
            ConnectionString = Db.AppConnectionString,
            ServiceConnectionString = Db.ServiceConnectionString,
        }), fault);
        using (var scope = factory.Services.CreateScope())
        {
            var handler = FeedSyncHandler(scope);
            await using var db = services.Create();
            // No fresh context: the outcome is recorded only if the save on this one succeeds.
            await new SchedulerRunner().RunDueAsync(
                db, new Dictionary<string, IScheduledJobHandler> { [handler.JobType] = handler },
                DateTime.UtcNow, NullLogger.Instance, default);
        }
        Assert.True(fault.Fired, "the save never failed, so nothing was tested");

        await using (var read = Db.NewDbContext())
        {
            var job = await read.ScheduledJobs.AsNoTracking()
                .SingleAsync(j => j.LedgerId == ledger.LedgerId && j.JobType == JobTypes.FeedSync);
            Assert.True(job.ConsecutiveFailures == 1,
                $"the scheduler's save of the job's outcome did not land: {job.ConsecutiveFailures} failure(s) recorded");
            Assert.NotNull(job.LastError);
        }
        var refused = await FeedStateAsync(ledger.LedgerId, bank);
        Assert.Equal(("failed", IngestOrchestrator.SaveFailedDetail), (refused.LastRun.Status, refused.LastRun.ErrorMessage));
        Assert.Equal(0, refused.Headers);
        Assert.Null(refused.Watermark);
        if (firstIngest)
            Assert.False(await UncategorizedExistsAsync(ledger.LedgerId), "the failed run's Uncategorized was saved");

        using (var scope = factory.Services.CreateScope())
        {
            await using var db = Db.NewServiceFactory().Create();
            var next = await FeedSyncHandler(scope).RunAsync(
                db, ledger.LedgerId, ledger.UserId, new JobRunClock(DateTime.UtcNow, "UTC"), default);
            Assert.Equal(JobRunResult.Ok, next.Result);
        }
        var saved = await FeedStateAsync(ledger.LedgerId, bank);
        Assert.Equal(1, saved.Headers);
        Assert.NotNull(saved.Watermark);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    private static MultipartFormDataContent QifUpload(string qif, Guid accountId)
    {
        var content = new MultipartFormDataContent();
        var stream = new ByteArrayContent(Encoding.UTF8.GetBytes(qif));
        stream.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(stream, "file", "statement.qif");
        content.Add(new StringContent(accountId.ToString()), "accountId");
        content.Add(new StringContent("qif"), "providerAccountId");
        return content;
    }

    private const string QifFirst = """
        !Type:Bank
        D01/05/2026
        T-12.34
        PPAYEE A
        ^
        """;

    private const string QifSecond = """
        !Type:Bank
        D01/15/2026
        T-20.00
        PPAYEE B
        ^
        """;

    /// <summary>
    /// A file import sent while another write holds the ledger's lock past the wait, on the
    /// ledger's first ingest (whose save creates Uncategorized) and on a later one (the run's
    /// final save): each answers the busy refusal, closes its run as failed and saves
    /// nothing; sent again, it is saved.
    /// </summary>
    [Fact]
    public async Task A_file_import_refused_because_the_ledger_is_being_written_closes_its_run_and_saves_nothing()
    {
        await using var w = await WorldAsync(shortCommandTimeout: true);
        var bank = await w.Ledger.AddBankAccountAsync("Checking");

        async Task<HttpResponseMessage> ImportAsync(string qif) =>
            await w.Client.PostAsync($"/api/ledgers/{w.Ledger.LedgerId}/ingest/qif/import", QifUpload(qif, bank.Id));

        async Task RefusedAsync(string qif, int headers)
        {
            var (holder, held) = await HoldLedgerAsync(w.Ledger.LedgerId);
            await using (holder)
            await using (held)
            {
                using var refused = await ImportAsync(qif);
                await AssertBusyAsync(refused, "the import");
            }
            await using var read = Db.NewDbContext();
            Assert.Equal(headers, await read.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == w.Ledger.LedgerId));
            var run = await read.LedgerOperations.AsNoTracking()
                .Where(o => o.LedgerId == w.Ledger.LedgerId && o.Family == "ingest")
                .OrderByDescending(o => o.StartedAt).FirstAsync();
            Assert.Equal("failed", run.Status);
            Assert.Equal(LedgerWriteLock.BusyDetail, run.ErrorMessage);
        }

        await RefusedAsync(QifFirst, headers: 0);
        Assert.False(await UncategorizedExistsAsync(w.Ledger.LedgerId), "the refused import saved its Uncategorized category");
        using (var saved = await ImportAsync(QifFirst))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        await RefusedAsync(QifSecond, headers: 1);
        using (var saved = await ImportAsync(QifSecond))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        await AssertHealthyAsync(w.Ledger.LedgerId);
    }

    /// <summary>
    /// A file import whose save fails for a reason other than the busy refusal, on the
    /// ledger's first ingest (the save that creates Uncategorized) and on a later one (the
    /// run's final save): the request fails as before and saves nothing, and its run is closed
    /// as failed, where it was left running for good; imported again, the file is saved.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_import_whose_save_fails_closes_its_run_and_saves_nothing(bool firstIngest)
    {
        // The first ingest's save fails at its Uncategorized row; a later one's final save at
        // its close of the run, which that save writes with the rows.
        var fault = new ArmedFailCommand(firstIngest ? "INSERT INTO accounts" : "UPDATE ledger_operations");
        await using var w = await WorldAsync(interceptor: fault);
        var bank = await w.Ledger.AddBankAccountAsync("Checking");

        async Task<HttpResponseMessage> ImportAsync(string qif) =>
            await w.Client.PostAsync($"/api/ledgers/{w.Ledger.LedgerId}/ingest/qif/import", QifUpload(qif, bank.Id));

        var headersBefore = 0;
        if (!firstIngest)
        {
            using var earlier = await ImportAsync(QifFirst);
            Assert.Equal(HttpStatusCode.OK, earlier.StatusCode);
            headersBefore = 1;
        }

        fault.Arm();
        using (var failed = await ImportAsync(QifSecond))
        {
            Assert.True(fault.Fired, "the save never failed, so nothing was tested");
            Assert.False(failed.IsSuccessStatusCode, $"the failed import answered {(int)failed.StatusCode}");
        }
        await using (var read = Db.NewDbContext())
        {
            Assert.Equal(headersBefore, await read.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == w.Ledger.LedgerId));
            var run = await read.LedgerOperations.AsNoTracking()
                .Where(o => o.LedgerId == w.Ledger.LedgerId && o.Family == "ingest")
                .OrderByDescending(o => o.StartedAt).FirstAsync();
            Assert.Equal(("failed", IngestOrchestrator.ImportSaveFailedDetail), (run.Status, run.ErrorMessage));
        }
        if (firstIngest)
            Assert.False(await UncategorizedExistsAsync(w.Ledger.LedgerId), "the failed import saved its Uncategorized category");

        using (var saved = await ImportAsync(QifSecond))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        await AssertHealthyAsync(w.Ledger.LedgerId);
    }

    /// <summary>The file importer as the app builds it over a service context, with the QIF
    /// reader and nothing that reaches out.</summary>
    private static IngestOrchestrator QifImporter(AppDbContext db) => new(
        db,
        new SyncConnectionLock(),
        Array.Empty<IPullProvider>(),
        [new QifFileProvider()],
        new QuoteOrchestrator(
            db,
            Array.Empty<IQuotePullProvider>(),
            Array.Empty<IQuotePushProvider>(),
            new UserPreferencesRepository(db, TimeProvider.System),
            NullLogger<QuoteOrchestrator>.Instance,
            TimeProvider.System),
        NullLogger<IngestOrchestrator>.Instance,
        TimeProvider.System);

    /// <summary>
    /// A file import cancelled while it waits for the ledger's write lock, after its run row
    /// is saved, on the ledger's first ingest (the wait is the save that creates
    /// Uncategorized) and on a later one (the wait is the run's final save): the cancellation
    /// reaches the caller, nothing is saved, and the run is closed as cancelled, where the
    /// later one's was left running for good; imported again, the file is saved.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_import_cancelled_while_it_waits_for_the_ledger_closes_its_run_as_cancelled(bool firstIngest)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");

        async Task<IngestRunOutcome> ImportAsync(AppDbContext db, string qif, CancellationToken cancellationToken) =>
            await QifImporter(db).RunFileAsync(
                QifFileProvider.Key,
                new MemoryStream(Encoding.UTF8.GetBytes(qif), writable: false),
                new FileIngestContext(ledger.LedgerId, bank.Id, ledger.UserId, ProviderAccountId: QifFileProvider.SingleAccountKey),
                cancellationToken);

        var headersBefore = 0;
        if (!firstIngest)
        {
            await using var earlier = Db.NewServiceFactory().Create();
            Assert.Equal(1, (await ImportAsync(earlier, QifFirst, CancellationToken.None)).TransactionsForReview);
            headersBefore = 1;
        }

        var (holder, held) = await HoldLedgerAsync(ledger.LedgerId);
        await using (holder)
        await using (held)
        {
            await using var db = Db.NewServiceFactory().Create();
            await db.Database.OpenConnectionAsync();
            using var cancel = new CancellationTokenSource();
            var importing = ImportAsync(db, QifSecond, cancel.Token);
            await WaitForLockWaitersOrDoneAsync(1, importing, Pid(db));
            Assert.False(importing.IsCompleted, "the import never waited on the ledger's lock, so nothing was cancelled while it waited");
            Assert.Equal("advisory", await WaitEventAsync(Pid(db)));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);
        }

        await using (var read = Db.NewDbContext())
        {
            Assert.Equal(headersBefore, await read.TxnHeaders.AsNoTracking().CountAsync(h => h.LedgerId == ledger.LedgerId));
            var run = await read.LedgerOperations.AsNoTracking()
                .Where(o => o.LedgerId == ledger.LedgerId && o.Family == "ingest")
                .OrderByDescending(o => o.StartedAt).FirstAsync();
            Assert.Equal(("failed", IngestOrchestrator.ImportCancelledDetail), (run.Status, run.ErrorMessage));
            Assert.NotNull(run.CompletedAt);
        }
        if (firstIngest)
            Assert.False(await UncategorizedExistsAsync(ledger.LedgerId), "the cancelled import saved its Uncategorized category");

        await using (var again = Db.NewServiceFactory().Create())
            Assert.Equal(1, (await ImportAsync(again, QifSecond, CancellationToken.None)).TransactionsForReview);
        await AssertHealthyAsync(ledger.LedgerId);
    }

    // ----- the reminder auto-post -----

    private static readonly DateOnly SeriesStart = new(2026, 1, 1);

    private ReminderAutoPostJobHandler AutoPostHandler(bool shortCommandTimeout) =>
        new(shortCommandTimeout ? ShortTimeoutServiceFactory() : Db.NewServiceFactory(),
            new RecurrenceExpander(),
            new NotificationPublisher(
                Db.NewServiceFactory(), Db.NewLedgerKeyService(), Array.Empty<INotificationSubscriber>(),
                NullLogger<NotificationPublisher>.Instance, TimeProvider.System),
            NullLogger<ReminderAutoPostJobHandler>.Instance,
            TimeProvider.System,
            // Catch-up off, so the one occurrence inside the window is the only one in play.
            new ReminderAutoPostLimits(CatchUpDays: 0));

    /// <summary>
    /// The reminder auto-post run while another write holds the ledger's lock past the
    /// wait: the occurrence is deferred, not failed. The run reports that it did nothing
    /// rather than throwing toward the auto-disable, the occurrence is neither posted nor
    /// marked, the backlog announcement counts it, and the next run posts it.
    /// </summary>
    [Fact]
    public async Task An_auto_post_refused_because_the_ledger_is_being_written_is_deferred_and_the_next_run_posts_it()
    {
        await using var w = await WorldAsync();
        var bank = await w.Ledger.AddBankAccountAsync("Checking");
        var category = await w.Ledger.AddCategoryAsync("Category R");
        var made = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = SeriesStart,
                AutoCommitDaysBefore = 3,
                Payee = "Payee R",
                SourceAccountId = bank.Id,
                Postings = new[] { new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -150m } },
            });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var seriesId = (await made.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        // Two days before the 2026-04-01 slot, inside the three-day window.
        var clock = new JobRunClock(new DateTime(2026, 3, 30, 5, 0, 0, DateTimeKind.Utc), "UTC");

        async Task<int> PostedAsync()
        {
            await using var db = Db.NewDbContext();
            return await db.TxnHeaders.AsNoTracking()
                .CountAsync(h => h.RecurringTransactionId == seriesId && !h.IsRecurringTemplate);
        }

        JobRunOutcome refused;
        var (holder, held) = await HoldLedgerAsync(w.Ledger.LedgerId);
        await using (holder)
        await using (held)
        await using (var runner = Db.NewServiceDbContext())
        {
            refused = await AutoPostHandler(shortCommandTimeout: true)
                .RunAsync(runner, w.Ledger.LedgerId, w.Ledger.UserId, clock, default);
        }

        Assert.Equal(JobRunResult.Degraded, refused.Result);
        Assert.True(refused.NothingDone, "a run that posted nothing did not say so");
        Assert.Contains("still being saved", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, await PostedAsync());
        await using (var read = Db.NewDbContext())
        {
            var backlog = Assert.Single(await read.LedgerEvents.AsNoTracking()
                .Where(e => e.LedgerId == w.Ledger.LedgerId && e.EventKey == "reminder-backlog.deferred")
                .ToListAsync());
            Assert.Equal("1", JsonSerializer.Deserialize<Dictionary<string, string>>(backlog.DetailJson)!["ledger_busy"]);
        }

        JobRunOutcome posted;
        await using (var runner = Db.NewServiceDbContext())
        {
            posted = await AutoPostHandler(shortCommandTimeout: false)
                .RunAsync(runner, w.Ledger.LedgerId, w.Ledger.UserId, clock, default);
        }
        Assert.Equal(JobRunResult.Ok, posted.Result);
        Assert.Equal(1, await PostedAsync());
    }

    /// <summary>Before the first ledger write lock each fire's context sends (the fires in
    /// the order the run makes them), runs that fire's action, when it has one.</summary>
    private sealed class BeforeEachFire(params Func<Task>?[] actions) : DbCommandInterceptor
    {
        private readonly HashSet<DbContext> _seen = [];

        public int Fires
        {
            get { lock (_seen) return _seen.Count; }
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context
                && command.CommandText.Contains("fn_ledger_write_lock", StringComparison.Ordinal))
            {
                int fire;
                lock (_seen)
                {
                    fire = _seen.Add(context) ? _seen.Count - 1 : -1;
                }
                if (fire >= 0 && fire < actions.Length && actions[fire] is { } action) await action();
            }
            return result;
        }
    }

    /// <summary>
    /// A run of three due occurrences in which some fail and others are deferred because the
    /// ledger was being written, with none posted and with one posted: its message counts
    /// both, so a run with nothing posted throws saying how many failed and how many were
    /// deferred, and a run that posted some is degraded saying the same.
    /// </summary>
    /// <remarks>
    /// A fire fails when it waits at its occurrence's slot lock (migration 218), which
    /// another session holds, past the context's short command timeout; it is deferred when
    /// another session holds the ledger write lock past the wait. Each lock is taken just
    /// before the fire it is for takes the ledger write lock, so the fires before it are not
    /// in its way.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_auto_post_whose_fires_fail_and_are_deferred_counts_both_in_its_message(bool firstPosts)
    {
        await using var w = await WorldAsync();
        var bank = await w.Ledger.AddBankAccountAsync("Checking");
        var category = await w.Ledger.AddCategoryAsync("Category R");
        var series = new List<Guid>();
        foreach (var payee in new[] { "Payee R", "Payee S", "Payee T" })
        {
            var made = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders",
                new CreateReminderRequest
                {
                    Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                    StartDate = SeriesStart,
                    AutoCommitDaysBefore = 3,
                    Payee = payee,
                    SourceAccountId = bank.Id,
                    Postings = new[] { new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -150m } },
                });
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
            series.Add((await made.Content.ReadFromJsonAsync<ReminderDetail>())!.Id);
        }
        // Two days before the 2026-04-01 slot every series has, inside the three-day window.
        var slot = new DateOnly(2026, 4, 1);
        var clock = new JobRunClock(new DateTime(2026, 3, 30, 5, 0, 0, DateTimeKind.Utc), "UTC");

        await using var slots = new NpgsqlConnection(Db.ServiceConnectionString);
        await slots.OpenAsync();
        await using var slotsHeld = await slots.BeginTransactionAsync();
        async Task HoldSlotsAsync()
        {
            foreach (var id in series)
            {
                await using var take = new NpgsqlCommand("SELECT locked FROM reminder_occurrence_lock(@s, @d)", slots, slotsHeld);
                take.Parameters.AddWithValue("s", id);
                take.Parameters.AddWithValue("d", slot);
                await take.ExecuteScalarAsync();
            }
        }

        AppDbContext? holder = null;
        LedgerWriteScope? held = null;
        async Task HoldLedgerForTheRestAsync() => (holder, held) = await HoldLedgerAsync(w.Ledger.LedgerId);

        // Without one posted: the first fire fails, the other two are deferred. With one:
        // the first posts, the second fails, the third is deferred.
        BeforeEachFire fires;
        if (firstPosts)
        {
            fires = new BeforeEachFire(null, HoldSlotsAsync, HoldLedgerForTheRestAsync);
        }
        else
        {
            await HoldSlotsAsync();
            fires = new BeforeEachFire(null, HoldLedgerForTheRestAsync);
        }
        var handler = new ReminderAutoPostJobHandler(
            PostgresFixture.ServiceFactoryFor(Options.Create(new ApiOptions
            {
                ConnectionString = Db.AppConnectionString,
                ServiceConnectionString = WithShortCommandTimeout(Db.ServiceConnectionString),
            }), fires),
            new RecurrenceExpander(),
            new NotificationPublisher(
                Db.NewServiceFactory(), Db.NewLedgerKeyService(), Array.Empty<INotificationSubscriber>(),
                NullLogger<NotificationPublisher>.Instance, TimeProvider.System),
            NullLogger<ReminderAutoPostJobHandler>.Instance,
            TimeProvider.System,
            new ReminderAutoPostLimits(CatchUpDays: 0));

        JobRunOutcome? outcome = null;
        Exception? thrown = null;
        try
        {
            await using var runner = Db.NewServiceDbContext();
            outcome = await handler.RunAsync(runner, w.Ledger.LedgerId, w.Ledger.UserId, clock, default);
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }
        finally
        {
            if (held is not null) await held.DisposeAsync();
            if (holder is not null) await holder.DisposeAsync();
            await slotsHeld.RollbackAsync();
        }
        Assert.Equal(3, fires.Fires);

        await using var read = Db.NewDbContext();
        var posted = await read.TxnHeaders.AsNoTracking()
            .CountAsync(h => h.RecurringTransactionId != null && series.Contains(h.RecurringTransactionId.Value)
                             && !h.IsRecurringTemplate);
        var backlog = Assert.Single(await read.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == w.Ledger.LedgerId && e.EventKey == "reminder-backlog.deferred")
            .ToListAsync());
        var deferred = JsonSerializer.Deserialize<Dictionary<string, string>>(backlog.DetailJson)!["ledger_busy"];
        if (firstPosts)
        {
            Assert.Null(thrown);
            var run = Assert.NotNull(outcome);
            Assert.Equal(JobRunResult.Degraded, run.Result);
            Assert.False(run.NothingDone, "a run that posted one said it did nothing");
            Assert.Equal(
                "1 of 3 due occurrence(s) failed, and 1 was deferred because another change to this ledger was "
                + "still being saved (the next run posts it). See the server log for per-occurrence detail.",
                run.Message);
            Assert.Equal((1, "1"), (posted, deferred));
        }
        else
        {
            Assert.True(thrown is not null, $"a run that posted nothing did not throw: {outcome?.Result} {outcome?.Message}");
            Assert.Equal(
                "Auto-post posted nothing: 1 of 3 due occurrence(s) failed, and 2 were deferred because another "
                + "change to this ledger was still being saved (the next run posts them). See the server log for "
                + "per-occurrence detail.",
                thrown.Message);
            Assert.Equal((0, "2"), (posted, deferred));
        }
    }
}
