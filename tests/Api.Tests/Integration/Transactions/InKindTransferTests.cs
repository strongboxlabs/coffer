using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// transfer_shares (in-kind, ADR-0065). Drives the live investment-create path
/// (which runs the FIFO recompute engine) to prove the three guarantees that
/// separate a real in-kind transfer from the sell+buy mis-model it replaces:
///   1. Per-lot carry — each moved lot keeps its acquired_at + unit_cost at the
///      destination (not collapsed to a transfer-date single lot).
///   2. Zero realized gain — the source records NO realized_gains row.
///   3. Availability gate — a destination sale dated BEFORE the transfer-in
///      can't consume a not-yet-arrived inherited lot.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InKindTransferTests : DatabaseTest
{
    public InKindTransferTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Transfer_shares_carries_lots_per_lot_with_zero_realized_gain()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source IRA");
        var dest = await ledger.AddInvestmentAccountAsync("Dest IRA");
        var sourceHoldings = source.HoldingsAccountId!.Value;
        var destHoldings = dest.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task Post(CreateInvestmentTransactionRequest req)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        // Source acquires two equal-sized lots: a cheap one (2020), then one at
        // twice the price (2022).
        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 100m, PostedAt = Utc(2020, 1, 1) });
        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 200m, PostedAt = Utc(2022, 1, 1) });

        // Transfer one and a half lots' worth in-kind to the destination (2025).
        // FIFO moves the whole cheap lot + half of the dear one.
        await Post(new()
        {
            BrokerageAccountId = source.Id,
            Action = "transfer_shares",
            SecurityId = security,
            Shares = 15m,
            TransferAccountId = dest.Id,
            PostedAt = Utc(2025, 1, 1),
        });

        await using var db = Db.NewDbContext();

        // Source: the unmoved half of the dear lot is left, at that lot's unit cost
        // (FIFO consumed the cheap lot + the other half of the dear one).
        var srcHolding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == sourceHoldings && h.SecurityId == security);
        Assert.Equal(5m, srcHolding.Quantity);
        Assert.Equal(1000m, srcHolding.CostBasis);

        // Destination: the moved shares, basis = the cheap lot's whole basis + half
        // the dear lot's (carried).
        var dstHolding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == destHoldings && h.SecurityId == security);
        Assert.Equal(15m, dstHolding.Quantity);
        Assert.Equal(2000m, dstHolding.CostBasis);

        // Per-lot carry: the destination has TWO open lots with the ORIGINAL
        // acquisition dates + unit costs — not one transfer-date lot.
        var destLots = await db.Lots.AsNoTracking()
            .Where(l => !l.IsClosed && db.Holdings.Any(h =>
                h.Id == l.HoldingId && h.AccountId == destHoldings && h.SecurityId == security))
            .OrderBy(l => l.AcquiredAt)
            .ToListAsync();
        Assert.Equal(2, destLots.Count);
        Assert.Equal(Utc(2020, 1, 1), destLots[0].AcquiredAt);
        Assert.Equal(10m, destLots[0].Quantity);
        Assert.Equal(100m, destLots[0].UnitCost);
        Assert.Equal(Utc(2022, 1, 1), destLots[1].AcquiredAt);
        Assert.Equal(5m, destLots[1].Quantity);
        Assert.Equal(200m, destLots[1].UnitCost);

        // Zero realized gain: a transfer is NOT a disposition.
        var realized = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Empty(realized.Rows);
    }

    [Fact]
    public async Task Transfer_shares_rounds_a_fractional_carried_basis_at_the_brokerages_places()
    {
        // A lot whose basis does not divide evenly by its quantity has a high-
        // precision unit_cost (mig 152 derives unit_cost = amount / quantity), so
        // quantity × unit_cost does not fit the currency. The transfer producer
        // rounds the carried basis once at the brokerage's places, a two-place
        // currency here (ADR-0114 D15); an unrounded basis past the database's
        // three-place ceiling throws a 23514 check-constraint violation on non-round
        // values, which round-number tests never exercise.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source");
        var dest = await ledger.AddInvestmentAccountAsync("Dest");
        var destHoldings = dest.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task Post(CreateInvestmentTransactionRequest req) =>
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync(
                    $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req)).StatusCode);

        // Buy a share count that does not divide the round total evenly → unit_cost
        // is a repeating decimal (high precision).
        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 3m, Price = 33.333333m, PostedAt = Utc(2020, 1, 1) });
        // Transfer them all in-kind: carried basis = quantity × that repeating
        // unit_cost, which falls just short of the round total → the producer
        // rounds it back to the currency's minor unit.
        await Post(new() { BrokerageAccountId = source.Id, Action = "transfer_shares", SecurityId = security, Shares = 3m, TransferAccountId = dest.Id, PostedAt = Utc(2025, 1, 1) });

        // Destination holds every share at the rounded, round-total basis.
        await using var db = Db.NewDbContext();
        var dst = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == destHoldings && h.SecurityId == security);
        Assert.Equal(3m, dst.Quantity);
        Assert.Equal(100.00m, dst.CostBasis);
    }

    [Fact]
    public async Task Inherited_lot_drives_fifo_holding_period_on_later_sale()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source");
        var dest = await ledger.AddInvestmentAccountAsync("Dest");
        var destHoldings = dest.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task Post(CreateInvestmentTransactionRequest req) =>
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync(
                    $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req)).StatusCode);

        // Source buys a lot (2019), transfers all of it to dest (2025), then dest
        // sells all of it at three times the price (2026).
        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 100m, PostedAt = Utc(2019, 1, 1) });
        await Post(new() { BrokerageAccountId = source.Id, Action = "transfer_shares", SecurityId = security, Shares = 10m, TransferAccountId = dest.Id, PostedAt = Utc(2025, 1, 1) });
        await Post(new() { BrokerageAccountId = dest.Id, Action = "sell", SecurityId = security, Shares = -10m, Price = 300m, PostedAt = Utc(2026, 1, 1) });

        // The sale's cost basis is the INHERITED unit cost (carried from 2019), not
        // a transfer-date market value: the gain is the proceeds less the original
        // buy's basis.
        var realized = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        var row = Assert.Single(realized.Rows);
        Assert.Equal(3000m, row.Proceeds);
        Assert.Equal(1000m, row.CostBasisSold);
        Assert.Equal(2000m, row.RealizedGain);

        // Destination position fully closed.
        await using var db = Db.NewDbContext();
        var dstHolding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == destHoldings && h.SecurityId == security);
        Assert.Equal(0m, dstHolding.Quantity);
        Assert.Equal(0m, dstHolding.CostBasis);
    }

    [Fact]
    public async Task Sale_before_transfer_in_does_not_consume_unarrived_lot()
    {
        // Availability gate (ADR-0065 D3). The destination holds its OWN lot,
        // sells some BEFORE an in-kind transfer arrives. That earlier sale must
        // consume the dest's own (already-arrived) lot — NOT the inherited lot,
        // even though the inherited lot's acquired_at is older.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source");
        var dest = await ledger.AddInvestmentAccountAsync("Dest");
        var destHoldings = dest.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task Post(CreateInvestmentTransactionRequest req) =>
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync(
                    $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req)).StatusCode);

        // Source buys a lot in 2010 (the future inherited lot — very old).
        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 100m, PostedAt = Utc(2010, 1, 1) });
        // Dest buys its own equal-sized lot at half that price in 2021.
        await Post(new() { BrokerageAccountId = dest.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 50m, PostedAt = Utc(2021, 1, 1) });
        // Dest sells that many shares in 2023, at a price between the two lots' —
        // BEFORE the transfer-in arrives.
        await Post(new() { BrokerageAccountId = dest.Id, Action = "sell", SecurityId = security, Shares = -10m, Price = 80m, PostedAt = Utc(2023, 1, 1) });
        // Source transfers its 2010 lot in-kind to dest in 2025.
        await Post(new() { BrokerageAccountId = source.Id, Action = "transfer_shares", SecurityId = security, Shares = 10m, TransferAccountId = dest.Id, PostedAt = Utc(2025, 1, 1) });

        // Only the 2023 dest sale realizes a gain (the transfer realizes none),
        // so the ledger has exactly one realized_gains row.
        var realized = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        var row = Assert.Single(realized.Rows);
        // The 2023 sale consumed the dest's OWN cheaper lot (arrived 2021), NOT the
        // inherited dearer lot (arrived 2025), so it books a gain: the proceeds less
        // the own lot's basis. Without the availability gate it would wrongly consume
        // the older-dated 2010 lot, whose basis exceeds the proceeds → a fabricated
        // loss.
        Assert.Equal(800m, row.Proceeds);
        Assert.Equal(500m, row.CostBasisSold);
        Assert.Equal(300m, row.RealizedGain);

        // After the transfer, the dest holds the inherited lot at its original basis.
        await using var db = Db.NewDbContext();
        var dstHolding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == destHoldings && h.SecurityId == security);
        Assert.Equal(10m, dstHolding.Quantity);
        Assert.Equal(1000m, dstHolding.CostBasis);
    }

    [Fact]
    public async Task Transfer_shares_rejects_more_than_held()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source");
        var dest = await ledger.AddInvestmentAccountAsync("Dest");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 5m, Price = 100m, PostedAt = Utc(2024, 1, 1) });

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = source.Id,
                Action = "transfer_shares",
                SecurityId = security,
                Shares = 10m,            // more than the source holds
                TransferAccountId = dest.Id,
                PostedAt = Utc(2025, 1, 1),
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Scrub_detects_and_converts_a_sell_buy_pair_to_an_in_kind_transfer()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source");
        var dest = await ledger.AddInvestmentAccountAsync("Dest");
        var sourceHoldings = source.HoldingsAccountId!.Value;
        var destHoldings = dest.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> Post(CreateInvestmentTransactionRequest req)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>();
            return body!.HeaderId;
        }

        // The mis-model: source bought a lot (2020), then a 2024 in-kind transfer
        // was recorded as a sell-in-source + buy-in-dest, both at the same, higher
        // transfer-day price.
        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 100m, PostedAt = Utc(2020, 1, 1) });
        var sellId = await Post(new() { BrokerageAccountId = source.Id, Action = "sell", SecurityId = security, Shares = -10m, Price = 150m, PostedAt = Utc(2024, 6, 1) });
        var buyId = await Post(new() { BrokerageAccountId = dest.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 150m, PostedAt = Utc(2024, 6, 1) });

        // The mis-model fabricates a realized gain in the source (the sale's
        // proceeds less the original basis).
        var before = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Equal(500m, before.TotalRealizedGain);

        // Detection finds exactly this pair.
        var candidates = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .FindInKindTransferCandidatesAsync(ledger.LedgerId);
        var candidate = Assert.Single(candidates);
        Assert.Equal(sellId, candidate.SellHeaderId);
        Assert.Equal(buyId, candidate.BuyHeaderId);
        Assert.Equal(source.Id, candidate.SourceAccountId);
        Assert.Equal(dest.Id, candidate.DestAccountId);
        Assert.Equal(10m, candidate.Quantity);

        // Convert it.
        var convert = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/in-kind-transfers/convert",
            new ConvertInKindTransferRequest { SellHeaderId = sellId, BuyHeaderId = buyId });
        Assert.Equal(HttpStatusCode.Created, convert.StatusCode);

        await using var db = Db.NewDbContext();

        // The fabricated gain is gone (a transfer realizes nothing).
        var after = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Empty(after.Rows);

        // Source emptied; destination holds the carried 2020 lot at its original
        // unit cost (NOT the transfer-day price).
        var src = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == sourceHoldings && h.SecurityId == security);
        Assert.Equal(0m, src.Quantity);
        var dst = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == destHoldings && h.SecurityId == security);
        Assert.Equal(10m, dst.Quantity);
        Assert.Equal(1000m, dst.CostBasis);

        var dstLot = await db.Lots.AsNoTracking()
            .Where(l => !l.IsClosed && db.Holdings.Any(h =>
                h.Id == l.HoldingId && h.AccountId == destHoldings && h.SecurityId == security))
            .SingleAsync();
        Assert.Equal(Utc(2020, 1, 1), dstLot.AcquiredAt);
        Assert.Equal(100m, dstLot.UnitCost);

        // Re-running detection finds nothing (the pair is gone).
        var none = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .FindInKindTransferCandidatesAsync(ledger.LedgerId);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Convert_succeeds_when_both_brokerages_are_since_closed()
    {
        // A historical in-kind correction targets transactions on accounts that
        // can be since closed (e.g. an account emptied by a transfer). The inactive-account
        // write gate must NOT block convert — it operates on the already-
        // existing sell+buy, not new activity (allowInactiveAccounts, ADR-0085
        // presentation-vs-correctness). A normal create to an inactive brokerage
        // still 422s — proved by InactiveAccountGateTests.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Old 401k (closed)");
        var dest = await ledger.AddInvestmentAccountAsync("Rollover IRA (closed)");
        var destHoldings = dest.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> Post(CreateInvestmentTransactionRequest req)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
        }

        // The mis-model, recorded while the accounts were open.
        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 100m, PostedAt = Utc(2020, 1, 1) });
        var sellId = await Post(new() { BrokerageAccountId = source.Id, Action = "sell", SecurityId = security, Shares = -10m, Price = 150m, PostedAt = Utc(2024, 6, 1) });
        var buyId = await Post(new() { BrokerageAccountId = dest.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 150m, PostedAt = Utc(2024, 6, 1) });

        // Both accounts are since closed.
        await ledger.SetIsActiveAsync(source.Id, false);
        await ledger.SetIsActiveAsync(dest.Id, false);

        // Convert still succeeds; it is not refused with brokerage-inactive.
        var convert = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/in-kind-transfers/convert",
            new ConvertInKindTransferRequest { SellHeaderId = sellId, BuyHeaderId = buyId });
        Assert.Equal(HttpStatusCode.Created, convert.StatusCode);

        // The fabricated gain is gone and the carried lot lands at the dest at its
        // original cost.
        var after = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Empty(after.Rows);

        await using var db = Db.NewDbContext();
        var dst = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == destHoldings && h.SecurityId == security);
        Assert.Equal(10m, dst.Quantity);
        Assert.Equal(1000m, dst.CostBasis);
    }

    [Fact]
    public async Task Transfer_carries_basis_exact_to_the_minor_unit_for_a_large_high_precision_lot()
    {
        // Mig 180. The transfer reconstructs the carried basis as quantity ×
        // unit_cost, rounded once at the brokerage's places (ADR-0114 D15). At
        // NUMERIC(19,4) a large lot whose basis doesn't divide evenly by its quantity
        // drifts by up to quantity × 5e-5 — here a six-figure basis would be carried
        // whole units of currency short. Mig 180 widens unit_cost to NUMERIC(25,12),
        // making the carry exact to the minor unit.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source");
        var dest = await ledger.AddInvestmentAccountAsync("Dest");
        var sourceHoldings = source.HoldingsAccountId!.Value;
        var destHoldings = dest.HoldingsAccountId!.Value;
        var security = await ledger.AddSecurityAsync("Bond Fund", "BDFA");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task Post(CreateInvestmentTransactionRequest req) =>
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync(
                    $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req)).StatusCode);

        // A large share count at a per-share cost with more than 4 significant
        // decimals: basis = quantity × price, rounded at the brokerage's places.
        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 90000m, Price = 1.371742111111m, PostedAt = Utc(2015, 1, 1) });

        await using (var db = Db.NewDbContext())
        {
            // Source basis is the exact buy leg amount (buys fold `amount` directly,
            // not via unit_cost) — the exact ground truth to carry.
            var src = await db.Holdings.AsNoTracking()
                .SingleAsync(h => h.AccountId == sourceHoldings && h.SecurityId == security);
            Assert.Equal(123456.79m, src.CostBasis);
        }

        // Transfer the whole lot in-kind.
        await Post(new() { BrokerageAccountId = source.Id, Action = "transfer_shares", SecurityId = security, Shares = 90000m, TransferAccountId = dest.Id, PostedAt = Utc(2025, 1, 1) });

        await using (var db = Db.NewDbContext())
        {
            var dst = await db.Holdings.AsNoTracking()
                .SingleAsync(h => h.AccountId == destHoldings && h.SecurityId == security);
            Assert.Equal(90000m, dst.Quantity);
            // Exact carry. Pre-mig-180 (19,4) unit_cost this came out whole units of
            // currency short.
            Assert.Equal(123456.79m, dst.CostBasis);
        }
    }

    [Fact]
    public async Task Transfer_shares_rejects_same_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = source.Id,
                Action = "transfer_shares",
                SecurityId = security,
                Shares = 1m,
                TransferAccountId = source.Id,   // same account
                PostedAt = Utc(2025, 1, 1),
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Convert_carries_basis_with_multilot_source_and_a_later_dest_sale()
    {
        // The shape this pins: a multi-lot source, the in-kind move recorded as
        // sell(source)+buy(dest) same day/qty, THEN a later dividend buy in the
        // destination, THEN a destination sale that disposes MORE than was
        // transferred. A correct convert carries the source's basis to the
        // destination, so the later sale nets roughly zero realized gain. A convert
        // that carries roughly zero basis makes the later sale show a large phantom
        // gain.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source");
        var dest = await ledger.AddInvestmentAccountAsync("Dest");
        var security = await ledger.AddSecurityAsync("Conv Fund", "CNV");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> Post(CreateInvestmentTransactionRequest req)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
        }

        // Source accumulates two lots at one price: a large one and a top-up a
        // tenth its size.
        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 5000m, Price = 10m, PostedAt = Utc(2025, 2, 10) });
        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 500m, Price = 10m, PostedAt = Utc(2025, 5, 5) });
        // Later: the in-kind move, mis-recorded as sell(source) + buy(dest) on one day.
        var sellId = await Post(new() { BrokerageAccountId = source.Id, Action = "sell", SecurityId = security, Shares = -5500m, Price = 10m, PostedAt = Utc(2025, 11, 12) });
        var buyId = await Post(new() { BrokerageAccountId = dest.Id, Action = "buy", SecurityId = security, Shares = 5500m, Price = 10m, PostedAt = Utc(2025, 11, 12) });
        // After the move: a small dividend reinvest in the destination, at the same price.
        await Post(new() { BrokerageAccountId = dest.Id, Action = "buy", SecurityId = security, Shares = 50m, Price = 10m, PostedAt = Utc(2025, 12, 1) });
        // After the reinvest: sell the WHOLE destination position (the transferred
        // shares plus the reinvest), still at the same price.
        await Post(new() { BrokerageAccountId = dest.Id, Action = "sell", SecurityId = security, Shares = -5550m, Price = 10m, PostedAt = Utc(2025, 12, 8) });

        // Baseline: proceeds == basis on both sales, so realized gain is roughly zero.
        var before = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Equal(0m, Math.Round(before.TotalRealizedGain!.Value, 2));

        // Convert the mis-recorded pair into a true in-kind transfer.
        var convert = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/in-kind-transfers/convert",
            new ConvertInKindTransferRequest { SellHeaderId = sellId, BuyHeaderId = buyId });
        Assert.Equal(HttpStatusCode.Created, convert.StatusCode);

        // A correct convert carries the source's whole basis to the destination, so
        // the destination sale still nets roughly zero. Carrying the basis onto lots
        // but leaving the destination's FIFO/realized rebuild stale (lots created
        // after the recompute fired) would make the sale oversell and book a phantom
        // gain the size of the whole transferred basis.
        var after = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .RealizedGainsAsync(ledger.LedgerId, null, null, null, null);
        Assert.Equal(0m, Math.Round(after.TotalRealizedGain!.Value, 2));

        // And the destination is internally consistent (no stranded open lots):
        // fully sold, zero remaining quantity + basis.
        await using var db = Db.NewDbContext();
        var destHolding = await db.Holdings.AsNoTracking()
            .SingleAsync(h => h.AccountId == dest.HoldingsAccountId!.Value && h.SecurityId == security);
        Assert.Equal(0m, destHolding.Quantity);
        Assert.Equal(0m, Math.Round(destHolding.CostBasis, 2));
    }

    [Fact]
    public async Task Candidates_leave_out_a_pair_whose_brokerages_are_in_different_currencies()
    {
        // ADR-0114 D13, ADR-0065 D4. One sale in a brokerage, and a buy of the same
        // security, day and quantity in each of two others: one in the sale's currency,
        // which is offered (the anchor), and one in another currency, which is not.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Source", currencyCode: "USD");
        var sameCurrency = await ledger.AddInvestmentAccountAsync("Dest A", currencyCode: "USD");
        var otherCurrency = await ledger.AddInvestmentAccountAsync("Dest B", currencyCode: "EUR");
        var security = await ledger.AddSecurityAsync("Index Fund", "IDX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<Guid> Post(CreateInvestmentTransactionRequest req)
        {
            var resp = await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions", req);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            return (await resp.Content.ReadFromJsonAsync<CreateInvestmentTransactionResponse>())!.HeaderId;
        }

        await Post(new() { BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 100m, PostedAt = Utc(2020, 1, 1) });
        var sellId = await Post(new() { BrokerageAccountId = source.Id, Action = "sell", SecurityId = security, Shares = -10m, Price = 150m, PostedAt = Utc(2024, 6, 1) });
        var sameBuyId = await Post(new() { BrokerageAccountId = sameCurrency.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 150m, PostedAt = Utc(2024, 6, 1) });
        await Post(new() { BrokerageAccountId = otherCurrency.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 140m, PostedAt = Utc(2024, 6, 1) });

        var candidates = await new InvestmentReportingRepository(Db.NewDbContext(), TimeProvider.System)
            .FindInKindTransferCandidatesAsync(ledger.LedgerId);

        var candidate = Assert.Single(candidates);
        Assert.Equal(sellId, candidate.SellHeaderId);
        Assert.Equal(sameBuyId, candidate.BuyHeaderId);
        Assert.Equal(sameCurrency.Id, candidate.DestAccountId);
    }
}
