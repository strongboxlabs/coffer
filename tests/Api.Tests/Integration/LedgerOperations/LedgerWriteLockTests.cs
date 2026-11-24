using System.Data;
using System.Diagnostics;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Migrations;
using Coffer.Api.Snapshots;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// The ledger write lock (migration 275): one lock per ledger, taken first by every
/// transaction that writes to the ledger and can recompute it, refused where taking it
/// could not keep two such transactions from waiting on each other's rows.
/// </summary>
/// <remarks>
/// <para>Each refusal the database makes is tested here directly, on raw connections:
/// after a write or a row lock, at another isolation level, while another advisory lock is
/// held; and each exemption: the transaction that created the ledger, a migration's
/// session, the session form held by restore. Then the bounded wait, and the helper
/// (<see cref="LedgerWriteLock"/>) that opens the transaction, takes the lock and answers
/// the busy refusal, and snapshot restore, which takes the session form.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class LedgerWriteLockTests : DatabaseTest
{
    private const string InvalidTransactionState = "25000";
    private const string InvalidParameterValue = "22023";

    public LedgerWriteLockTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // ----- helpers -----

    private async Task<NpgsqlConnection> OpenAsync(string? connectionString = null)
    {
        var connection = new NpgsqlConnection(connectionString ?? Db.ServiceConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>The transaction form, on <paramref name="connection"/>'s current
    /// transaction (or as one autocommit statement).</summary>
    private static async Task<bool> LockAsync(NpgsqlConnection connection, Guid ledgerId, int? waitMs = null) =>
        (bool)(await (waitMs is { } wait
            ? ScalarAsync(connection, "SELECT locked FROM fn_ledger_write_lock(@l, @w)", ("l", ledgerId), ("w", wait))
            : ScalarAsync(connection, "SELECT locked FROM fn_ledger_write_lock(@l)", ("l", ledgerId))))!;

    private static async Task<PostgresException> RefusedAsync(Func<Task> act, string sqlState)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(act);
        Assert.Equal(sqlState, refused.SqlState);
        return refused;
    }

    /// <summary>A write that assigns the transaction an id and touches nothing the lock reads.</summary>
    private static Task WriteAsync(NpgsqlConnection connection, Guid accountId) =>
        ScalarAsync(connection, "UPDATE accounts SET name = name WHERE id = @a", ("a", accountId));

    private static async Task<bool> HasTransactionIdAsync(NpgsqlConnection connection) =>
        await ScalarAsync(connection, "SELECT txid_current_if_assigned()") is not DBNull and not null;

    private async Task<(SyntheticLedger Ledger, Guid Account)> LedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        return (ledger, bank.Id);
    }

    /// <summary>Wait until <paramref name="pid"/> waits on a lock, or <paramref name="running"/>
    /// is done. Asked as the superuser, who can read every role's wait events.</summary>
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

    private static int Pid(AppDbContext db) => ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;

    /// <summary>A spend from <paramref name="from"/> to <paramref name="to"/>, saved through a
    /// context wired as production's service context (its interceptors recompute), with the
    /// ledger's write lock taken first.</summary>
    private async Task<Guid> SeedSpendAsync(SyntheticLedger ledger, Guid from, Guid to, DateTime postedAt)
    {
        var headerId = Guid.NewGuid();
        await using var db = Db.NewServiceFactory().Create();
        AddSpend(db, ledger.LedgerId, headerId, from, to, postedAt);
        await db.SaveChangesUnderLedgerLockAsync(ledger.LedgerId);
        return headerId;
    }

    private static void AddSpend(AppDbContext db, Guid ledgerId, Guid headerId, Guid from, Guid to, DateTime postedAt)
    {
        var header = TxnHeaderRow.NewTransaction(headerId, ledgerId, from);
        header.Payee = "Market";
        header.PostedAt = postedAt;
        header.TransactedAt = postedAt;
        db.AddHeader(header, createdAt: postedAt);
        db.Add(new TxnLegRow { Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId, AccountId = from, PostingIndex = 0, Amount = -10m });
        db.Add(new TxnLegRow { Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId, AccountId = to, PostingIndex = 1, Amount = 10m });
    }

    private static readonly DateTime Day = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    // ----- taking it -----

    [Fact]
    public async Task Taken_first_it_is_held_until_the_transaction_ends_and_taken_again_it_does_nothing()
    {
        var (ledger, account) = await LedgerAsync();
        await using var holder = await OpenAsync();
        await using var other = await OpenAsync();
        await using (var tx = await holder.BeginTransactionAsync())
        {
            Assert.True(await LockAsync(holder, ledger.LedgerId));
            await RefusedAsync(() => LockAsync(other, ledger.LedgerId, waitMs: 200), LedgerWriteLock.BusySqlState);

            // Held, so a second call after a write does nothing rather than refusing.
            await WriteAsync(holder, account);
            Assert.True(await LockAsync(holder, ledger.LedgerId));
            await tx.CommitAsync();
        }
        Assert.True(await LockAsync(other, ledger.LedgerId, waitMs: 200));
    }

    /// <summary>A second call in a transaction that holds the lock returns on the
    /// transaction-local marker, which names each ledger taken and ends with the transaction
    /// (or with a savepoint rolled back, which releases the lock too).</summary>
    [Fact]
    public async Task Its_marker_names_each_ledger_taken_and_ends_with_the_transaction_or_the_savepoint()
    {
        var (first, _) = await LedgerAsync();
        await using var connection = await OpenAsync();
        async Task<string> MarkerAsync() =>
            await ScalarAsync(connection, "SELECT coalesce(current_setting('coffer.ledger_write_held', true), '')") as string ?? "";

        await using (var tx = await connection.BeginTransactionAsync())
        {
            Assert.True(await LockAsync(connection, first.LedgerId));
            Assert.Equal(first.LedgerId.ToString(), await MarkerAsync());
            await tx.CommitAsync();
        }
        Assert.Equal("", await MarkerAsync());

        await using (var tx = await connection.BeginTransactionAsync())
        {
            await tx.SaveAsync("before_lock");
            Assert.True(await LockAsync(connection, first.LedgerId));
            await tx.RollbackAsync("before_lock");
            Assert.Equal("", await MarkerAsync());
            await using var other = await OpenAsync();
            Assert.True(await LockAsync(other, first.LedgerId, waitMs: 200));
        }
    }

    [Fact]
    public async Task A_single_statement_outside_a_transaction_takes_it_for_that_statement_alone()
    {
        var (ledger, account) = await LedgerAsync();
        await using var connection = await OpenAsync();
        Assert.True(await LockAsync(connection, ledger.LedgerId));

        // Released when the statement ended: another session takes it at once.
        await using (var other = await OpenAsync())
        await using (var otherTx = await other.BeginTransactionAsync())
            Assert.True(await LockAsync(other, ledger.LedgerId, waitMs: 200));

        // And it did not outlive the statement on its own session either: a transaction
        // there that writes first is refused.
        await using var tx = await connection.BeginTransactionAsync();
        await WriteAsync(connection, account);
        await RefusedAsync(() => LockAsync(connection, ledger.LedgerId), InvalidTransactionState);
    }

    // ----- the refusals -----

    [Fact]
    public async Task A_transaction_that_wrote_before_taking_it_is_refused()
    {
        var (ledger, account) = await LedgerAsync();
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await WriteAsync(connection, account);
        var refused = await RefusedAsync(() => LockAsync(connection, ledger.LedgerId), InvalidTransactionState);
        Assert.Contains("after this transaction wrote or locked a row", refused.MessageText, StringComparison.Ordinal);
    }

    /// <summary>A row lock assigns the transaction an id as a write does, so the check sees
    /// migration 264's and 274's row locks: FOR UPDATE (fn_lock_accounts), FOR NO KEY
    /// UPDATE (fn_lock_txn_headers) and the trade-price FOR SHARE
    /// (fn_lock_trade_price_accounts).</summary>
    [Theory]
    [InlineData("SELECT locked FROM fn_lock_accounts(ARRAY[@a]::uuid[])")]
    [InlineData("SELECT locked FROM fn_lock_txn_headers(ARRAY(SELECT h.id FROM txn_headers h WHERE h.ledger_id = @l))")]
    [InlineData("SELECT locked FROM fn_lock_trade_price_accounts(ARRAY[@a]::uuid[], false)")]
    public async Task A_transaction_that_only_locked_a_row_before_taking_it_is_refused(string rowLock)
    {
        var (ledger, account) = await LedgerAsync();
        var category = await ledger.AddCategoryAsync("Groceries");
        await SeedSpendAsync(ledger, account, category.Id, Day);

        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        Assert.False(await HasTransactionIdAsync(connection));
        var locked = (int)(await ScalarAsync(connection, rowLock, ("a", account), ("l", ledger.LedgerId)))!;
        Assert.True(locked > 0, "the statement locked no row, so nothing was tested");
        Assert.True(await HasTransactionIdAsync(connection), "a row lock left the transaction without an id");
        await RefusedAsync(() => LockAsync(connection, ledger.LedgerId), InvalidTransactionState);
    }

    [Fact]
    public async Task It_refuses_any_isolation_level_but_read_committed()
    {
        var (ledger, _) = await LedgerAsync();
        await using var connection = await OpenAsync();
        foreach (var level in new[] { IsolationLevel.RepeatableRead, IsolationLevel.Serializable })
        {
            await using var tx = await connection.BeginTransactionAsync(level);
            var refused = await RefusedAsync(() => LockAsync(connection, ledger.LedgerId), InvalidTransactionState);
            Assert.Contains("READ COMMITTED", refused.MessageText, StringComparison.Ordinal);
            await tx.RollbackAsync();
        }
        await using (var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted))
            Assert.True(await LockAsync(connection, ledger.LedgerId));
    }

    [Fact]
    public async Task Holding_one_ledgers_lock_does_not_stand_for_another_ledgers()
    {
        var (first, account) = await LedgerAsync();
        var second = await SyntheticLedger.CreateAsync(Db);
        await using var connection = await OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        Assert.True(await LockAsync(connection, first.LedgerId));
        await WriteAsync(connection, account);
        await RefusedAsync(() => LockAsync(connection, second.LedgerId), InvalidTransactionState);
    }

    [Fact]
    public async Task It_refuses_a_null_ledger_and_a_wait_that_is_not_positive()
    {
        var (ledger, _) = await LedgerAsync();
        await using var connection = await OpenAsync();
        await RefusedAsync(() => ScalarAsync(connection, "SELECT locked FROM fn_ledger_write_lock(NULL::uuid)"), InvalidParameterValue);
        await RefusedAsync(() => LockAsync(connection, ledger.LedgerId, waitMs: 0), InvalidParameterValue);
        await RefusedAsync(() => ScalarAsync(connection,
            "SELECT locked FROM fn_ledger_write_session_lock(@l, 0)", ("l", ledger.LedgerId)), InvalidParameterValue);
    }

    // ----- the exemptions -----

    /// <summary>The importer's case: the transaction that inserted the ledger row may take its
    /// lock after writing, since no other session can see the ledger. An update of an existing
    /// ledger's row does not make a transaction its creator.</summary>
    [Fact]
    public async Task The_transaction_that_created_the_ledger_may_take_it_after_writing_and_one_that_updated_it_may_not()
    {
        var (existing, _) = await LedgerAsync();
        await using var connection = await OpenAsync();
        var created = Guid.NewGuid();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await ScalarAsync(connection, "INSERT INTO ledgers (id, name) VALUES (@l, 'created here')", ("l", created));
            await ScalarAsync(connection,
                "INSERT INTO accounts (id, ledger_id, name, account_type, currency_code, opening_balance, is_active) "
                + "VALUES (gen_random_uuid(), @l, 'Checking', 'bank', 'USD', 0, true)",
                ("l", created));
            Assert.True(await LockAsync(connection, created));
            await tx.RollbackAsync();
        }
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await ScalarAsync(connection, "UPDATE ledgers SET name = name WHERE id = @l", ("l", existing.LedgerId));
            await RefusedAsync(() => LockAsync(connection, existing.LedgerId), InvalidTransactionState);
        }
    }

    /// <summary>
    /// A migration that writes (here, DDL and an insert) and then takes the lock, as a data
    /// migration that recomputes balances or holdings does, runs: the runner marks its
    /// session, and the function does nothing there.
    /// </summary>
    [Fact]
    public async Task A_migration_run_by_the_runner_may_take_it_after_writing()
    {
        var (ledger, _) = await LedgerAsync();
        var work = Directory.CreateTempSubdirectory("coffer-mig275-probe-").FullName;
        var source = MigrationsDirectoryLocator.Locate(AppContext.BaseDirectory);
        foreach (var script in Directory.GetFiles(source, "*.sql"))
            File.Copy(script, Path.Combine(work, Path.GetFileName(script)));
        await File.WriteAllTextAsync(Path.Combine(work, "999_ledger_write_lock_probe.sql"), $"""
            BEGIN;
            CREATE TEMP TABLE ledger_write_lock_probe (x integer) ON COMMIT DROP;
            INSERT INTO ledger_write_lock_probe VALUES (1);
            SELECT locked FROM fn_ledger_write_lock('{ledger.LedgerId}');
            COMMIT;
            """);

        MigrationRunner.Run(TestDatabaseCluster.ForMigrationRunner(Db.ServiceConnectionString), work, NullLogger.Instance);

        await using var connection = await OpenAsync();
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT count(*) FROM __schema_migrations WHERE scriptname = '999_ledger_write_lock_probe.sql'"));
    }

    [Fact]
    public async Task Only_a_session_marked_as_a_migrations_is_exempt()
    {
        var (ledger, account) = await LedgerAsync();
        await using (var plain = await OpenAsync())
        await using (var tx = await plain.BeginTransactionAsync())
        {
            await WriteAsync(plain, account);
            await RefusedAsync(() => LockAsync(plain, ledger.LedgerId), InvalidTransactionState);
        }
        await using (var marked = await OpenAsync(MigrationRunner.ForMigrationSession(Db.ServiceConnectionString)))
        await using (var tx = await marked.BeginTransactionAsync())
        {
            await WriteAsync(marked, account);
            Assert.True(await LockAsync(marked, ledger.LedgerId));
        }
    }

    // ----- the recompute functions take it -----

    private static PostgresException? Postgres(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            if (e is PostgresException p) return p;
        return null;
    }

    /// <summary>The balances and holdings recomputes take their ledger's write lock, so a
    /// transaction that wrote or row-locked without it is refused there: a writer that
    /// misses the lock fails rather than races. One that took it first recomputes.</summary>
    [Theory]
    [InlineData("SELECT fn_recompute_balances_for_account(@a, '0001-01-01'::timestamptz)")]
    [InlineData("SELECT recompute_holdings_cost_basis(NULL, @a, NULL)")]
    [InlineData("SELECT recompute_holdings_cost_basis(@l)")]
    public async Task A_recompute_in_a_transaction_that_locked_a_row_without_the_lock_is_refused(string recompute)
    {
        var (ledger, account) = await LedgerAsync();
        await using var connection = await OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await ScalarAsync(connection, "SELECT locked FROM fn_lock_accounts(ARRAY[@a]::uuid[])", ("a", account));
            await RefusedAsync(() => ScalarAsync(connection, recompute, ("a", account), ("l", ledger.LedgerId)), InvalidTransactionState);
        }
        await using (var tx = await connection.BeginTransactionAsync())
        {
            Assert.True(await LockAsync(connection, ledger.LedgerId));
            await ScalarAsync(connection, "SELECT locked FROM fn_lock_accounts(ARRAY[@a]::uuid[])", ("a", account));
            await ScalarAsync(connection, recompute, ("a", account), ("l", ledger.LedgerId));
            await tx.RollbackAsync();
        }
    }

    /// <summary>A recompute run as a statement of its own, as the consistency repairs run
    /// one per account or position, takes the lock for that statement: it waits for a
    /// holder, and its lock ends with the statement.</summary>
    [Fact]
    public async Task A_recompute_run_as_a_statement_of_its_own_waits_for_a_holder_and_holds_the_lock_for_that_statement_alone()
    {
        var (ledger, account) = await LedgerAsync();
        var category = await ledger.AddCategoryAsync("Groceries");
        await SeedSpendAsync(ledger, account, category.Id, Day);
        await using var holder = await OpenAsync();
        await using var holding = await holder.BeginTransactionAsync();
        Assert.True(await LockAsync(holder, ledger.LedgerId));

        await using var repair = await OpenAsync();
        var running = ScalarAsync(repair, "SELECT fn_recompute_balances_for_account(@a, '0001-01-01'::timestamptz)", ("a", account));
        await WaitForLockWaitOrDoneAsync(repair.ProcessID, running);
        Assert.False(running.IsCompleted, "the recompute did not wait for the holder of its ledger's lock");
        await holding.RollbackAsync();
        await running;

        await using var other = await OpenAsync();
        Assert.True(await LockAsync(other, ledger.LedgerId, waitMs: 200));
    }

    /// <summary>A save whose interceptors recompute is refused unless the ledger's write
    /// lock came first; the test-side seeding helper takes it.</summary>
    [Fact]
    public async Task A_save_that_recomputes_is_refused_without_the_lock_and_saved_through_the_seeding_helper()
    {
        var (ledger, account) = await LedgerAsync();
        var category = await ledger.AddCategoryAsync("Groceries");
        await using (var db = Db.NewServiceFactory().Create())
        {
            AddSpend(db, ledger.LedgerId, Guid.NewGuid(), account, category.Id, Day);
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
            Assert.Equal(InvalidTransactionState, Postgres(refused)?.SqlState);
        }

        var headerId = await SeedSpendAsync(ledger, account, category.Id, Day);
        await using var read = Db.NewDbContext();
        var balance = await read.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(b => b.HeaderId == headerId && b.AccountId == account);
        Assert.Equal(-10m, balance.BalanceAfter);
    }

    // ----- the order among advisory locks -----

    [Fact]
    public async Task It_comes_before_every_other_advisory_lock_but_the_ledgers_snapshot_create_lock()
    {
        var (ledger, _) = await LedgerAsync();
        var other = await SyntheticLedger.CreateAsync(Db);
        await using var connection = await OpenAsync();

        // The reminder slot lock first.
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await ScalarAsync(connection, "SELECT locked FROM reminder_occurrence_lock(@s, DATE '2026-05-01')", ("s", Guid.NewGuid()));
            var refused = await RefusedAsync(() => LockAsync(connection, ledger.LedgerId), InvalidTransactionState);
            Assert.Contains("another advisory lock", refused.MessageText, StringComparison.Ordinal);
        }

        // Another ledger's write lock first.
        await using (var tx = await connection.BeginTransactionAsync())
        {
            Assert.True(await LockAsync(connection, other.LedgerId));
            await RefusedAsync(() => LockAsync(connection, ledger.LedgerId), InvalidTransactionState);
        }

        // Another ledger's snapshot-create lock held by the session.
        await ScalarAsync(connection, "SELECT locked FROM snapshot_ledger_lock(@l)", ("l", other.LedgerId));
        await using (var tx = await connection.BeginTransactionAsync())
            await RefusedAsync(() => LockAsync(connection, ledger.LedgerId), InvalidTransactionState);
        await ScalarAsync(connection, "SELECT unlocked FROM snapshot_ledger_unlock(@l)", ("l", other.LedgerId));

        // The same ledger's snapshot-create lock held by the session: it comes first.
        await ScalarAsync(connection, "SELECT locked FROM snapshot_ledger_lock(@l)", ("l", ledger.LedgerId));
        await using (var tx = await connection.BeginTransactionAsync())
            Assert.True(await LockAsync(connection, ledger.LedgerId));
        await ScalarAsync(connection, "SELECT unlocked FROM snapshot_ledger_unlock(@l)", ("l", ledger.LedgerId));
    }

    [Fact]
    public async Task The_snapshot_create_lock_refuses_a_session_holding_the_ledgers_write_lock()
    {
        var (ledger, _) = await LedgerAsync();
        await using var connection = await OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            Assert.True(await LockAsync(connection, ledger.LedgerId));
            await RefusedAsync(() => ScalarAsync(connection, "SELECT locked FROM snapshot_ledger_lock(@l)", ("l", ledger.LedgerId)),
                InvalidTransactionState);
        }
        await ScalarAsync(connection, "SELECT locked FROM fn_ledger_write_session_lock(@l)", ("l", ledger.LedgerId));
        await RefusedAsync(() => ScalarAsync(connection, "SELECT locked FROM snapshot_ledger_lock(@l)", ("l", ledger.LedgerId)),
            InvalidTransactionState);
        Assert.True((bool)(await ScalarAsync(connection, "SELECT unlocked FROM fn_ledger_write_session_unlock(@l)", ("l", ledger.LedgerId)))!);
    }

    // ----- the wait -----

    [Fact]
    public async Task A_wait_past_its_bound_is_refused_as_busy_and_the_callers_lock_timeout_comes_back()
    {
        var (ledger, _) = await LedgerAsync();
        await using var holder = await OpenAsync();
        await using var holding = await holder.BeginTransactionAsync();
        Assert.True(await LockAsync(holder, ledger.LedgerId));

        await using var waiter = await OpenAsync();
        var watch = Stopwatch.StartNew();
        await using (var tx = await waiter.BeginTransactionAsync())
        {
            var busy = await RefusedAsync(() => LockAsync(waiter, ledger.LedgerId, waitMs: 300), LedgerWriteLock.BusySqlState);
            Assert.Contains("being written by another transaction", busy.MessageText, StringComparison.Ordinal);
        }
        Assert.InRange(watch.ElapsedMilliseconds, 250, 10_000);
        await holding.RollbackAsync();

        // Granted: the transaction's own lock_timeout is back for its later waits.
        await using var granted = await waiter.BeginTransactionAsync();
        await ScalarAsync(waiter, "SET LOCAL lock_timeout = '7s'");
        Assert.True(await LockAsync(waiter, ledger.LedgerId, waitMs: 300));
        Assert.Equal("7s", await ScalarAsync(waiter, "SHOW lock_timeout"));
    }

    /// <summary>The helper waits half the context's command timeout (15 s on the API's
    /// default 30 s, the functions' own default), so a held lock is refused as
    /// <see cref="LedgerWriteBusyException"/> before the command timeout cancels the
    /// statement, and a transaction the helper opened is rolled back.</summary>
    [Fact]
    public async Task Through_the_helper_a_held_lock_is_refused_as_busy_before_the_command_timeout()
    {
        var (ledger, _) = await LedgerAsync();
        await using (var plain = Db.NewDbContext())
            Assert.Equal(LedgerWriteLock.DefaultWaitMilliseconds, LedgerWriteLock.WaitMilliseconds(plain));

        await using var holder = Db.NewDbContext();
        await using var held = await holder.BeginLedgerWriteAsync(ledger.LedgerId);

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString, o => o.CommandTimeout(2)).Options);
        Assert.Equal(1_000, LedgerWriteLock.WaitMilliseconds(db));
        // Busy, not a timeout: the command timeout would cancel the statement and surface
        // as a different exception, so the type alone says the wait ended first. No clock.
        var busy = await Assert.ThrowsAsync<LedgerWriteBusyException>(() => db.BeginLedgerWriteAsync(ledger.LedgerId));
        Assert.Equal(ledger.LedgerId, busy.LedgerId);
        Assert.Null(db.Database.CurrentTransaction);

        var session = await Assert.ThrowsAsync<LedgerWriteBusyException>(() =>
            db.HoldLedgerWriteSessionAsync(ledger.LedgerId, NullLogger.Instance));
        Assert.Equal(ledger.LedgerId, session.LedgerId);
        Assert.NotEqual(ConnectionState.Open, db.Database.GetDbConnection().State);
    }

    // ----- the helper -----

    [Fact]
    public async Task The_helper_opens_a_read_committed_transaction_and_commits_or_rolls_back_only_that()
    {
        var (ledger, account) = await LedgerAsync();
        async Task<string> NameAsync()
        {
            await using var read = Db.NewDbContext();
            return await read.Accounts.AsNoTracking().Where(a => a.Id == account).Select(a => a.Name).SingleAsync();
        }

        // No transaction open: it opens one, READ COMMITTED, and commits it.
        await using (var db = Db.NewDbContext())
        {
            await using (var write = await db.BeginLedgerWriteAsync(ledger.LedgerId))
            {
                Assert.True(write.OwnsTransaction);
                Assert.Equal(IsolationLevel.ReadCommitted, db.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel);
                await db.Accounts.Where(a => a.Id == account).ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "Committed"));
                await write.CommitAsync();
            }
            Assert.Null(db.Database.CurrentTransaction);
        }
        Assert.Equal("Committed", await NameAsync());

        // Disposed uncommitted: rolled back.
        await using (var db = Db.NewDbContext())
        await using (var write = await db.BeginLedgerWriteAsync(ledger.LedgerId))
            await db.Accounts.Where(a => a.Id == account).ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "Rolled back"));
        Assert.Equal("Committed", await NameAsync());

        // Inside the caller's transaction: committing and disposing the scope leave it open.
        await using (var db = Db.NewDbContext())
        {
            await using var caller = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await using (var write = await db.BeginLedgerWriteAsync(ledger.LedgerId))
            {
                Assert.False(write.OwnsTransaction);
                await db.Accounts.Where(a => a.Id == account).ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "Caller's"));
                await write.CommitAsync();
            }
            Assert.Same(caller, db.Database.CurrentTransaction);
            await caller.RollbackAsync();
        }
        Assert.Equal("Committed", await NameAsync());

        // A caller's transaction that wrote first: the refusal reaches the caller as it is.
        await using (var db = Db.NewDbContext())
        {
            await using var caller = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await db.Accounts.Where(a => a.Id == account).ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, "First"));
            var refused = await Assert.ThrowsAsync<PostgresException>(() => db.BeginLedgerWriteAsync(ledger.LedgerId));
            Assert.Equal(InvalidTransactionState, refused.SqlState);
            Assert.Same(caller, db.Database.CurrentTransaction);
        }
    }

    // ----- the session form -----

    [Fact]
    public async Task While_a_session_holds_the_session_form_the_transaction_form_does_nothing_there_and_waits_elsewhere()
    {
        var (ledger, account) = await LedgerAsync();
        await using var connection = await OpenAsync();
        await using var other = await OpenAsync();
        Assert.True((bool)(await ScalarAsync(connection, "SELECT locked FROM fn_ledger_write_session_lock(@l)", ("l", ledger.LedgerId)))!);

        // On its own session: nothing, even in a REPEATABLE READ transaction that wrote.
        await using (var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead))
        {
            await WriteAsync(connection, account);
            Assert.True(await LockAsync(connection, ledger.LedgerId));
            await tx.RollbackAsync();
        }

        // On another: held.
        await using (var tx = await other.BeginTransactionAsync())
            await RefusedAsync(() => LockAsync(other, ledger.LedgerId, waitMs: 200), LedgerWriteLock.BusySqlState);

        Assert.True((bool)(await ScalarAsync(connection, "SELECT unlocked FROM fn_ledger_write_session_unlock(@l)", ("l", ledger.LedgerId)))!);
        await using (var tx = await other.BeginTransactionAsync())
            Assert.True(await LockAsync(other, ledger.LedgerId, waitMs: 200));
    }

    [Fact]
    public async Task The_session_form_refuses_after_a_write_and_after_another_advisory_lock()
    {
        var (ledger, account) = await LedgerAsync();
        await using var connection = await OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await WriteAsync(connection, account);
            await RefusedAsync(() => ScalarAsync(connection, "SELECT locked FROM fn_ledger_write_session_lock(@l)", ("l", ledger.LedgerId)),
                InvalidTransactionState);
        }
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await ScalarAsync(connection, "SELECT locked FROM reminder_occurrence_lock(@s, DATE '2026-05-01')", ("s", Guid.NewGuid()));
            await RefusedAsync(() => ScalarAsync(connection, "SELECT locked FROM fn_ledger_write_session_lock(@l)", ("l", ledger.LedgerId)),
                InvalidTransactionState);
        }
    }

    // ----- snapshot restore takes the session form -----

    private async Task<(SyntheticLedger Ledger, Guid SnapshotId)> SnapshottedLedgerAsync()
    {
        var (ledger, account) = await LedgerAsync();
        var category = await ledger.AddCategoryAsync("Groceries");
        await SeedSpendAsync(ledger, account, category.Id, Day);
        await using var db = Db.NewDbContext();
        var created = await new LedgerSnapshotsRepository(db, NullLogger<LedgerSnapshotsRepository>.Instance, TimeProvider.System)
            .CreateAsync(ledger.LedgerId, "manual", ledger.UserId, "baseline");
        Assert.Equal(LedgerSnapshotsRepository.CreateOutcome.Created, created.Outcome);
        return (ledger, created.Row!.Id);
    }

    [Fact]
    public async Task A_restore_waits_for_a_write_holding_the_ledgers_write_lock()
    {
        var (ledger, snapshotId) = await SnapshottedLedgerAsync();
        await using var holder = Db.NewDbContext();
        var held = await holder.BeginLedgerWriteAsync(ledger.LedgerId);

        await using var restoreDb = Db.NewDbContext();
        await restoreDb.Database.OpenConnectionAsync();
        var restoring = new LedgerSnapshotsRepository(restoreDb, NullLogger<LedgerSnapshotsRepository>.Instance, TimeProvider.System)
            .RestoreAsync(ledger.LedgerId, snapshotId);
        await WaitForLockWaitOrDoneAsync(Pid(restoreDb), restoring);
        Assert.False(restoring.IsCompleted, "the restore did not wait for the write holding the ledger's lock");

        await held.DisposeAsync();
        Assert.Equal(LedgerSnapshotsRepository.RestoreOutcome.Restored, await restoring);
    }

    [Fact]
    public async Task A_write_waits_for_a_restore_in_flight_and_inside_the_restore_the_transaction_form_does_nothing()
    {
        var (ledger, snapshotId) = await SnapshottedLedgerAsync();
        await using var restoreDb = Db.NewDbContext();
        await using var writerDb = Db.NewDbContext();
        await writerDb.Database.OpenConnectionAsync();
        Task<LedgerWriteScope>? writing = null;
        var waited = false;
        var insideRestore = false;

        var restored = await new LedgerSnapshotsRepository(restoreDb, NullLogger<LedgerSnapshotsRepository>.Instance, TimeProvider.System)
            .RestoreAsync(ledger.LedgerId, snapshotId, beforeCommit: async ct =>
            {
                // The restore's own REPEATABLE READ transaction has rewritten the ledger:
                // the session form it holds makes the transaction form a no-op here.
                insideRestore = await restoreDb.LedgerWriteLockFn(ledger.LedgerId, 1_000).Select(r => r.Locked).FirstAsync(ct);

                writing = writerDb.BeginLedgerWriteAsync(ledger.LedgerId, ct);
                await WaitForLockWaitOrDoneAsync(Pid(writerDb), writing);
                waited = !writing.IsCompleted;
            });

        Assert.Equal(LedgerSnapshotsRepository.RestoreOutcome.Restored, restored);
        Assert.True(insideRestore);
        Assert.True(waited, "the write did not wait for the restore in flight");
        await using var write = await writing!;
        Assert.True(write.OwnsTransaction);
    }
}
