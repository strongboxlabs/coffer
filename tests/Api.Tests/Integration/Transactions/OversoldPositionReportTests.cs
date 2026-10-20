using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Positions already short are reported, not repaired (FU-097, decided 2026-10-20).
/// </summary>
/// <remarks>
/// Migration 256 stops any create or edit from selling more shares than were held, or
/// making a shortfall worse. A position short from before it, or from an import, stays
/// short and overstates its gain by the unmatched shares' proceeds. The purchase it is
/// missing is not in the data, so nothing can repair it honestly: the consistency
/// report lists it, the ledger counts as unhealthy, and the monitor says which position
/// and how to fix it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class OversoldPositionReportTests
{
    private readonly PostgresFixture _fixture;

    public OversoldPositionReportTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Day(int month, int day) =>
        new(2014, month, day, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(SyntheticLedger Ledger, Guid BrokerageId, Guid HoldingsId, Guid SecurityId);

    private static async Task<Guid> TradeAsync(
        HttpClient client, Seed s, string action, decimal shares, DateTime postedAt)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{s.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = s.BrokerageId,
                PostedAt = postedAt,
                Action = action,
                SecurityId = s.SecurityId,
                Shares = shares,
                Price = 10m,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("headerId").GetGuid();
    }

    /// <summary>
    /// A position whose first buy holds fewer shares than a later sale took: short by the
    /// difference.
    /// Made through the API, then the buy's quantity lowered directly, which is the state
    /// an install keeps from before migration 256 or gets from an importer. Then the
    /// derived figures are recomputed, so the only thing wrong is the position itself.
    /// </summary>
    private async Task<Seed> ShortPositionAsync(bool boughtBackAfter)
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index Fund", ticker: "IDX");
        var s = new Seed(ledger, brokerage.Id, brokerage.HoldingsAccountId!.Value, securityId);

        await using (var factory = new ApiFactory(_fixture).WithoutDevAuth())
        {
            var cookie = await ledger.IssueSessionCookieAsync();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
            var buy = await TradeAsync(client, s, "buy", 10m, Day(1, 10));
            await TradeAsync(client, s, "sell", -10m, Day(2, 1));
            if (boughtBackAfter) await TradeAsync(client, s, "buy", 2m, Day(3, 1));

            await using var db = _fixture.NewDbContext();
            await db.TxnLegs
                .Where(l => l.HeaderId == buy && l.AccountId == s.HoldingsId)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.Quantity, 8m));
        }

        await using (var db = _fixture.NewDbContext())
            await new HoldingsRecomputeService(db).RecomputeAsync([(s.HoldingsId, s.SecurityId)]);
        await ledger.RecomputeTradePricesAsync();
        return s;
    }

    private static LedgerConsistencyRepository RepoFor(AppDbContext db) =>
        new(db, new RegisterRepository(db, TimeProvider.System), new HoldingsRecomputeService(db));

    [Fact]
    public async Task A_short_position_is_listed_and_the_ledger_is_unhealthy_for_it_alone()
    {
        var s = await ShortPositionAsync(boughtBackAfter: false);

        await using var db = _fixture.NewDbContext();
        var report = await RepoFor(db).CheckAsync(s.Ledger.LedgerId);

        var oversold = Assert.Single(report.Oversold);
        // The visible brokerage, not its hidden holdings sibling: the settings page
        // links it to the register where the sale can be corrected.
        Assert.Equal(s.BrokerageId, oversold.AccountId);
        Assert.Equal("Brokerage", oversold.Account);
        Assert.Equal(s.SecurityId, oversold.SecurityId);
        Assert.Equal(2m, oversold.SharesShort);
        Assert.False(report.Healthy);
        Assert.All(report.Projections, p => Assert.True(p.Healthy, p.Projection + " is unhealthy"));
    }

    [Fact]
    public async Task A_position_short_in_its_history_but_back_to_zero_is_still_listed()
    {
        // Bought back to nothing: its holding is zero, and the sale in between still
        // booked the unmatched shares at zero cost.
        var s = await ShortPositionAsync(boughtBackAfter: true);

        await using var db = _fixture.NewDbContext();
        Assert.Equal(0m, await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == s.HoldingsId && h.SecurityId == s.SecurityId)
            .Select(h => h.Quantity).SingleAsync());
        var report = await RepoFor(db).CheckAsync(s.Ledger.LedgerId);

        Assert.Equal(2m, Assert.Single(report.Oversold).SharesShort);
    }

    [Fact]
    public async Task The_monitor_records_it_and_says_which_position_and_what_to_do()
    {
        var s = await ShortPositionAsync(boughtBackAfter: false);
        var monitor = new ConsistencyMonitor(
            _fixture.NewServiceFactory(),
            new NotificationPublisher(
                _fixture.NewServiceFactory(),
                _fixture.NewLedgerKeyService(),
                Array.Empty<INotificationSubscriber>(),
                NullLogger<NotificationPublisher>.Instance, TimeProvider.System),
            NullLogger<ConsistencyMonitor>.Instance, TimeProvider.System);
        // The API the position was made through runs the scheduler, which checked this
        // ledger as it started, before the position was short. Forget that check, or
        // the monitor's twelve-hour gate skips the ledger.
        await using (var reset = _fixture.NewDbContext())
            await reset.LedgerConsistencyStates.Where(r => r.LedgerId == s.Ledger.LedgerId).ExecuteDeleteAsync();

        await monitor.CheckAllAsync();

        await using var db = _fixture.NewDbContext();
        var state = await db.LedgerConsistencyStates.AsNoTracking()
            .SingleAsync(r => r.LedgerId == s.Ledger.LedgerId);
        Assert.False(state.Healthy);
        Assert.Equal([ConsistencyMonitor.OversoldFinding], state.FailingProjections);

        var warning = await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == s.Ledger.LedgerId && e.EventKey == "consistency.drift")
            .Select(e => e.Summary)
            .SingleAsync();
        Assert.Contains("sold more shares than they held", warning, StringComparison.Ordinal);
        Assert.Contains("Brokerage / IDX", warning, StringComparison.Ordinal);
        Assert.Contains("Correct the sale or enter the missing purchase", warning, StringComparison.Ordinal);
    }
}
