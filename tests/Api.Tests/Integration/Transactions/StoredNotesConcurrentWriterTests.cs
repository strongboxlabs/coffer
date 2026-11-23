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
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// A writer that reads which headers to recompute before it writes must not miss a
/// header another request changes in between: the stored presentation of that header
/// would be left as it was, with nothing to recompute it.
/// </summary>
/// <remarks>
/// <para>Two shapes of the race. In one, another request holds an uncommitted write
/// (a tag link, a leg) that the writer's change will touch, and commits while the writer
/// runs; the writer must wait for it and recompute its header. In the other, another
/// request commits a new row between the writer's read and its write; the writer must
/// either wait for it or leave it alone.</para>
/// <para>Each test ends with the stored-equals-computed check over the ledger.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class StoredNotesConcurrentWriterTests : DatabaseTest
{
    public StoredNotesConcurrentWriterTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Day(int month, int day) => new(2026, month, day, 12, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        SyntheticLedger Ledger, HttpClient Client, ApiFactory Factory,
        AccountRow BankA, AccountRow BankB, AccountRow CatA, AccountRow CatB) : IAsyncDisposable
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
        var bankA = await ledger.AddBankAccountAsync("Account A");
        var bankB = await ledger.AddBankAccountAsync("Account B");
        var catA = await ledger.AddCategoryAsync("Category A");
        var catB = await ledger.AddCategoryAsync("Category B");
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return new World(ledger, client, factory, bankA, bankB, catA, catB);
    }

    private static async Task<Guid> CreateBankAsync(World w, Guid source, DateTime postedAt, string payee, Guid counterparty, decimal amount)
    {
        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = payee,
                SourceAccountId = source,
                Postings = [new TransactionPosting { CounterpartyAccountId = counterparty, Amount = amount }],
            });
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private async Task<Guid> LegOnAsync(Guid headerId, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == accountId)
            .Select(l => l.Id).SingleAsync();
    }

    private async Task<Guid> AddTagAsync(World w, string name)
    {
        var id = Guid.NewGuid();
        await using var db = Db.NewServiceFactory().Create();
        db.Tags.Add(new TagRow { Id = id, LedgerId = w.Ledger.LedgerId, Name = name, CreatedAt = Day(1, 1) });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task AssertStoredAsync(World w)
    {
        await using var db = Db.NewDbContext();
        var differences = await LegNotes.DifferencesAsync(db, w.Ledger.LedgerId, CancellationToken.None);
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    /// <summary>
    /// Another request's write held open: a production-wired context in its own
    /// transaction, its save (and that save's recompute) done and not yet committed.
    /// </summary>
    private sealed class Held : IAsyncDisposable
    {
        public required AppDbContext Db { get; init; }
        public required Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Transaction { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private async Task<Held> HoldAsync(Action<AppDbContext> write)
    {
        var db = Db.NewServiceFactory().Create();
        var tx = await db.Database.BeginTransactionAsync();
        write(db);
        await db.SaveChangesAsync();
        return new Held { Db = db, Transaction = tx };
    }

    /// <summary>
    /// Run <paramref name="work"/> on <paramref name="db"/> until it is done or waits on a
    /// row lock; then commit <paramref name="held"/> and let the work finish.
    /// </summary>
    private async Task RunPastAsync(AppDbContext db, Held held, Func<Task> work)
    {
        await db.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        var running = work();
        await using (var probe = Db.OpenServiceConnection())
        {
            for (var i = 0; i < 400 && !running.IsCompleted; i++)
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock'", probe);
                cmd.Parameters.AddWithValue("pid", pid);
                if ((long)(await cmd.ExecuteScalarAsync())! > 0) break;
                await Task.Delay(25);
            }
        }
        await held.Transaction.CommitAsync();
        await running;
    }

    /// <summary>Runs <c>action</c> once, just before the first command whose text <c>match</c> accepts.</summary>
    private sealed class BeforeCommand(Func<string, bool> match, Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;

        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (match(command.CommandText) && Interlocked.Exchange(ref _fired, 1) == 0) await action();
            return result;
        }
    }

    /// <summary>A context wired as production wires it, with <paramref name="extra"/> before its interceptors.</summary>
    private AppDbContext ProductionContext(IInterceptor? extra = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Db.ServiceConnectionString);
        if (extra is not null) builder.AddInterceptors(extra);
        builder.AddInterceptors(
            new LegDerivedRecomputeInterceptor(Microsoft.Extensions.Logging.Abstractions.NullLogger<LegDerivedRecomputeInterceptor>.Instance),
            new HoldingsRecomputeInterceptor(),
            new TradePriceFromLegInterceptor(),
            new LegNotesRecomputeInterceptor());
        return new AppDbContext(builder.Options);
    }

    /// <summary>
    /// Start a request that writes past the writer and wait for it while it can finish,
    /// but not for longer: a request the writer's lock holds back finishes after it.
    /// </summary>
    private static async Task<Task> StartAsync(Func<Task> request, List<Task> later)
    {
        var task = request();
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
        if (!task.IsCompleted) later.Add(task);
        else await task;
        return task;
    }

    private static TxnLegTagRow LegTag(Guid leg, Guid tag, Guid ledger) =>
        new() { LegId = leg, TagId = tag, LedgerId = ledger, CreatedAt = Day(1, 2) };

    [Fact]
    public async Task A_tag_deleted_while_another_request_assigns_it_recomputes_that_transaction()
    {
        // The bank side's own tag is what its category side shows, inherited. Deleting the
        // tag takes the assignment with it, so the category side must stop inheriting.
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(3, 1), "Payee Tag Race", w.CatA.Id, -10m);
        var bankLeg = await LegOnAsync(h, w.BankA.Id);
        var tag = await AddTagAsync(w, "t-race");

        await using var held = await HoldAsync(db => db.TxnLegTags.Add(LegTag(bankLeg, tag, w.Ledger.LedgerId)));
        await using var db = ProductionContext();
        await RunPastAsync(db, held, () => new TagsRepository(db).DeleteAsync(w.Ledger.LedgerId, tag));
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_tag_merged_while_another_request_assigns_it_moves_that_assignment_and_recomputes_it()
    {
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(3, 2), "Payee Merge Race", w.CatA.Id, -11m);
        var bankLeg = await LegOnAsync(h, w.BankA.Id);
        var source = await AddTagAsync(w, "t-source");
        var into = await AddTagAsync(w, "t-into");

        await using var held = await HoldAsync(db => db.TxnLegTags.Add(LegTag(bankLeg, source, w.Ledger.LedgerId)));
        // As a request runs it: the merge repoints saved reports, which checks the
        // merging member's grant, so the connection carries the member's user.
        await using var db = Db.NewRequestDbContextAsUser(w.Ledger.UserId);
        await RunPastAsync(db, held, () => new TagsRepository(db).MergeAsync(w.Ledger.LedgerId, source, into));

        await using (var check = Db.NewDbContext())
            Assert.True(await check.TxnLegTags.AnyAsync(t => t.LegId == bankLeg && t.TagId == into),
                "the assignment made while the merge ran was lost instead of moved to the merged tag");
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task Cleanup_keeps_a_tag_another_request_assigns_while_it_runs()
    {
        // An unused tag that another request is assigning is not unused: cleanup must not
        // take the new assignment away with it.
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(3, 3), "Payee Cleanup Race", w.CatA.Id, -12m);
        var bankLeg = await LegOnAsync(h, w.BankA.Id);
        var tag = await AddTagAsync(w, "t-cleanup");

        await using var held = await HoldAsync(db => db.TxnLegTags.Add(LegTag(bankLeg, tag, w.Ledger.LedgerId)));
        await using var db = ProductionContext();
        await RunPastAsync(db, held, () => new TagsRepository(db).CleanupUnusedAsync(w.Ledger.LedgerId));

        await using (var check = Db.NewDbContext())
            Assert.True(await check.TxnLegTags.AnyAsync(t => t.LegId == bankLeg && t.TagId == tag),
                "cleanup deleted a tag another request had just assigned");
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_tag_deleted_through_the_change_tracker_while_another_request_assigns_it_recomputes_that_transaction()
    {
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(3, 4), "Payee Tracked Race", w.CatA.Id, -13m);
        var bankLeg = await LegOnAsync(h, w.BankA.Id);
        var tag = await AddTagAsync(w, "t-tracked");

        await using var held = await HoldAsync(db => db.TxnLegTags.Add(LegTag(bankLeg, tag, w.Ledger.LedgerId)));
        await using var db = ProductionContext();
        var row = await db.Tags.SingleAsync(t => t.Id == tag);
        db.Tags.Remove(row);
        await RunPastAsync(db, held, () => db.SaveChangesAsync());
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_category_merged_while_another_request_adds_a_transaction_to_it_recomputes_that_transaction()
    {
        // The new transaction's bank side names the category its values come from; once
        // the merge moves its category leg, that must be the merge's target.
        await using var w = await WorldAsync();
        await CreateBankAsync(w, w.BankA.Id, Day(4, 1), "Payee Before Merge", w.CatA.Id, -14m);

        var later = new List<Task>();
        var inject = new BeforeCommand(
            sql => sql.Contains("UPDATE txn_legs", StringComparison.OrdinalIgnoreCase),
            () => StartAsync(() => CreateBankAsync(w, w.BankB.Id, Day(4, 2), "Payee During Merge", w.CatA.Id, -15m), later));
        await using (var db = ProductionContext(inject))
        {
            var repo = new AccountsRepository(db, new LegDerivedRecomputeService(db), TimeProvider.System);
            var outcome = await repo.MergeCategoryAsync(w.Ledger.LedgerId, w.CatA.Id, w.CatB.Id, dryRun: false);
            Assert.Equal(AccountsRepository.MergeCategoryResult.Ok, outcome.Result);
        }
        Assert.True(inject.Fired, "the other request was never started, so nothing was tested");
        await Task.WhenAll(later.Select(async t => { try { await t; } catch (InvalidOperationException) { } }));
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_select_all_move_leaves_alone_a_transaction_committed_after_it_read_the_selection()
    {
        // The move recomputes the transactions it read as selected; one committed after
        // that read must not be moved without its recompute.
        await using var w = await WorldAsync();
        await CreateBankAsync(w, w.BankA.Id, Day(4, 3), "Payee Selected", w.CatA.Id, -16m);

        var later = new List<Task>();
        Guid? arrived = null;
        var inject = new BeforeCommand(
            sql => sql.Contains("UPDATE txn_legs", StringComparison.OrdinalIgnoreCase),
            () => StartAsync(async () => arrived = await CreateBankAsync(w, w.BankA.Id, Day(4, 4), "Payee Arrived", w.CatB.Id, -17m), later));
        await using (var db = ProductionContext(inject))
        {
            var repo = new BulkTransactionsRepository(
                db, new LegDerivedRecomputeService(db), new HoldingsRecomputeService(db), TimeProvider.System);
            var (outcome, _) = await repo.BulkMoveAccountAsync(w.Ledger.LedgerId, new SelectionRequest
            {
                Kind = "all", AccountId = w.BankA.Id, StatusFilter = "all", SelectedAt = DateTime.UtcNow.AddMinutes(5),
            }, w.BankB.Id);
            Assert.Equal(BulkTransactionsRepository.BulkMoveOutcome.Moved, outcome);
        }
        Assert.True(inject.Fired, "the other request was never started, so nothing was tested");
        await Task.WhenAll(later);
        Assert.NotNull(arrived);
        Assert.Equal(w.BankA.Id, await AccountOfAsync(arrived!.Value, w.BankA.Id, w.BankB.Id));
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_holdings_link_changed_while_another_request_adds_a_trade_on_the_brokerage_leaves_the_trade_recomputed()
    {
        // Which legs of an event are on its investment side follows the brokerage's
        // holdings link. A trade added while the link changes must be computed under the
        // link that ends up committed.
        await using var w = await WorldAsync();
        var brokerage = await w.Ledger.AddInvestmentAccountAsync("Brokerage A");
        var other = await w.Ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await w.Ledger.AddSecurityAsync("Fund A", "FNDA");
        Guid otherHoldings;
        await using (var db = Db.NewDbContext())
            otherHoldings = await db.Accounts.AsNoTracking().Where(a => a.Id == other.Id)
                .Select(a => a.HoldingsAccountId!.Value).SingleAsync();

        await using var changing = ProductionContext();
        await using (var tx = await changing.Database.BeginTransactionAsync())
        {
            // The property is init-only on the entity, so no writer sets it today; the
            // interceptor still handles the change, and this is the change.
            var account = await changing.Accounts.SingleAsync(a => a.Id == brokerage.Id);
            changing.Entry(account).Property(a => a.HoldingsAccountId).CurrentValue = otherHoldings;
            await changing.SaveChangesAsync();

            var trade = w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = Coffer.Domain.Investment.LedgerActions.Buy,
                    SecurityId = security, Shares = 2m, Price = 10m, PostedAt = Day(5, 1), Payee = "Payee Trade Race",
                });
            await Task.WhenAny(trade, Task.Delay(TimeSpan.FromSeconds(3)));
            await tx.CommitAsync();
            using var resp = await trade;
            Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        }
        await AssertStoredAsync(w);
    }

    private async Task<Guid> AccountOfAsync(Guid header, params Guid[] candidates)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == header && candidates.Contains(l.AccountId))
            .Select(l => l.AccountId).SingleAsync();
    }
}
