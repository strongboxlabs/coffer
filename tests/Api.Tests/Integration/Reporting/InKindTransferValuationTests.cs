using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// An in-kind share transfer that crosses a returns perimeter is an external flow
/// valued at MARKET at the transfer instant, the basis every portfolio boundary is
/// valued on, and not at the carried cost basis of the lots that moved.
/// </summary>
/// <remarks>
/// The destination of a transfer receives shares worth their market value and the
/// source gives up shares worth the same. A flow booked at the lots' cost basis
/// instead leaves the difference (market minus basis) inside the sub-period that
/// holds the transfer: the receiving account reads an unearned gain and the sending
/// account reads its own appreciation as nothing, in gain, in the time-weighted
/// chain and in the money-weighted cash flows. Full ledger scope drops the flow and
/// is not affected, so every case here reads ACCOUNT scope.
/// <para>
/// Positions are bought and moved through the API, so the lots, the carried basis
/// and the transfer's leg shape are the production writers' own. Prices come from
/// the feed, dated after the buy so the market differs from the basis.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InKindTransferValuationTests : DatabaseTest
{
    public InKindTransferValuationTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private const decimal Funding = 1_000m;
    private const decimal Shares = 10m;
    private const decimal BuyPrice = 50m;
    private const decimal FeedPrice = 80m;

    // The window opens after every setup write, so each account starts the window
    // holding what the setup left it; the transfer lands inside it.
    private static readonly DateTime WindowStart = Utc(2025, 2, 1);
    private static readonly DateTime WindowEnd = Utc(2025, 12, 31);
    private static readonly DateTime PricedOn = Utc(2025, 3, 1);
    private static readonly DateTime TransferredOn = Utc(2025, 6, 1);

    private sealed record Portfolio(Guid SourceId, Guid DestinationId, Guid SecurityId);

    private static async Task<HttpClient> ClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static async Task PostAsync(HttpClient client, SyntheticLedger ledger, string path, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/{path}", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"{path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static Task ContributeAsync(
        HttpClient client, SyntheticLedger ledger, Guid bankId, Guid brokerageId, DateTime postedAt) =>
        PostAsync(client, ledger, "transactions", new CreateTransactionRequest
        {
            PostedAt = postedAt,
            SourceAccountId = bankId,
            Postings = [new TransactionPosting { CounterpartyAccountId = brokerageId, Amount = -Funding }],
        });

    private static Task BuyAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId,
        decimal price, DateTime postedAt) =>
        PostAsync(client, ledger, "investment-transactions", new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerageId,
            Action = "buy",
            SecurityId = securityId,
            Shares = Shares,
            Price = price,
            PostedAt = postedAt,
        });

    private static Task TransferAsync(
        HttpClient client, SyntheticLedger ledger, Guid fromId, Guid toId, Guid securityId, decimal shares) =>
        PostAsync(client, ledger, "investment-transactions", new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = fromId,
            Action = "transfer_shares",
            SecurityId = securityId,
            Shares = shares,
            TransferAccountId = toId,
            PostedAt = TransferredOn,
        });

    /// <summary>
    /// Both brokerages funded; the source buys <see cref="Shares"/> at
    /// <see cref="BuyPrice"/>; the feed (when given) prices the security from
    /// <see cref="PricedOn"/>; <paramref name="moved"/> shares move source to
    /// destination on <see cref="TransferredOn"/>. With <paramref name="destinationBuyPrice"/>
    /// the destination already holds <see cref="Shares"/> of the security, bought a
    /// day after the source's buy so the ledger's last trade price is its own.
    /// </summary>
    private async Task<(SyntheticLedger Ledger, Portfolio Accounts)> TransferredAsync(
        decimal? feedPrice, decimal moved = Shares, decimal? destinationBuyPrice = null)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var bank = await ledger.AddBankAccountAsync("Account A");
        var source = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var destination = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");

        await ContributeAsync(client, ledger, bank.Id, source.Id, Utc(2025, 1, 2));
        await ContributeAsync(client, ledger, bank.Id, destination.Id, Utc(2025, 1, 2));
        await BuyAsync(client, ledger, source.Id, security, BuyPrice, Utc(2025, 1, 3));
        if (destinationBuyPrice is { } price)
            await BuyAsync(client, ledger, destination.Id, security, price, Utc(2025, 1, 4));
        if (feedPrice is { } feed)
            await ledger.AddSecurityPriceAsync(security, feed, PricedOn);

        await TransferAsync(client, ledger, source.Id, destination.Id, security, moved);
        return (ledger, new Portfolio(source.Id, destination.Id, security));
    }

    /// <summary>
    /// The source buys <paramref name="shares"/> at <paramref name="price"/> and moves
    /// <paramref name="moved"/> of them to the destination on <see cref="TransferredOn"/>,
    /// with no feed price, so the market is the last trade price.
    /// </summary>
    private async Task<(SyntheticLedger Ledger, Portfolio Accounts)> TradedAndMovedAsync(
        decimal shares, decimal price, decimal moved)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var bank = await ledger.AddBankAccountAsync("Account A");
        var source = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var destination = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");

        await PostAsync(client, ledger, "transactions", new CreateTransactionRequest
        {
            PostedAt = Utc(2025, 1, 2),
            SourceAccountId = bank.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = source.Id, Amount = -10_000m }],
        });
        await PostAsync(client, ledger, "investment-transactions", new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = source.Id,
            Action = "buy",
            SecurityId = security,
            Shares = shares,
            Price = price,
            PostedAt = Utc(2025, 1, 3),
        });
        await TransferAsync(client, ledger, source.Id, destination.Id, security, moved);
        return (ledger, new Portfolio(source.Id, destination.Id, security));
    }

    /// <summary>The amount of the share leg the transfer wrote on the destination's holdings
    /// sibling, which is the carried basis of what moved.</summary>
    private async Task<decimal> ArrivingBasisAsync(Guid destinationId)
    {
        await using var db = Db.NewDbContext();
        var holdings = await db.Accounts.AsNoTracking()
            .Where(a => a.Id == destinationId).Select(a => a.HoldingsAccountId!.Value).SingleAsync();
        return await db.ResolvedTransactions.AsNoTracking()
            .Where(r => r.InvestmentAction == "transfer_shares" && r.AccountId == holdings)
            .Select(r => r.Amount)
            .Distinct()
            .SingleAsync();
    }

    private async Task<ReturnsResult> ReturnsAsync(SyntheticLedger ledger, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        var repository = new InvestmentReportingRepository(db, TimeProvider.System);
        return await repository.ReturnsAsync(
            ledger.LedgerId, accountId, fromUtc: WindowStart, toUtc: WindowEnd, nowUtc: WindowEnd);
    }

    private static void AssertFlowAmount(ReturnsResult r, decimal inKind)
    {
        Assert.Equal(inKind, r.NetContributions);
        var bySource = Assert.Single(r.ContributionsBySource, s => s.Source == "in_kind_transfers");
        Assert.Equal(inKind > 0m ? inKind : 0m, bySource.In);
        Assert.Equal(inKind < 0m ? inKind : 0m, bySource.Out);
    }

    /// <summary>
    /// The receiving account is worth what it held plus the shares' market value
    /// afterwards, and nothing was earned by the arrival: the flow is the market value
    /// and the gain and the time-weighted return over a window with no real movement
    /// are zero.
    /// </summary>
    [Fact]
    public async Task The_destination_reads_the_arrival_at_market_and_earns_nothing_from_it()
    {
        var (ledger, p) = await TransferredAsync(FeedPrice);

        var r = await ReturnsAsync(ledger, p.DestinationId);

        // Anchors: the shares really arrived and are worth the feed price.
        Assert.Equal(Funding, r.StartValue);
        Assert.Equal(Funding + Shares * FeedPrice, r.EndValue);

        Assert.Equal(0m, r.Gain);
        Assert.NotNull(r.TimeWeightedReturn);
        Assert.True(Math.Abs(r.TimeWeightedReturn!.Value) < 1e-9,
            $"expected ~0% TWR, got {r.TimeWeightedReturn}");
        AssertFlowAmount(r, Shares * FeedPrice);
    }

    /// <summary>
    /// The sending account keeps the appreciation it earned up to the transfer: the
    /// shares leave at market, so the rise from the buy price to the feed price is its
    /// gain and its time-weighted return, not a withdrawal of the lower basis.
    /// </summary>
    [Fact]
    public async Task The_source_keeps_its_appreciation_up_to_the_transfer()
    {
        var (ledger, p) = await TransferredAsync(FeedPrice);

        var r = await ReturnsAsync(ledger, p.SourceId);

        // Anchors: valued at the buy price before the feed starts, cash only after.
        Assert.Equal(Funding, r.StartValue);
        Assert.Equal(Funding - Shares * BuyPrice, r.EndValue);

        Assert.Equal(Shares * (FeedPrice - BuyPrice), r.Gain);

        // The one invested stretch that held the shares grew by market over start.
        var growth = (double)((Funding + Shares * (FeedPrice - BuyPrice)) / Funding);
        Assert.NotNull(r.TimeWeightedReturn);
        Assert.NotNull(r.TimeWeightedCoveredYears);
        var expected = Math.Pow(growth, 1.0 / r.TimeWeightedCoveredYears!.Value) - 1.0;
        Assert.True(Math.Abs(r.TimeWeightedReturn!.Value - expected) < 1e-9,
            $"expected TWR {expected}, got {r.TimeWeightedReturn}");
        AssertFlowAmount(r, -Shares * FeedPrice);
    }

    /// <summary>
    /// A transfer of part of a position into an account that already holds the
    /// security values the moved shares at the market, share for share: the flow is
    /// the moved quantity at the feed price, and each side's gain is the appreciation
    /// on the shares it actually held.
    /// </summary>
    [Fact]
    public async Task A_partial_transfer_into_a_holder_is_valued_by_the_moved_quantity()
    {
        const decimal moved = 4m;
        const decimal destinationBuyPrice = 60m;
        var (ledger, p) = await TransferredAsync(
            FeedPrice, moved, destinationBuyPrice);

        var destination = await ReturnsAsync(ledger, p.DestinationId);
        var source = await ReturnsAsync(ledger, p.SourceId);

        // Ten shares each were held at the last trade price before the feed, 60.
        Assert.Equal(Shares * (FeedPrice - destinationBuyPrice), destination.Gain);
        Assert.Equal(Shares * (FeedPrice - destinationBuyPrice), source.Gain);
        AssertFlowAmount(destination, moved * FeedPrice);
        AssertFlowAmount(source, -moved * FeedPrice);
    }

    /// <summary>
    /// Control: when the market equals the carried basis the two valuations agree, so
    /// the flow is the basis and nothing is gained or lost on either side.
    /// </summary>
    [Fact]
    public async Task When_market_equals_basis_the_flow_is_the_basis_and_nothing_is_gained()
    {
        var (ledger, p) = await TransferredAsync(BuyPrice);

        var destination = await ReturnsAsync(ledger, p.DestinationId);
        var source = await ReturnsAsync(ledger, p.SourceId);

        Assert.Equal(0m, destination.Gain);
        Assert.Equal(0m, source.Gain);
        Assert.True(Math.Abs(destination.TimeWeightedReturn!.Value) < 1e-9);
        Assert.True(Math.Abs(source.TimeWeightedReturn!.Value) < 1e-9);
        AssertFlowAmount(destination, Shares * BuyPrice);
        AssertFlowAmount(source, -Shares * BuyPrice);
    }

    private async Task<ReturnsCostEstimate> EstimateAsync(SyntheticLedger ledger, Guid? accountId)
    {
        await using var db = Db.NewDbContext();
        return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsCostEstimateAsync(
            ledger.LedgerId, accountId, fromUtc: null, toUtc: null, nowUtc: WindowEnd);
    }

    private async Task<ReturnsResult> SinceInceptionAsync(SyntheticLedger ledger, Guid? accountId, DateTime end)
    {
        await using var db = Db.NewDbContext();
        return await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsAsync(
            ledger.LedgerId, accountId, fromUtc: null, toUtc: null, nowUtc: end);
    }

    /// <summary>
    /// The estimate does not value the transfer, and counts the instants the valued call
    /// uses: here the cash contribution and the in-kind arrival, whose market value differs
    /// from its basis. Mutation caught: the estimate dropping the in-kind instant, or
    /// counting a transfer once per leg.
    /// </summary>
    [Fact]
    public async Task The_cost_estimate_counts_the_instants_the_valued_report_uses()
    {
        var (ledger, p) = await TransferredAsync(FeedPrice);

        var destination = await EstimateAsync(ledger, p.DestinationId);
        var source = await EstimateAsync(ledger, p.SourceId);
        var wholeLedger = await EstimateAsync(ledger, null);

        // The valued report holds both flows at the account scope: the contribution, and the
        // arrival at its market value.
        var report = await SinceInceptionAsync(ledger, p.DestinationId, WindowEnd);
        Assert.Equal(Funding + Shares * FeedPrice, report.NetContributions);
        Assert.Equal(Shares * FeedPrice, Assert.Single(report.ContributionsBySource, s => s.Source == "in_kind_transfers").In);
        Assert.Equal(2, destination.FlowInstants);
        Assert.Equal(2, source.FlowInstants);
        Assert.Equal(report.StartDate, destination.StartDate);
        Assert.Equal(report.EndDate, destination.EndDate);

        // Both brokerages are inside the whole ledger's perimeter, so the transfer is no
        // boundary there, and the two contributions share an instant.
        Assert.Equal(1, wholeLedger.FlowInstants);
    }

    /// <summary>
    /// A transfer carried at no basis but worth something is a flow of the valued report, so
    /// the estimate counts it though its legs hold no amount. Mutation caught: the estimate
    /// dropping an in-kind flow whose leg amounts are zero.
    /// </summary>
    [Fact]
    public async Task The_cost_estimate_counts_a_transfer_whose_basis_is_nothing_and_market_is_not()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var destination = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");
        await ledger.AddInvestmentBuyAsync(
            source.Id, source.HoldingsAccountId!.Value, security, Shares, BuyPrice, Utc(2025, 1, 3));
        await ledger.AddSecurityPriceAsync(security, FeedPrice, PricedOn);
        await ledger.AddTransferSharesAsync(
            source.Id, source.HoldingsAccountId!.Value,
            destination.Id, destination.HoldingsAccountId!.Value,
            security, Shares, 0m, TransferredOn);

        var report = await SinceInceptionAsync(ledger, destination.Id, WindowEnd);
        var estimate = await EstimateAsync(ledger, destination.Id);

        Assert.Equal(Shares * FeedPrice, report.NetContributions);
        Assert.Equal(1, estimate.FlowInstants);
    }

    /// <summary>
    /// An account funded only by shares arriving in kind: its since-inception series is the
    /// scalar report's at every point, with the arrival at market. Mutation caught: the
    /// arrival booked at basis, which moves every gain by the difference.
    /// </summary>
    [Fact]
    public async Task An_account_funded_in_kind_has_a_series_equal_to_its_scalar_report()
    {
        var (ledger, p) = await TradedAndMovedAsync(Shares, BuyPrice, Shares);
        await ledger.AddSecurityPriceAsync(p.SecurityId, FeedPrice, PricedOn);
        await ledger.AddSecurityPriceAsync(p.SecurityId, 100m, Utc(2025, 9, 1));

        await using var db = Db.NewDbContext();
        var series = Assert.Single((await new InvestmentReportingRepository(db, TimeProvider.System).ReturnsSeriesAsync(
            ledger.LedgerId, p.DestinationId, false, null, WindowEnd, ReportTimeBucket.Quarter,
            Utc(2030, 1, 1))).Series);

        Assert.Equal(3, series.Points.Count);
        Assert.Equal([0m, 200m, 200m], series.Points.Select(x => x.Gain!.Value));
        Assert.Equal([100.0, 125.0, 125.0], series.Points.Select(x => Math.Round(x.Index!.Value, 9)));
        foreach (var point in series.Points)
        {
            var scalar = await SinceInceptionAsync(ledger, p.DestinationId, point.PeriodEnd);
            Assert.Equal(scalar.Gain, point.Gain);
            Assert.Equal(scalar.TimeWeightedCoveredDays, point.CoveredDays);
            Assert.Equal(
                scalar.TimeWeightedReturn!.Value,
                Math.Pow(point.Index!.Value / 100.0, 1.0 / scalar.TimeWeightedCoveredYears!.Value) - 1.0, 9);
        }
    }

    /// <summary>
    /// Part of a position moves at a price with a third and fourth decimal, and the value
    /// moved is its share of the position at the transfer instant at the 4-place bound
    /// every read-side figure keeps, not rounded to the dollar's two places (ADR-0114 D15).
    /// The REST report and the MCP tool read the same figure. Mutation caught: rounding the
    /// valued flow to two places, or valuing the whole position.
    /// </summary>
    [Fact]
    public async Task A_partial_transfer_keeps_the_four_place_bound_on_rest_and_mcp()
    {
        const decimal moved = 3m;
        const decimal price = 33.3357m;
        var (ledger, p) = await TradedAndMovedAsync(Shares, BuyPrice, moved);
        await ledger.AddSecurityPriceAsync(p.SecurityId, price, PricedOn);

        var destination = await ReturnsAsync(ledger, p.DestinationId);
        var source = await ReturnsAsync(ledger, p.SourceId);

        // Anchor: the moved shares' value has four decimals, so two places would move it.
        Assert.NotEqual(moved * price, decimal.Round(moved * price, 2));
        AssertFlowAmount(destination, moved * price);
        AssertFlowAmount(source, -moved * price);

        // The same window through the endpoint and the MCP tool.
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);
        var response = await client.GetAsync(
            $"/api/ledgers/{ledger.LedgerId}/reports/returns?accountId={p.DestinationId}"
            + $"&from={WindowStart:O}&to={WindowEnd:O}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var rest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await using var db = Db.NewDbContext();
        var mcp = await Coffer.Api.Mcp.InvestmentTools.Returns(
            new InvestmentReportingRepository(db, TimeProvider.System),
            ledger.LedgerId, p.DestinationId, WindowStart, WindowEnd);
        Assert.Equal(moved * price, rest.RootElement.GetProperty("netContributions").GetDecimal());
        Assert.Equal(moved * price, mcp.NetContributions);
        Assert.Equal(rest.RootElement.GetProperty("endValue").GetDecimal(), mcp.EndValue);
        Assert.Equal(rest.RootElement.GetProperty("gain").GetDecimal(), mcp.Gain);
    }

    /// <summary>
    /// When the market is the last trade price, the flow is the moved position's value at
    /// the 4-place bound, the figure the destination's end value carries, so nothing is
    /// gained on either side, though the carried basis the leg holds is at the dollar's two
    /// places, at an odd quantity and a price that leaves a half cent. Mutation caught: a
    /// rounding of the valued flow to the currency's places, which moves it off the
    /// position's value and reads that difference as a gain or loss.
    /// </summary>
    [Fact]
    public async Task When_market_is_the_trade_price_the_flow_is_the_positions_value_and_nothing_is_gained()
    {
        var (ledger, p) = await TradedAndMovedAsync(3m, 33.335m, 3m);
        var basis = await ArrivingBasisAsync(p.DestinationId);

        var destination = await ReturnsAsync(ledger, p.DestinationId);
        var source = await ReturnsAsync(ledger, p.SourceId);

        // Anchors: the destination held nothing before, so its end value is the moved
        // position's value, which the basis at two places is not.
        Assert.Equal(0m, destination.StartValue);
        var value = destination.EndValue!.Value;
        Assert.NotEqual(basis, value);
        AssertFlowAmount(destination, value);
        AssertFlowAmount(source, -value);
        Assert.Equal(0m, destination.Gain);
        Assert.Equal(0m, source.Gain);
    }

    /// <summary>
    /// A security with neither a close nor a priced trade is carried at its cost
    /// basis by every valuation, so there is no market to value the flow at and it
    /// stays the carried basis.
    /// </summary>
    [Fact]
    public async Task A_security_with_no_price_at_all_keeps_the_carried_basis_as_the_flow()
    {
        const decimal basis = 500m;
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await ClientAsync(factory, ledger);

        var bank = await ledger.AddBankAccountAsync("Account A");
        var source = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var destination = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("Fund One", "FNDA");

        await ContributeAsync(client, ledger, bank.Id, destination.Id, Utc(2025, 1, 2));
        // Shares in: a share movement, which prices nothing.
        await ledger.AddSharesInAsync(
            source.Id, source.HoldingsAccountId!.Value, bank.Id, security, Shares, basis, Utc(2025, 1, 3));
        await ledger.AddTransferSharesAsync(
            source.Id, source.HoldingsAccountId!.Value,
            destination.Id, destination.HoldingsAccountId!.Value,
            security, Shares, BuyPrice, TransferredOn);

        var r = await ReturnsAsync(ledger, destination.Id);

        Assert.Equal(Funding + basis, r.EndValue);
        Assert.Equal(0m, r.Gain);
        AssertFlowAmount(r, basis);
    }
}
