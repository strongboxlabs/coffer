using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Errors;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// One repair of a ledger at a time: a repair sent while another repair of the same ledger
/// runs is refused before it rewrites anything, and the one running finishes.
/// </summary>
/// <remarks>
/// <para>The balances rewrite and the holdings rewrite (holdings, lots and realized gains)
/// each delete a scope's stored rows and insert them again, one account or position per
/// statement, each statement under the ledger's write lock (migration 275), so a second
/// repair would take turns with the first and rewrite the same rows again; it is refused
/// instead. A save beside a repair, which that lock makes wait, is
/// <see cref="RecomputeOverlapTests"/>'s.</para>
/// <para>Each overlap test holds the scope's stored rows locked, so the first repair waits
/// at its delete, sends the second while it waits, and then lets both go.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConsistencyRepairOverlapTests : DatabaseTest
{
    private static readonly DateTime FirstDay = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LaterDay = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public ConsistencyRepairOverlapTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static HttpClient AuthedClient(ApiFactory factory, string cookie)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static Task<HttpResponseMessage> RepairAsync(HttpClient client, Guid ledgerId, string projection) =>
        client.PostAsync($"/api/ledgers/{ledgerId}/balances/consistency/{projection}/repair", content: null);

    /// <summary>What a test locks so the first repair waits at its delete.</summary>
    private delegate Task HoldRows(AppDbContext db);

    /// <summary>A bank account and a category with one transaction between them, the bank
    /// account's stored running balance out of step. The bank account's balance rows are
    /// what the balances rewrite deletes and inserts again.</summary>
    private static async Task<HoldRows> BalanceOutOfStepAsync(SyntheticLedger ledger, TestDatabase db)
    {
        var bank = await ledger.AddBankAccountAsync("Checking");
        var category = await ledger.AddCategoryAsync("Groceries");
        await ledger.AddTransactionPairAsync(bank.Id, category.Id, -25m, FirstDay);
        await using (var seed = db.NewDbContext())
        {
            Assert.Equal(1, await seed.TxnHeaderAccountBalances.Where(b => b.AccountId == bank.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.BalanceAfter, b => b.BalanceAfter + 1m)));
        }
        return held => held.TxnHeaderAccountBalances.Where(b => b.AccountId == bank.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.BalanceAfter, b => b.BalanceAfter));
    }

    /// <summary>A purchase and a part sale through the API, the sale's stored realized gain
    /// out of step. The position's realized-gain rows are what the holdings rewrite deletes
    /// and inserts again.</summary>
    private static async Task<HoldRows> RealizedGainOutOfStepAsync(
        HttpClient client, SyntheticLedger ledger, TestDatabase db)
    {
        var securityId = await ledger.AddSecurityAsync(name: "Security R", ticker: "SECR");
        var made = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/accounts",
            new CreateAccountRequest { Name = "Brokerage R", AccountType = "investment", CurrencyCode = "USD" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var brokerage = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        foreach (var (action, shares, price, day) in new[] { ("buy", 10m, 10m, FirstDay), ("sell", -5m, 12m, LaterDay) })
        {
            var traded = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions",
                new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage,
                    PostedAt = day,
                    Action = action,
                    SecurityId = securityId,
                    Shares = shares,
                    Price = price,
                });
            Assert.True(traded.StatusCode == HttpStatusCode.Created, await traded.Content.ReadAsStringAsync());
        }
        await using (var seed = db.NewDbContext())
        {
            Assert.Equal(1, await seed.RealizedGains.Where(g => g.LedgerId == ledger.LedgerId)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.RealizedGain, g => g.RealizedGain + 1m)));
        }
        return held => held.RealizedGains.Where(g => g.LedgerId == ledger.LedgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RealizedGain, g => g.RealizedGain));
    }

    /// <summary>Block until the monitor's start pass has recorded a verdict for each ledger,
    /// so the pass is not running while a test counts the sessions waiting on a lock.</summary>
    private async Task WaitForStartPassAsync(params Guid[] ledgerIds)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using (var db = Db.NewDbContext())
            {
                var announced = await db.LedgerEvents.AsNoTracking()
                    .Where(e => ledgerIds.Contains(e.LedgerId)
                                && ConsistencyVerdicts.VerdictKeys.Contains(e.EventKey))
                    .Select(e => e.LedgerId)
                    .Distinct()
                    .CountAsync();
                if (announced == ledgerIds.Length) return;
            }
            await Task.Delay(100);
        }
        Assert.Fail("The monitor's start pass never recorded a verdict for every ledger.");
    }

    /// <summary>Wait until at least <paramref name="sessions"/> sessions of this test's
    /// database wait on a lock, or <paramref name="running"/> is done. Asked as the
    /// superuser: the requests run as the app role, whose sessions' wait events another role
    /// cannot read.</summary>
    private async Task WaitForLockWaitersOrDoneAsync(int sessions, Task running)
    {
        await using var probe = new NpgsqlConnection(Db.SuperuserConnectionString);
        await probe.OpenAsync();
        for (var i = 0; i < 400 && !running.IsCompleted; i++)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                 WHERE datname = current_database() AND wait_event_type = 'Lock'
                """, probe);
            if ((long)(await cmd.ExecuteScalarAsync())! >= sessions) return;
            await Task.Delay(25);
        }
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// A repair of <paramref name="projection"/> sent while one is waiting partway through its
    /// rewrite, on rows the second would delete and insert again too: the second is refused
    /// with <c>consistency-repair-in-progress</c> before it rewrites anything, as is a repair of
    /// another projection of the ledger, while a repair of another ledger runs. The first
    /// finishes and leaves the projection agreeing, and the ledger takes a repair again once
    /// it has answered.
    /// </summary>
    [Theory]
    [InlineData(ConsistencyProjections.Balances)]
    [InlineData(ConsistencyProjections.RealizedGains)]
    public async Task A_repair_sent_while_another_repair_of_the_ledger_runs_is_refused(string projection)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var other = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        using var otherClient = AuthedClient(factory, await other.IssueSessionCookieAsync());
        var holdRows = projection == ConsistencyProjections.Balances
            ? await BalanceOutOfStepAsync(ledger, Db)
            : await RealizedGainOutOfStepAsync(client, ledger, Db);
        await WaitForStartPassAsync(ledger.LedgerId, other.LedgerId);

        await using var holder = Db.NewDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holdRows(holder);

        var first = RepairAsync(client, ledger.LedgerId, projection);
        await WaitForLockWaitersOrDoneAsync(1, first);
        Assert.False(first.IsCompleted, "the first repair did not wait at the held rows, so nothing was tested");

        var second = RepairAsync(client, ledger.LedgerId, projection);
        await WaitForLockWaitersOrDoneAsync(2, second);
        var otherProjection = await RepairAsync(client, ledger.LedgerId, ConsistencyProjections.PostingCounts);
        var otherLedger = await RepairAsync(otherClient, other.LedgerId, ConsistencyProjections.PostingCounts);

        await held.RollbackAsync();
        using var firstAnswer = await first;
        using var secondAnswer = await second;

        Assert.True(secondAnswer.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"the second repair answered {(int)secondAnswer.StatusCode}: {await secondAnswer.Content.ReadAsStringAsync()}");
        Assert.Equal(BusinessError.Codes.ConsistencyRepairInProgress, await CodeOfAsync(secondAnswer));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, otherProjection.StatusCode);
        Assert.Equal(BusinessError.Codes.ConsistencyRepairInProgress, await CodeOfAsync(otherProjection));
        Assert.Equal(HttpStatusCode.OK, otherLedger.StatusCode);

        Assert.True(firstAnswer.StatusCode == HttpStatusCode.OK,
            $"the first repair answered {(int)firstAnswer.StatusCode}: {await firstAnswer.Content.ReadAsStringAsync()}");
        var repaired = (await firstAnswer.Content.ReadFromJsonAsync<ProjectionRepairResult>())!;
        Assert.True(repaired.Repaired.MismatchedCount > 0, "the first repair found nothing to rewrite");
        Assert.True(repaired.ProjectionAfter().Healthy, $"{projection} still disagrees after the first repair");

        using var again = await RepairAsync(client, ledger.LedgerId, projection);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    /// <summary>
    /// A repair whose rewrite throws answers its error and leaves the ledger free: the next
    /// repair of it runs.
    /// </summary>
    [Fact]
    public async Task A_repair_that_fails_leaves_the_ledger_free_for_the_next()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var failing = new ThrowAtFirst("recompute_balances_for_account");
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithServices(s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(failing)));
        using var client = AuthedClient(factory, await ledger.IssueSessionCookieAsync());
        await BalanceOutOfStepAsync(ledger, Db);
        await WaitForStartPassAsync(ledger.LedgerId);

        failing.Arm();
        using var failed = await RepairAsync(client, ledger.LedgerId, ConsistencyProjections.Balances);
        Assert.True(failing.Fired, "the rewrite never ran, so nothing was tested");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        using var next = await RepairAsync(client, ledger.LedgerId, ConsistencyProjections.Balances);
        Assert.True(next.StatusCode == HttpStatusCode.OK,
            $"the next repair answered {(int)next.StatusCode}: {await next.Content.ReadAsStringAsync()}");
        var repaired = (await next.Content.ReadFromJsonAsync<ProjectionRepairResult>())!;
        Assert.True(repaired.ProjectionAfter().Healthy, "the balances still disagree after the next repair");
    }

    /// <summary>Throws once armed, at the first command whose text contains
    /// <paramref name="marker"/>.</summary>
    private sealed class ThrowAtFirst(string marker) : DbCommandInterceptor
    {
        private int _armed;

        public bool Fired { get; private set; }

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Fired = true;
                throw new PostgresException(
                    "The rewrite failed.", "ERROR", "ERROR", PostgresErrorCodes.InternalError);
            }
            return ValueTask.FromResult(result);
        }
    }
}
