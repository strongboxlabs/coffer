using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for the investment-transactions endpoint
/// surface (ADR-0029). Covers:
/// <list type="bullet">
///   <item>POST happy paths for each action shape category
///     (Buy / Buy+Fee / Sell / Dividend / Reinvest / Xfr / Misc).</item>
///   <item>POST validation rejections from the action × field
///     matrix.</item>
///   <item>GET .../lots — open lots ordered for the editor's FIFO
///     preview popover.</item>
///   <item>PATCH — full wholesale reshape, plus cross-topic
///     rejection.</item>
///   <item>DELETE — hard / soft policy, plus cross-topic rejection.</item>
///   <item>Cross-topic protection on the bank
///     <c>/transactions</c> endpoint (POST / PATCH / DELETE all
///     refuse investment-shape headers / accounts).</item>
/// </list>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvestmentTransactionsEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentTransactionsEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private sealed record InvestmentSeed(
        SyntheticLedger Ledger,
        Guid BrokerageId,
        Guid HoldingsId,
        Guid SecurityId,
        Guid IncomeCategoryId,
        Guid ExpenseCategoryId,
        Guid TransferTargetId);

    private async Task<InvestmentSeed> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var securityId = await ledger.AddSecurityAsync("Index ETF A", ticker: "ETFA");
        var incomeCategory = await ledger.AddCategoryAsync("Dividends", kind: "income");
        var expenseCategory = await ledger.AddCategoryAsync("Trading Commission", kind: "expense");
        var bank = await ledger.AddBankAccountAsync("Checking");
        return new InvestmentSeed(
            ledger,
            BrokerageId: brokerage.Id,
            HoldingsId: brokerage.HoldingsAccountId!.Value,
            SecurityId: securityId,
            IncomeCategoryId: incomeCategory.Id,
            ExpenseCategoryId: expenseCategory.Id,
            TransferTargetId: bank.Id);
    }

    // ---------- POST happy paths ----------

    [Fact]
    public async Task Post_buy_creates_header_legs_and_lot()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var postedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = postedAt,
                Action = "buy",
                Payee = "Buy ETFA",
                SecurityId = seed.SecurityId,
                Shares = 10m,
                Price = 650m,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var headerId = await ReadHeaderIdAsync(response);

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == headerId);
        Assert.Equal("buy", header.Action);
        Assert.Equal("manual", header.Origin);

        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId).ToListAsync();
        Assert.Equal(2, legs.Count);
        var cashLeg = legs.Single(l => l.AccountId == seed.BrokerageId);
        var holdingsLeg = legs.Single(l => l.AccountId == seed.HoldingsId);
        Assert.Equal("security", cashLeg.PostingRole);
        Assert.Equal("security", holdingsLeg.PostingRole);
        Assert.Equal(-6500m, cashLeg.Amount);
        Assert.Equal(6500m, holdingsLeg.Amount);
        Assert.Equal(10m, holdingsLeg.Quantity);
        Assert.Equal(650m, holdingsLeg.UnitPrice);

        // Lot created on the holdings-side leg.
        var lot = await db.Lots.AsNoTracking()
            .SingleAsync(l => l.LegId == holdingsLeg.Id);
        Assert.Equal(10m, lot.Quantity);
        Assert.False(lot.IsClosed);
    }

    [Fact]
    public async Task Post_buy_honors_authoritative_amount_and_derives_2dp_money_and_price()
    {
        // ADR-0073: the request Amount is the real settled cash (2dp) and is
        // authoritative — the cash + holdings legs carry it EXACTLY, and
        // unit_price is DERIVED from it (amount ÷ |shares|), not the reverse.
        // A rounded wire price (4.878 sh @ 29.45) would give 143.6571; the real
        // total 143.68 must win, and no sub-cent amount may land on a leg.
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 4.878m,
                Price = 29.45m,
                Amount = 143.68m,
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var headerId = await ReadHeaderIdAsync(response);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId).ToListAsync();
        var cashLeg = legs.Single(l => l.AccountId == seed.BrokerageId);
        var holdingsLeg = legs.Single(l => l.AccountId == seed.HoldingsId);

        // Authoritative amount, exact to the cent — not 143.6571.
        Assert.Equal(-143.68m, cashLeg.Amount);
        Assert.Equal(143.68m, holdingsLeg.Amount);
        // Money is exactly 2 decimals (no sub-cent).
        Assert.Equal(cashLeg.Amount, Math.Round(cashLeg.Amount, 2));
        Assert.Equal(holdingsLeg.Amount, Math.Round(holdingsLeg.Amount, 2));
        // Price is derived from the amount at 6dp display precision.
        Assert.Equal(
            Math.Round(143.68m / 4.878m, 6, MidpointRounding.AwayFromZero),
            holdingsLeg.UnitPrice);
        Assert.Equal(4.878m, holdingsLeg.Quantity);
    }

    [Fact]
    public async Task Post_buy_without_amount_rounds_price_times_shares_to_cents()
    {
        // Fallback path (no Amount supplied): principal = round(price × |shares|, 2)
        // so a product with a sub-cent tail (1.5 × 678.55 = 1017.825) still
        // lands on the leg at exactly 2 decimals (ADR-0073) — never sub-cent.
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 1.5m,
                Price = 678.55m,
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var headerId = await ReadHeaderIdAsync(response);

        await using var db = _fixture.NewDbContext();
        var cashLeg = await db.TxnLegs.AsNoTracking()
            .SingleAsync(l => l.HeaderId == headerId && l.AccountId == seed.BrokerageId);
        // 1.5 × 678.55 = 1017.825 → rounded to 1017.83, never stored sub-cent.
        Assert.Equal(-1017.83m, cashLeg.Amount);
        Assert.Equal(cashLeg.Amount, Math.Round(cashLeg.Amount, 2));
    }

    [Fact]
    public async Task Post_buy_with_provider_hint_upserts_security_mapping()
    {
        // ADR-0031 Phase 3d.1: when the editor saves an investment
        // transaction that resolved a previously-unmapped ticker, the
        // endpoint records the (ledger, provider_key, ticker) →
        // security_id mapping so the NEXT sync of the same ticker
        // auto-resolves without prompting.
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 10m,
                Price = 650m,
                ProviderSecurityHint = new ProviderSecurityHint(
                    ProviderKey: "simplefin",
                    ProviderSecurityId: "ETFA"),
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var mapping = await db.ProviderSecurityMappings.AsNoTracking()
            .SingleOrDefaultAsync(m =>
                m.LedgerId == seed.Ledger.LedgerId
                && m.ProviderKey == "simplefin"
                && m.ProviderSecurityId == "ETFA");
        Assert.NotNull(mapping);
        Assert.Equal(seed.SecurityId, mapping!.SecurityId);
    }

    [Fact]
    public async Task Post_buy_with_provider_hint_is_idempotent_when_already_mapped()
    {
        // Re-saving with the same hint + same security is a no-op:
        // the upsert detects no change and returns without writing.
        // Re-saving with a DIFFERENT security overwrites (user
        // re-linked the ticker).
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var requestBody = new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = seed.BrokerageId,
            PostedAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
            Action = "buy",
            SecurityId = seed.SecurityId,
            Shares = 10m,
            Price = 650m,
            ProviderSecurityHint = new ProviderSecurityHint(
                ProviderKey: "simplefin",
                ProviderSecurityId: "ETFA"),
        };

        var first = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions", requestBody);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions", requestBody);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        await using var db = _fixture.NewDbContext();
        var mappings = await db.ProviderSecurityMappings.AsNoTracking()
            .Where(m => m.LedgerId == seed.Ledger.LedgerId
                        && m.ProviderKey == "simplefin"
                        && m.ProviderSecurityId == "ETFA")
            .ToListAsync();
        Assert.Single(mappings);
        Assert.Equal(seed.SecurityId, mappings[0].SecurityId);
    }

    [Fact]
    public async Task Post_buy_with_provider_hint_but_no_SecurityId_does_not_persist_mapping()
    {
        // Safety check: even if the request includes a provider hint,
        // we only record the mapping when there's a SecurityId to
        // point it at. (The action × field matrix gates the SecurityId
        // requirement for buys; this asserts the side-effect honors
        // that — no orphan mappings without a resolved security.)
        //
        // We trigger the gate by sending a transfer (no SecurityId
        // required by the action) but still passing a provider hint.
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
                Action = "transfer",
                Amount = 1000m,
                TransferAccountId = seed.TransferTargetId,
                ProviderSecurityHint = new ProviderSecurityHint(
                    ProviderKey: "simplefin",
                    ProviderSecurityId: "ETFA"),
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var mappings = await db.ProviderSecurityMappings.AsNoTracking()
            .Where(m => m.LedgerId == seed.Ledger.LedgerId)
            .ToListAsync();
        Assert.Empty(mappings);
    }

    [Fact]
    public async Task Patch_upgrades_bank_shape_sync_row_when_ingest_action_hint_set()
    {
        // ADR-0031 Phase 3d.2: the cross-topic gate that normally
        // rejects investment-PATCH against a bank-shape header is
        // relaxed when `ingest_action_hint` is set on that header.
        // This is the canonical upgrade path for a sync-imported
        // brokerage row: the orchestrator (Phase 3c) inserts
        // bank-shape with the classifier's outputs as hints; the
        // user reviews via the editor + saves through
        // /investment-transactions, which converts the row to
        // proper investment-shape in place (header_id stays, FITID
        // preserved, ingest_action_hint stays as audit metadata).
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Seed a bank-shape sync row: header with origin='simplefin',
        // action=null, ingest_action_hint='buy', + a cash leg pair
        // (brokerage ↔ Uncategorized) — exactly what
        // IngestOrchestrator writes on a classifier-recognized
        // brokerage description with no existing mapping.
        var uncategorized = await seed.Ledger.AddCategoryAsync(
            "Uncategorized", kind: "expense");
        var bankShapeHeaderId = Guid.NewGuid();
        await using (var db = _fixture.NewDbContext())
        {
            db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = bankShapeHeaderId,
                LedgerId = seed.Ledger.LedgerId,
                Origin = "online_import",
                ProviderKey = "simplefin",
                Payee = "YOU BOUGHT ACME INDEX FUND S&P 500 ETF (ETFA) Cash",
                PostedAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),                IsPending = false,
                NeedsReview = true,
                // mig 105: SimpleFIN id lands on external_id;
                // OFX columns aren't written by SimpleFIN.
                ExternalId = "fitid-upgrade-1",
                IngestActionHint = "buy",
            });
            db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = bankShapeHeaderId,
                LedgerId = seed.Ledger.LedgerId,
                AccountId = seed.BrokerageId,
                PostingIndex = 0,
                Amount = -300m,
            });
            db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = bankShapeHeaderId,
                LedgerId = seed.Ledger.LedgerId,
                AccountId = uncategorized.Id,
                PostingIndex = 0,
                Amount = 300m,
            });
            await db.SaveChangesAsync();
        }

        // PATCH it via /investment-transactions with the upgrade
        // payload — same shape the editor sends when the user
        // confirms a hint row.
        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions/{bankShapeHeaderId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                Payee = "Buy ETFA (upgraded from sync)",
                SecurityId = seed.SecurityId,
                Shares = 1m,
                Price = 300m,
                ProviderSecurityHint = new ProviderSecurityHint(
                    ProviderKey: "simplefin",
                    ProviderSecurityId: "ETFA"),
            });
        Assert.Equal(HttpStatusCode.NoContent, patchResp.StatusCode);

        // Verify: header now carries action='buy'; FITID +
        // ingest_action_hint preserved; legs reshape to investment-
        // shape with posting_role='security'.
        await using var verify = _fixture.NewDbContext();
        var header = await verify.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == bankShapeHeaderId);
        Assert.Equal("buy", header.Action);
        Assert.Equal("online_import", header.Origin);
        Assert.Equal("simplefin", header.ProviderKey);
        Assert.Equal("fitid-upgrade-1", header.ExternalId);
        Assert.Null(header.OnlineMatchFitid);
        Assert.Null(header.OnlineMatchFiId);
        Assert.Equal("buy", header.IngestActionHint);

        var legs = await verify.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == bankShapeHeaderId)
            .ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.All(legs, l => Assert.Equal("security", l.PostingRole));
        // Lot created on the holdings-side leg.
        var holdingsLeg = legs.Single(l => l.AccountId == seed.HoldingsId);
        Assert.Equal(seed.SecurityId, holdingsLeg.SecurityId);
        Assert.Equal(1m, holdingsLeg.Quantity);
        Assert.Equal(300m, holdingsLeg.UnitPrice);

        // Mapping recorded so future syncs of ETFA auto-resolve.
        var mapping = await verify.ProviderSecurityMappings.AsNoTracking()
            .SingleOrDefaultAsync(m =>
                m.LedgerId == seed.Ledger.LedgerId
                && m.ProviderKey == "simplefin"
                && m.ProviderSecurityId == "ETFA");
        Assert.NotNull(mapping);
        Assert.Equal(seed.SecurityId, mapping!.SecurityId);
    }

    [Fact]
    public async Task Patch_upgrades_feed_imported_bank_shape_row_without_ingest_action_hint()
    {
        // ADR-0031 Phase 3d.2 + gap-fix: a SimpleFIN-imported
        // brokerage row whose description the classifier didn't
        // recognize (real-world data shows the regex misses
        // a major brokerage / 529 / a bank formats) is still upgradable
        // via PATCH. Without this, those rows would be stranded in
        // the brokerage register with no upgrade path.
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var uncategorized = await seed.Ledger.AddCategoryAsync(
            "Uncategorized G3", kind: "expense");
        var headerId = Guid.NewGuid();
        await using (var db = _fixture.NewDbContext())
        {
            db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = headerId,
                LedgerId = seed.Ledger.LedgerId,
                Origin = "online_import",
                ProviderKey = "simplefin",      // feed-imported
                Payee = "ACME TAX MANAGED CAPITAL APPRC ADMIRAL CL",
                PostedAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),                NeedsReview = true,
                ExternalId = "fitid-gap-fix",
                // NOTE: NO ingest_action_hint — classifier didn't match.
            });
            db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = headerId,
                LedgerId = seed.Ledger.LedgerId,
                AccountId = seed.BrokerageId,
                PostingIndex = 0,
                Amount = -579m,
            });
            db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = headerId,
                LedgerId = seed.Ledger.LedgerId,
                AccountId = uncategorized.Id,
                PostingIndex = 0,
                Amount = 579m,
            });
            await db.SaveChangesAsync();
        }

        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 1m,
                Price = 579m,
            });
        Assert.Equal(HttpStatusCode.NoContent, patchResp.StatusCode);

        await using var verify = _fixture.NewDbContext();
        var header = await verify.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == headerId);
        Assert.Equal("buy", header.Action);
        Assert.Equal("online_import", header.Origin);
        Assert.Equal("simplefin", header.ProviderKey);
        Assert.Equal("fitid-gap-fix", header.ExternalId);
        // Hint stays null — classifier never matched + user upgrade
        // didn't synthesize one.
        Assert.Null(header.IngestActionHint);
    }

    [Fact]
    public async Task Patch_still_rejects_bank_header_without_ingest_action_hint()
    {
        // Regression guard: the cross-topic gate stays in place for
        // ordinary bank-shape headers. Only headers with the
        // classifier hint get the upgrade pass.
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var uncategorized = await seed.Ledger.AddCategoryAsync(
            "Uncategorized 2", kind: "expense");
        var bankHeaderId = Guid.NewGuid();
        await using (var db = _fixture.NewDbContext())
        {
            db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = bankHeaderId,
                LedgerId = seed.Ledger.LedgerId,
                Origin = "manual",
                Payee = "Coffee shop",
                PostedAt = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),                // NOTE: NO ingest_action_hint set.
            });
            db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = bankHeaderId,
                LedgerId = seed.Ledger.LedgerId,
                AccountId = seed.BrokerageId,
                PostingIndex = 0,
                Amount = -5m,
            });
            db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = bankHeaderId,
                LedgerId = seed.Ledger.LedgerId,
                AccountId = uncategorized.Id,
                PostingIndex = 0,
                Amount = 5m,
            });
            await db.SaveChangesAsync();
        }

        var patchResp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions/{bankHeaderId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 1m,
                Price = 5m,
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, patchResp.StatusCode);
    }

    [Fact]
    public async Task Post_buy_with_fee_includes_commission_in_lot_unit_cost()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        await seed.Ledger.SetIsTradeCommissionAsync(seed.BrokerageId, isOn: true);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 10m,
                Price = 650m,
                FeeAccountId = seed.ExpenseCategoryId,
                FeeAmount = 1.00m,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var headerId = await ReadHeaderIdAsync(response);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId).ToListAsync();
        // sec pair (cash + holdings) + fee pair (cash + category) = 4 legs.
        Assert.Equal(4, legs.Count);
        Assert.Contains(legs, l => l.PostingRole == "fee");

        var holdingsLeg = legs.Single(l => l.AccountId == seed.HoldingsId);
        var lot = await db.Lots.AsNoTracking()
            .SingleAsync(l => l.LegId == holdingsLeg.Id);
        // With is_trade_commission=TRUE, recompute folds the $1.00 fee
        // into basis: (6500 + 1) / 10 = 650.10
        Assert.Equal(650.10m, lot.UnitCost);
    }

    [Fact]
    public async Task Post_sell_creates_disposing_holdings_delta_no_new_lot()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Seed an open lot to sell from.
        await SeedOpenLotAsync(seed, quantity: 20m, unitCost: 600m);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "sell",
                SecurityId = seed.SecurityId,
                Shares = -5m,                  // signed: negative on dispose
                Price = 700m,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var headerId = await ReadHeaderIdAsync(response);

        await using var db = _fixture.NewDbContext();
        var holdingsLeg = await db.TxnLegs.AsNoTracking()
            .SingleAsync(l => l.HeaderId == headerId && l.AccountId == seed.HoldingsId);
        Assert.Equal(-5m, holdingsLeg.Quantity);
        // No new lot for a sell.
        var newLots = await db.Lots.AsNoTracking()
            .Where(l => l.LegId == holdingsLeg.Id).ToListAsync();
        Assert.Empty(newLots);
    }

    [Fact]
    public async Task Post_dividend_cash_creates_income_pair()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "dividend_cash",
                SecurityId = seed.SecurityId,
                Amount = 30.57m,
                CategoryAccountId = seed.IncomeCategoryId,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var headerId = await ReadHeaderIdAsync(response);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId).ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.All(legs, l => Assert.Equal("income", l.PostingRole));

        var cashLeg = legs.Single(l => l.AccountId == seed.BrokerageId);
        Assert.Equal(30.57m, cashLeg.Amount);
        Assert.Equal(seed.SecurityId, cashLeg.SecurityId);   // pinned for per-security queries
        Assert.Null(cashLeg.Quantity);                       // qty=0 suppressed → null
    }

    [Fact]
    public async Task Post_dividend_reinvest_creates_income_and_sec_pairs()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "dividend_reinvest",
                SecurityId = seed.SecurityId,
                Shares = 0.019m,
                Price = 652.10m,
                CategoryAccountId = seed.IncomeCategoryId,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var headerId = await ReadHeaderIdAsync(response);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .OrderBy(l => l.PostingIndex).ThenBy(l => l.Id)
            .ToListAsync();
        // inc pair + sec pair = 4 legs.
        Assert.Equal(4, legs.Count);
        Assert.Contains(legs, l => l.PostingRole == "income");
        Assert.Contains(legs, l => l.PostingRole == "security");
        // Brokerage cash nets to zero (inc + sec cancel).
        var cashOnBrokerage = legs.Where(l => l.AccountId == seed.BrokerageId)
            .Sum(l => l.Amount);
        Assert.Equal(0m, cashOnBrokerage);
    }

    [Fact]
    public async Task Post_transfer_creates_xfr_pair_no_fee_allowed()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "transfer",
                Amount = 1000m,
                TransferAccountId = seed.TransferTargetId,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var headerId = await ReadHeaderIdAsync(response);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId).ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.All(legs, l => Assert.Equal("transfer", l.PostingRole));
    }

    [Fact]
    public async Task Post_misc_expense_uses_income_role_with_negative_cash()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "misc",
                Amount = -25m,                  // negative = expense direction
                CategoryAccountId = seed.ExpenseCategoryId,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var headerId = await ReadHeaderIdAsync(response);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId).ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.All(legs, l => Assert.Equal("income", l.PostingRole));
        Assert.Equal(-25m, legs.Single(l => l.AccountId == seed.BrokerageId).Amount);
    }

    // ---------- POST validation rejections ----------

    [Fact]
    public async Task Post_rejects_unknown_action()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "spinoff",            // not in catalog
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertCodeAsync(response, "investment-txn-action-invalid");
    }

    [Fact]
    public async Task Post_rejects_non_investment_brokerage_account()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Use the bank account id (not investment) as the brokerage.
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.TransferTargetId,   // bank account
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 1m,
                Price = 100m,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertCodeAsync(response, "investment-txn-account-not-investment");
    }

    [Fact]
    public async Task Post_buy_rejects_missing_shares()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                // Shares omitted
                Price = 100m,
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertCodeAsync(response, "investment-txn-shares-required");
    }

    [Fact]
    public async Task Post_rejects_fee_amount_without_fee_account()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 1m,
                Price = 100m,
                FeeAmount = 1m,        // amount without account
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertCodeAsync(response, "investment-txn-fee-without-account");
    }

    // ---------- GET lots ----------

    [Fact]
    public async Task Get_lots_returns_open_lots_ordered_ascending_by_acquired_at()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var older = await SeedOpenLotAsync(seed,
            quantity: 50m, unitCost: 600m,
            acquiredAt: new DateTime(2023, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        var newer = await SeedOpenLotAsync(seed,
            quantity: 25m, unitCost: 650m,
            acquiredAt: new DateTime(2024, 6, 10, 0, 0, 0, DateTimeKind.Utc));

        var response = await client.GetAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/accounts/{seed.BrokerageId}/securities/{seed.SecurityId}/lots");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var lots = await response.Content.ReadFromJsonAsync<InvestmentLotDto[]>();
        Assert.NotNull(lots);
        Assert.Equal(2, lots!.Length);
        // Oldest first.
        Assert.Equal(older, lots[0].LotId);
        Assert.Equal(newer, lots[1].LotId);
    }

    [Fact]
    public async Task Get_lots_rejects_non_investment_account()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Use the bank account (not investment).
        var response = await client.GetAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/accounts/{seed.TransferTargetId}/securities/{seed.SecurityId}/lots");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertCodeAsync(response, "investment-txn-account-not-investment");
    }

    // ---------- PATCH ----------

    [Fact]
    public async Task Patch_reshapes_a_buy_into_a_buy_with_fee()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 10m,
                Price = 650m,
            });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var headerId = await ReadHeaderIdAsync(createResponse);

        var patchResponse = await client.PatchAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 10m,
                Price = 650m,
                FeeAccountId = seed.ExpenseCategoryId,
                FeeAmount = 1.00m,
            });
        Assert.Equal(HttpStatusCode.NoContent, patchResponse.StatusCode);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId).ToListAsync();
        // sec pair + new fee pair = 4 legs.
        Assert.Equal(4, legs.Count);
        Assert.Contains(legs, l => l.PostingRole == "fee");
    }

    [Fact]
    public async Task Patch_rejects_bank_header_with_cross_topic_code()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Create a bank transaction (action=null on header).
        var bankCreate = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = seed.TransferTargetId,
                Postings = new[]
                {
                    new TransactionPosting
                    {
                        CounterpartyAccountId = seed.ExpenseCategoryId,
                        Amount = -50m,
                    },
                },
            });
        Assert.Equal(HttpStatusCode.Created, bankCreate.StatusCode);
        using var doc = JsonDocument.Parse(await bankCreate.Content.ReadAsStringAsync());
        var bankHeaderId = doc.RootElement.GetProperty("headerId").GetGuid();

        var patchResponse = await client.PatchAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions/{bankHeaderId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                Action = "buy",
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, patchResponse.StatusCode);
        await AssertCodeAsync(patchResponse, "investment-txn-header-not-investment");
    }

    // ---------- DELETE ----------

    [Fact]
    public async Task Delete_hard_deletes_manual_investment_txn()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "transfer",
                Amount = 100m,
                TransferAccountId = seed.TransferTargetId,
            });
        var headerId = await ReadHeaderIdAsync(createResponse);

        var deleteResponse = await client.DeleteAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions/{headerId}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        var payload = await deleteResponse.Content.ReadFromJsonAsync<DeleteTransactionResponse>();
        Assert.Equal("hard-deleted", payload!.Kind);

        await using var db = _fixture.NewDbContext();
        var exists = await db.TxnHeaders.AsNoTracking()
            .AnyAsync(h => h.Id == headerId);
        Assert.False(exists);
    }

    // ---------- Cross-topic rejections from /transactions ----------

    [Fact]
    public async Task Bank_post_rejects_investment_source_account()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = seed.BrokerageId,        // investment-typed
                Postings = new[]
                {
                    new TransactionPosting
                    {
                        CounterpartyAccountId = seed.ExpenseCategoryId,
                        Amount = -50m,
                    },
                },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertCodeAsync(response, "transaction-account-is-investment");
    }

    [Fact]
    public async Task Bank_patch_rejects_investment_header_with_cross_topic_code()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = seed.SecurityId,
                Shares = 1m,
                Price = 100m,
            });
        var investmentHeaderId = await ReadHeaderIdAsync(createResponse);

        var patchResponse = await client.PatchAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/{investmentHeaderId}",
            new PatchTransactionRequest { Payee = "edited" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, patchResponse.StatusCode);
        await AssertCodeAsync(patchResponse, "transaction-header-is-investment");
    }

    [Fact]
    public async Task Bank_delete_rejects_investment_header_with_cross_topic_code()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var createResponse = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = seed.BrokerageId,
                PostedAt = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
                Action = "transfer",
                Amount = 100m,
                TransferAccountId = seed.TransferTargetId,
            });
        var investmentHeaderId = await ReadHeaderIdAsync(createResponse);

        var deleteResponse = await client.DeleteAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/{investmentHeaderId}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deleteResponse.StatusCode);
        await AssertCodeAsync(deleteResponse, "transaction-header-is-investment");
    }

    // ---------- Helpers ----------

    private static async Task<Guid> ReadHeaderIdAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("headerId").GetGuid();
    }

    private static async Task AssertCodeAsync(HttpResponseMessage response, string expectedCode)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var code = doc.RootElement.GetProperty("code").GetString();
        Assert.Equal(expectedCode, code);
    }

    /// <summary>
    /// Insert a synthetic open lot + matching holding row directly so
    /// Sell / lots tests have a pre-existing position to consume from.
    /// Bypasses the import path because the tests don't need to
    /// exercise it — they just need the read state.
    /// </summary>
    private async Task<Guid> SeedOpenLotAsync(
        InvestmentSeed seed,
        decimal quantity,
        decimal unitCost,
        DateTime? acquiredAt = null)
    {
        var asOf = acquiredAt ?? new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var db = _fixture.NewDbContext();

        // Upsert holding for (holdings sibling, security).
        var holding = await db.Holdings
            .SingleOrDefaultAsync(h => h.AccountId == seed.HoldingsId
                                    && h.SecurityId == seed.SecurityId);
        if (holding is null)
        {
            holding = new HoldingRow
            {
                Id = Guid.NewGuid(),
                AccountId = seed.HoldingsId,
                SecurityId = seed.SecurityId,
                LedgerId = seed.Ledger.LedgerId,
                Quantity = quantity,
                CostBasis = quantity * unitCost,
                AsOf = asOf,
            };
            db.Holdings.Add(holding);
        }

        // Seed a header + leg so the lot's leg_id FK resolves.
        var headerId = Guid.NewGuid();
        db.TxnHeaders.Add(new TxnHeaderRow
        {
            Id = headerId,
            LedgerId = seed.Ledger.LedgerId,
            Origin = "manual",
            Action = "buy",
            PostedAt = asOf,
        });
        var legId = Guid.NewGuid();
        db.TxnLegs.Add(new TxnLegRow
        {
            Id = legId,
            HeaderId = headerId,
            LedgerId = seed.Ledger.LedgerId,
            AccountId = seed.HoldingsId,
            PostingIndex = 0,
            Amount = quantity * unitCost,
            SecurityId = seed.SecurityId,
            Quantity = quantity,
            UnitPrice = unitCost,
            PostingRole = "security",
            CreatedAt = DateTime.UtcNow,
        });
        db.TxnLegs.Add(new TxnLegRow
        {
            Id = Guid.NewGuid(),
            HeaderId = headerId,
            LedgerId = seed.Ledger.LedgerId,
            AccountId = seed.BrokerageId,
            PostingIndex = 0,
            Amount = -quantity * unitCost,
            PostingRole = "security",
            CreatedAt = DateTime.UtcNow,
        });

        var lotId = Guid.NewGuid();
        db.Lots.Add(new LotRow
        {
            Id = lotId,
            HoldingId = holding.Id,
            LegId = legId,
            LedgerId = seed.Ledger.LedgerId,
            Quantity = quantity,
            UnitCost = unitCost,
            AcquiredAt = asOf,
            IsClosed = false,
        });

        await db.SaveChangesAsync();
        return lotId;
    }
}
