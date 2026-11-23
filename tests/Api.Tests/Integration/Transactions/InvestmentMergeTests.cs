using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Investment-side merge (the brokerage equivalent of the bank merge). A fresh,
/// needs-review investment row folds into a settled candidate: the candidate is
/// the surviving winner (stamped is_merge_winner + adopts the loser's date), the
/// loser is stamped is_merged_into and its shares drop out of holdings.
///
/// Matching is by the security-leg's signed principal amount (stable across the
/// share-count rounding that differs between feeds), same holdings-sibling
/// account + security, within ±7 effective days. The holdings drop verifies
/// migration 163 (recompute excludes is_merged_into) and the interceptors counting
/// a merge stamp or a moved date on a header as a change to every line it has
/// (ADR-0032, 2026-11-03 amendment). Atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvestmentMergeTests : DatabaseTest
{
    public InvestmentMergeTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static async Task<Guid> BuyAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId,
        DateTime postedAt, decimal shares, decimal amount, decimal price)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerageId,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = shares,
                Price = price,
                Amount = amount,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();
    }

    /// <summary>
    /// Flip a manually-created investment row to needs_review.
    /// </summary>
    /// <remarks>
    /// <b>This fabricates a shape ingest never writes</b>, and it is the only
    /// writer of it in existence: a row with an action AND holdings legs AND
    /// needs_review. Real unreviewed rows are bank-shaped (see
    /// <see cref="IngestOrchestrator"/>), because posting_role is written only
    /// together with header.action — an invariant upheld by repository code, not
    /// by a trigger (migration 084 dropped trg_validate_posting_role) — so the investment shape only appears at Accept, which clears needs_review
    /// in the same transaction.
    ///
    /// It survives in exactly TWO tests, both of which need a loser that actually
    /// holds shares so that "the loser's shares drop out of holdings" and "its lot
    /// is not consumable" are observable at all. Those guard a DEFENSIVE path:
    /// a real merge loser is bank-shaped and contributes no holdings, so nothing
    /// reachable exercises them today. They are worth keeping as a guard on
    /// migration 163 — but they are not evidence that merge works.
    ///
    /// Everything else in this file builds its loser with the real OFX
    /// importer. That is not a style preference. An anchor written against this
    /// fixture can leave a query unreachable in the product and still green in the
    /// suite: raw SQL can write a state no code path produces, and a test over that
    /// state proves nothing about the product.
    /// Before adding another caller here, name the shipping code path that writes
    /// the state you want. If there is none, that is a finding about the product.
    /// </remarks>
    private async Task MarkNeedsReviewAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE txn_headers SET needs_review = TRUE WHERE id = {headerId}");
    }

    [Fact]
    public async Task Merge_folds_loser_into_winner_and_drops_its_shares_from_holdings()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 75.125m, amount: 1000.50m, price: 13.32m);
        var loser = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 75.193m, amount: 1000.50m, price: 13.31m);
        await MarkNeedsReviewAsync(loser);

        // Both buys' shares are in holdings before the merge.
        await using (var db0 = Db.NewDbContext())
        {
            var qty0 = await db0.Holdings.AsNoTracking()
                .Where(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId)
                .Select(h => h.Quantity).SingleAsync();
            Assert.Equal(75.125m + 75.193m, qty0);
        }

        // Fold the loser into the winner (merge-only PATCH; account_id → survivor entry).
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge?account_id={brokerage.Id}",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        // Loser → merged into winner; winner → merge winner.
        var loserRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
        var winnerRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == winner);
        Assert.Equal(winner, loserRow.IsMergedInto);
        Assert.True(winnerRow.IsMergeWinner);

        // Holdings now reflect ONLY the winner's shares — the merged loser dropped out
        // (mig 163 recompute, run for the loser's lines by HoldingsRecomputeInterceptor
        // when the save stamps is_merged_into).
        var qty = await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId)
            .Select(h => h.Quantity).SingleAsync();
        Assert.Equal(75.125m, qty);
    }

    [Fact]
    public async Task Merge_rejects_self_and_settled_editor_with_422()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("REJX", ticker: "REJX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 4.321m, amount: 108.47m, price: 25.102985m);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        // Self-merge: rejected.
        var self = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = loser });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        using (var doc = JsonDocument.Parse(await self.Content.ReadAsStringAsync()))
            Assert.Equal("merge-source-invalid", doc.RootElement.GetProperty("code").GetString());

        // Editor is a SETTLED row (winner, not needs_review) → can't be a loser.
        var settledEditor = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{winner}/merge",
            new MergeTransactionRequest { FromHeaderId = loser });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, settledEditor.StatusCode);
    }

    /// <summary>
    /// A merged loser's LOT must not survive the recompute and stay consumable.
    /// </summary>
    /// <remarks>
    /// The existing coverage asserts the merged loser drops out of
    /// <c>holdings.quantity</c>, which it does. Lots are a separate table and a
    /// separate code path, and migration 163 treats them asymmetrically: the
    /// lot RESET (163:91-112) excludes merged headers, so a loser's lot is
    /// never restored — but nothing DELETES it, and the FIFO consumption loop
    /// (163:202-211) joins <c>live_txn_headers</c> filtering only on
    /// <c>posted_at</c>, with no <c>is_merged_into</c> guard. A stale lot could
    /// therefore be drawn against by a later sell, giving the wrong cost basis
    /// and the wrong realized gain while holdings looked right.
    ///
    /// Both buys carry the SAME principal (that is what makes them merge
    /// candidates) and DIFFERENT share counts, so the two lots have different
    /// unit costs — which is what makes a wrong consumption visible in the
    /// realized gain rather than cancelling out.
    /// </remarks>
    [Fact]
    public async Task Merged_losers_lot_is_not_consumable_by_a_later_sell()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("LOTX", ticker: "LOTX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Holdings and realized gains key on the HOLDINGS account, not the
        // brokerage cash sleeve the transaction is posted against.
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        var buyDate = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc);
        // Winner: a round lot for a round principal → the lower unit cost.
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            buyDate, shares: 100m, amount: 1000m, price: 10m);
        // Loser: half the shares for the SAME principal → twice the unit cost. Same
        // principal keeps it a candidate; the different unit cost is the detector.
        var loser = await BuyAsync(client, ledger, brokerage.Id, securityId,
            buyDate, shares: 50m, amount: 1000m, price: 20m);
        await MarkNeedsReviewAsync(loser);

        var merge = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge?account_id={brokerage.Id}",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);

        // DIRECT PROBE: no open lot may remain tied to a leg of the merged loser.
        await using (var db = Db.NewDbContext())
        {
            var loserLotQty = await (
                from lot in db.Lots.AsNoTracking()
                join leg in db.TxnLegs.AsNoTracking() on lot.LegId equals leg.Id
                where leg.HeaderId == loser && !lot.IsClosed
                select lot.Quantity).ToListAsync();
            Assert.True(
                loserLotQty.Count == 0 || loserLotQty.All(q => q == 0m),
                $"a merged loser left {loserLotQty.Count} open lot(s) with quantities "
                    + $"[{string.Join(", ", loserLotQty)}] — a later sell can draw against them");
        }

        // CONSEQUENCE: sell the whole position. Only the winner's lot exists, so
        // the basis is the winner's principal and the gain is the proceeds less
        // that. If the loser's lot
        // were consumed instead (or first), the basis — and the gain — differ.
        var sell = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc),
                Action = "sell",
                SecurityId = securityId,
                // NEGATIVE: a disposal. Positive shares read as an acquisition
                // and the walk records no gain at all.
                Shares = -100m,
                Price = 30m,
                Amount = 3000m,
            });
        Assert.Equal(HttpStatusCode.Created, sell.StatusCode);

        await using (var db = Db.NewDbContext())
        {
            var all = await db.RealizedGains.AsNoTracking()
                .Where(g => g.SecurityId == securityId)
                .Select(g => new { g.AccountId, g.Quantity, g.Proceeds, g.CostBasisSold, g.RealizedGain })
                .ToListAsync();
            var lots = await (from lot in db.Lots.AsNoTracking()
                              join leg in db.TxnLegs.AsNoTracking() on lot.LegId equals leg.Id
                              select new { lot.Quantity, lot.UnitCost, lot.IsClosed, leg.HeaderId })
                .ToListAsync();
            var gain = all.Sum(g => g.RealizedGain);
            Assert.True(gain == 2000m,
                $"expected a 2000 gain; got {gain} from {all.Count} row(s): "
                + string.Join(" ; ", all.Select(g => $"acct={g.AccountId} qty={g.Quantity} proceeds={g.Proceeds} basis={g.CostBasisSold} gain={g.RealizedGain}"))
                + $" || holdingsAcct={holdingsAccountId} lots=[{string.Join(", ", lots.Select(l => $"{l.Quantity}@{l.UnitCost} closed={l.IsClosed}"))}]");
        }
    }

    /// <summary>
    /// ADR-0082: merging bumps the survivor's leg to 'reconciling'.
    /// </summary>
    /// <remarks>
    /// The bank has done this since ADR-0082 and the investment branch returned
    /// before any recon handling. Nothing in ADR-0082's reasoning depends on the
    /// institution being a bank — a feed match is the institution acknowledging
    /// the transaction, and a brokerage confirming a trade is the same evidence
    /// — so the split read as an oversight rather than a decision.
    ///
    /// The assertion is on the BROKERAGE leg specifically. An investment header
    /// also has legs on the holdings sibling, which is an 'investment' account
    /// too, so a status picked by account TYPE could pass while landing on the
    /// wrong leg.
    /// </remarks>
    [Fact]
    public async Task Merge_bumps_the_survivor_to_reconciling_on_the_brokerage_leg()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 4.321m, amount: 108.47m, price: 25.102985m);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        await using (var before = Db.NewDbContext())
        {
            // Control: nothing is reconciling until the merge says so.
            var pre = await before.ResolvedTransactions.AsNoTracking()
                .Where(rt => rt.HeaderId == winner && rt.AccountId == brokerage.Id)
                .Select(rt => rt.Status).FirstAsync();
            Assert.NotEqual("reconciling", pre);
        }

        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge?account_id={brokerage.Id}",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = Db.NewDbContext();
        var status = await db.ResolvedTransactions.AsNoTracking()
            .Where(rt => rt.HeaderId == winner && rt.AccountId == brokerage.Id)
            .Select(rt => rt.Status)
            .FirstAsync();
        Assert.Equal("reconciling", status);
    }
    // =================================================================
    // The REACHABLE anchor: a row the OFX importer actually wrote.
    //
    // Everything above fabricates its loser with raw SQL. These do not:
    // they import a statement and then ask for candidates, which is the
    // only sequence a person can perform. A candidates query that
    // demanded a security leg and a non-null action from the edited row —
    // neither of which ingest writes — would return an empty list for
    // every real row while fixture-built tests reported it working.
    // =================================================================

    /// <summary>
    /// An OFX REINVEST lands unreviewed, bank-shaped: no action, no security leg,
    /// and a cash movement of exactly zero because no cash moves. Its settled
    /// twin must still be offered.
    /// </summary>
    /// <remarks>
    /// This is the case that proves the anchor cannot read legs. The row's own
    /// amount is zero, so the ONLY number that can match it is ingest_amount —
    /// the wire's TOTAL, carried since migration 228. Before that column existed
    /// there was nothing on a reinvest to match on at all.
    /// </remarks>
    [Fact]
    public async Task Imported_reinvest_is_offered_its_settled_twin()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        // The settled twin: same principal as the statement's TOTAL, but a
        // DIFFERENT share count. Feeds disagree on share rounding, so the
        // principal is the stable half and must be what matches.
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 4.335m, amount: 108.47m, price: 25.0219m);
        // Decoy: same security and date, different principal.
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 10m, amount: 500.00m, price: 50m);
        // Decoy: same principal, 20 days out of the ±7d window.
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            date.AddDays(20), shares: 4.3m, amount: 108.47m, price: 25.23m);

        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        var only = Assert.Single(candidates!);
        Assert.Equal(winner, only.HeaderId);
        // The person's own entry: the tooltip says the merge keeps it as entered.
        Assert.Equal("manual", only.Origin);
        // The buy's register row: cash going out of the brokerage.
        Assert.Equal(-108.47m, only.Amount);
        // Matched on principal, not shares: the statement's UNITS differ.
        Assert.Equal(4.335m, only.Shares);
    }

    /// <summary>
    /// A REINVEST whose TOTAL carries more places than the brokerage's currency keeps
    /// it as sent in ingest_amount (ADR-0114 D15). The candidates' legs are at the
    /// currency's places, so the total is read once at them, half away from zero, as
    /// the editor's prefill reads it (ADR-0114 D14), and the settled twin at the
    /// rounded figure is offered.
    /// </summary>
    [Fact]
    public async Task Imported_reinvest_whose_total_is_finer_than_its_currency_is_offered_its_twin_at_the_rounded_figure()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 4.335m, amount: 108.47m, price: 25.0219m);

        // A whole-cent figure and a fraction of a hundredth of a cent more.
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id, total: "-108.47004");
        await using (var db = Db.NewDbContext())
            Assert.Equal(108.47004m, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IngestAmount);

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        Assert.Equal(winner, Assert.Single(candidates!).HeaderId);
    }

    /// <summary>
    /// The imported row really is bank-shaped — no action, no security leg. Stated
    /// as its own assertion so that if ingest ever starts writing the investment
    /// shape, this fails loudly instead of quietly making the test above pass for
    /// a different reason than the one it documents.
    /// </summary>
    [Fact]
    public async Task Imported_row_has_no_action_and_no_security_leg()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var headerId = await ImportReinvestAsync(client, ledger, brokerage.Id);

        await using var db = Db.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == headerId);
        Assert.Null(header.Action);
        Assert.True(header.NeedsReview);
        // The detail lives in the carriers instead.
        Assert.Equal(108.47m, header.IngestAmount);
        Assert.Equal(4.321m, header.IngestShares);

        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId).ToListAsync();
        Assert.All(legs, l => Assert.Null(l.PostingRole));
        Assert.All(legs, l => Assert.Null(l.SecurityId));
        // And its own cash movement is zero, which is why ingest_amount exists.
        Assert.Equal(0m, legs.Where(l => l.AccountId == brokerage.Id).Sum(l => l.Amount));
    }

    /// <summary>
    /// A settled row of the same magnitude on a DIFFERENT security is still
    /// offered when OFX could not resolve the statement's ticker.
    /// </summary>
    /// <remarks>
    /// ingest_security_id is set only when the ticker hint matched an existing
    /// provider_security_mappings row. On a security seen for the first time it is
    /// NULL — and requiring a security match in that case would return nothing for
    /// exactly the rows most likely to be under review. So the security is
    /// disregarded there and the amount, account and window carry the match.
    /// </remarks>
    [Fact]
    public async Task Unmapped_ticker_disregards_the_candidate_security()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var otherSecurity = await ledger.AddSecurityAsync("OTHR", ticker: "OTHR");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, otherSecurity,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 4.321m, amount: 108.47m, price: 25.102985m);

        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        await using (var db = Db.NewDbContext())
        {
            // The premise: nothing mapped this CUSIP, so the hint did not resolve.
            var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
            Assert.NotNull(header.IngestSecurityTickerHint);
            var resolved = await db.ProviderSecurityMappings.AsNoTracking()
                .AnyAsync(m => m.LedgerId == ledger.LedgerId);
            Assert.False(resolved);
        }

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        var only = Assert.Single(candidates!);
        Assert.Equal(winner, only.HeaderId);
        Assert.Equal("OTHR", only.SecurityTicker);
    }

    /// <summary>
    /// Once OFX HAS resolved the ticker, a same-magnitude row on another security
    /// is no longer a candidate — only the matching security is offered.
    /// </summary>
    [Fact]
    public async Task Mapped_ticker_requires_the_candidate_security_to_match()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var mapped = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");
        var otherSecurity = await ledger.AddSecurityAsync("OTHR", ticker: "OTHR");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        var right = await BuyAsync(client, ledger, brokerage.Id, mapped,
            date, shares: 4.321m, amount: 108.47m, price: 25.102985m);
        // Same magnitude, same window, wrong security — must be excluded now.
        await BuyAsync(client, ledger, brokerage.Id, otherSecurity,
            date, shares: 4.321m, amount: 108.47m, price: 25.102985m);

        // The statement's SECLIST resolves UNIQUEID FAKE0001 to ticker FAKE, so
        // this is the mapping the importer looks up.
        await MapProviderSecurityAsync(ledger, "FAKE", mapped);

        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        await using (var db = Db.NewDbContext())
        {
            var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
            Assert.Equal("FAKE", header.IngestSecurityTickerHint);
        }

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        var only = Assert.Single(candidates!);
        Assert.Equal(right, only.HeaderId);
        Assert.Equal("FAKE", only.SecurityTicker);
    }

    /// <summary>
    /// End to end: import, then fold the imported row into its settled twin. The
    /// write path never required the investment shape, so this has always worked —
    /// there was simply no way to reach it, because the candidate that names the
    /// winner could not be obtained.
    /// </summary>
    [Fact]
    public async Task Imported_row_folds_into_its_twin_and_leaves_the_winner_standing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 4.321m, amount: 108.47m, price: 25.102985m);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        var patch = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        await using var db = Db.NewDbContext();
        var loserRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
        var winnerRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == winner);
        Assert.Equal(winner, loserRow.IsMergedInto);
        Assert.True(winnerRow.IsMergeWinner);
        Assert.Null(winnerRow.IsMergedInto);
        // Same end state the BANK merge reaches. Its editor pairs the stamp with
        // approve: true "to keep state coherent if the row is ever surfaced
        // again"; the investment branch takes a merge-only patch, so if it returned
        // before any approve handling its losers alone would stay flagged as
        // awaiting review and keep the sidebar dot lit.
        Assert.False(loserRow.NeedsReview);
    }

    /// <summary>
    /// A merge moves the winner to the imported row's date and writes none of its lines,
    /// and the save recomputes what reads that date: the winner's position re-sorts
    /// around it, and its trade prices the day it now sits on (ADR-0032, 2026-11-03
    /// amendment; ADR-0084 D4).
    /// </summary>
    /// <remarks>
    /// The position is built so its order shows. The winner is the earliest purchase;
    /// a second purchase follows it, and a sale of the winner's share count follows
    /// both, before the imported row's date. Before the merge the sale takes the
    /// winner's shares first in, first out, and the position keeps the second purchase's
    /// cost. Once the winner moves past the sale, the sale takes the second purchase's
    /// shares instead, and the position keeps the winner's whole cost besides what is
    /// left of the second.
    /// </remarks>
    [Fact]
    public async Task A_merge_that_moves_the_winner_re_sorts_its_position_and_prices_its_new_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Inside the match window before the imported row's date (the statement's).
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 17, 12, 0, 0, DateTimeKind.Utc),
            shares: 4.321m, amount: 108.47m, price: 25.102985m);
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 18, 12, 0, 0, DateTimeKind.Utc),
            shares: 10m, amount: 100m, price: 10m);
        var sale = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 1, 19, 12, 0, 0, DateTimeKind.Utc),
                Action = "sell",
                SecurityId = securityId,
                // A disposal is negative.
                Shares = -4.321m,
                Price = 30m,
                Amount = 129.63m,
            });
        Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);
        var importedDay = await DayOfAsync(loser);

        // Premises: the sale took the winner's shares, and the ledger checks clean.
        Assert.Equal(100m, Math.Round(await CostBasisAsync(holdingsAccountId, securityId), 2));
        await AssertConsistentAsync(client, ledger, "before the merge");

        var merge = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.Equal(HttpStatusCode.NoContent, merge.StatusCode);

        // Premise: the winner now sits on the imported row's day.
        Assert.Equal(importedDay, await DayOfAsync(winner));

        // The sale now takes the second purchase's shares at its unit cost.
        Assert.Equal(100m - 43.21m + 108.47m, await CostBasisAsync(holdingsAccountId, securityId));
        await using (var db = Db.NewDbContext())
        {
            var prices = await db.SecurityPrices.AsNoTracking()
                .Where(p => p.LedgerId == ledger.LedgerId && p.SecurityId == securityId)
                .Select(p => new { p.PriceDate, p.Source })
                .ToListAsync();
            Assert.Contains(prices, p => p.PriceDate == importedDay && p.Source == PriceSource.Trade);
        }
        await AssertConsistentAsync(client, ledger, "after the merge");
    }

    /// <summary>
    /// The bank merge folds a row into an investment winner too: it gates the edited row
    /// on the bank shape and the winner on being settled, visible and not merged, never on
    /// its shape. It moves the winner to the imported row's date and writes none of the
    /// winner's lines, so the save recomputes what reads that date, as for the investment
    /// merge above: the position re-sorts and the trade prices the day it now sits on
    /// (ADR-0032, 2026-11-03 amendment; ADR-0084 D4).
    /// </summary>
    /// <remarks>
    /// The position is the one above, so its order shows: the winner is the earliest
    /// purchase, a second purchase and a sale of the winner's share count follow it, and
    /// the imported row is dated after the sale.
    /// </remarks>
    [Fact]
    public async Task A_bank_merge_into_an_investment_winner_re_sorts_its_position_and_prices_its_new_day()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 17, 12, 0, 0, DateTimeKind.Utc),
            shares: 4.321m, amount: 108.47m, price: 25.102985m);
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 18, 12, 0, 0, DateTimeKind.Utc),
            shares: 10m, amount: 100m, price: 10m);
        var sale = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 1, 19, 12, 0, 0, DateTimeKind.Utc),
                Action = "sell",
                SecurityId = securityId,
                Shares = -4.321m,
                Price = 30m,
                Amount = 129.63m,
            });
        Assert.Equal(HttpStatusCode.Created, sale.StatusCode);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);
        var importedDay = await DayOfAsync(loser);

        // Premises: the sale took the winner's shares, and the ledger checks clean.
        Assert.Equal(100m, Math.Round(await CostBasisAsync(holdingsAccountId, securityId), 2));
        await AssertConsistentAsync(client, ledger, "before the merge");

        var merge = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.True(merge.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)merge.StatusCode}: {await merge.Content.ReadAsStringAsync()}");

        // Premises: the bank merge folded the row into the winner, and the winner now sits
        // on the imported row's day.
        await using (var db = Db.NewDbContext())
        {
            var loserRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
            Assert.Equal(winner, loserRow.IsMergedInto);
        }
        Assert.Equal(importedDay, await DayOfAsync(winner));

        // The sale now takes the second purchase's shares at its unit cost, and the
        // position still holds the second purchase's remainder and all the winner's shares.
        Assert.Equal(100m - 43.21m + 108.47m, await CostBasisAsync(holdingsAccountId, securityId));
        await using (var db = Db.NewDbContext())
        {
            var quantity = await db.Holdings.AsNoTracking()
                .Where(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId)
                .Select(h => h.Quantity).SingleAsync();
            Assert.Equal(10m, quantity);
            var prices = await db.SecurityPrices.AsNoTracking()
                .Where(p => p.LedgerId == ledger.LedgerId && p.SecurityId == securityId)
                .Select(p => new { p.PriceDate, p.Source })
                .ToListAsync();
            Assert.Contains(prices, p => p.PriceDate == importedDay && p.Source == PriceSource.Trade);
        }
        await AssertConsistentAsync(client, ledger, "after the merge");
    }

    // =================================================================
    // Deleting a merge winner. A manual row is a valid winner, and the
    // database un-merges its losers when it is deleted (ON DELETE SET
    // NULL): each loser is back in the register and in every walk that
    // leaves a merged row out, on accounts the deleted winner never had a
    // line on, such as a feed row's Uncategorized category.
    // =================================================================

    /// <summary>
    /// A brokerage feed row folded into the manual dividend it duplicates, then the
    /// dividend deleted: the feed row is back in the brokerage's register and the
    /// consistency check is healthy, its category's balances included.
    /// </summary>
    [Fact]
    public async Task Deleting_an_investment_merge_winner_restores_its_loser_with_every_balance()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var income = await ledger.AddCategoryAsync("Dividend Income", kind: "income");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id,
            PostedAt = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            Action = "dividend_cash",
            SecurityId = securityId,
            CategoryAccountId = income.Id,
            Amount = 42.50m,
        });
        var loser = await ImportCashDividendAsync(client, ledger, brokerage.Id);
        await MergeIntoAsync(client, ledger, loser, winner);

        var delete = await client.DeleteAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions/{winner}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Contains("hard-deleted", await delete.Content.ReadAsStringAsync());

        await AssertLoserRestoredAsync(client, ledger, brokerage.Id, winner, loser);
    }

    /// <summary>
    /// The bulk delete, which bypasses the change tracker, restores a loser that holds
    /// shares: its position takes them back.
    /// </summary>
    /// <remarks>
    /// The investment PATCH turns an imported row into a purchase while it awaits review;
    /// a merged row takes no edit (MergedAway). Merged as a purchase of another security,
    /// its shares are left out of that position, stored at 0, until the winner goes.
    /// The other security keeps the winner's own position from re-walking them by chance.
    /// </remarks>
    [Fact]
    public async Task Bulk_deleting_a_merge_winner_restores_the_shares_of_its_loser()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");
        var otherId = await ledger.AddSecurityAsync("OTHR", ticker: "OTHR");
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 4.321m, amount: 108.47m, price: 25.102985m);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = date,
                Action = "buy",
                SecurityId = otherId,
                Shares = 3m,
                Price = 10m,
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: {await patch.Content.ReadAsStringAsync()}");
        // Dated as the winner is, so the merge moves no date.
        await MergeIntoAsync(client, ledger, loser, winner);
        // Premises: merged, its shares out of their position, the ledger clean.
        await using (var db = Db.NewDbContext())
            Assert.Equal(winner, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);
        Assert.Equal(0m, await QuantityAsync(holdingsAccountId, otherId));
        await AssertConsistentAsync(client, ledger, "with the merged row re-entered");

        var delete = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-delete",
            new BulkDeleteRequest
            {
                Selection = new SelectionRequest { Kind = "explicit", AccountId = brokerage.Id, HeaderIds = [winner] },
            });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal(1, (await delete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hardDeleted").GetInt32());

        Assert.Equal(3m, await QuantityAsync(holdingsAccountId, otherId));
        await AssertLoserRestoredAsync(client, ledger, brokerage.Id, winner, loser);
    }

    /// <summary>
    /// Converting a sale and a purchase into an in-kind transfer deletes both, so a feed
    /// row folded into the sale is restored as a single delete restores it.
    /// </summary>
    [Fact]
    public async Task Converting_a_merge_winner_into_an_in_kind_transfer_restores_its_loser()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("brokerage");
        var dest = await ledger.AddInvestmentAccountAsync("other brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The pair sits on the feed row's day, so the merge leaves the sale's day as it is.
        var day = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        await BuyAsync(client, ledger, source.Id, securityId,
            new DateTime(2025, 1, 2, 12, 0, 0, DateTimeKind.Utc), shares: 10m, amount: 100m, price: 10m);
        var sell = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = source.Id, PostedAt = day, Action = "sell",
            SecurityId = securityId, Shares = -10m, Price = 15m,
        });
        var buy = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = dest.Id, PostedAt = day, Action = "buy",
            SecurityId = securityId, Shares = 10m, Price = 15m,
        });
        var loser = await ImportCashDividendAsync(client, ledger, source.Id);
        await MergeIntoAsync(client, ledger, loser, sell);

        var convert = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/in-kind-transfers/convert",
            new ConvertInKindTransferRequest { SellHeaderId = sell, BuyHeaderId = buy });
        Assert.Equal(HttpStatusCode.Created, convert.StatusCode);

        await AssertLoserRestoredAsync(client, ledger, source.Id, sell, loser);
    }

    /// <summary>
    /// A reminder's template is refused as a merge winner on both merge routes, as a
    /// hidden or merged row is, though it is settled and visible to the gate's other
    /// tests: the candidates never offer it, and a direct call must not fold a feed row
    /// into it.
    /// </summary>
    [Theory]
    [InlineData("transactions")]
    [InlineData("investment-transactions")]
    public async Task A_reminder_template_is_never_a_merge_winner(string route)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var checking = await ledger.AddBankAccountAsync("checking");
        var rent = await ledger.AddCategoryAsync("rent");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var reminder = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=20",
                StartDate = new DateOnly(2026, 1, 20),
                Payee = "Rent",
                SourceAccountId = checking.Id,
                Postings = [new TransactionPosting { CounterpartyAccountId = rent.Id, Amount = -42.50m }],
            });
        Assert.Equal(HttpStatusCode.Created, reminder.StatusCode);
        TxnHeaderRow template;
        await using (var db = Db.NewDbContext())
            template = await db.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.IsRecurringTemplate);
        // Premise: nothing else in the gate refuses it.
        Assert.False(template.NeedsReview || template.IsHidden || template.IsMergedInto is not null);
        var loser = await ImportCashDividendAsync(client, ledger, brokerage.Id);

        var merge = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/{route}/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = template.Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, merge.StatusCode);
        using (var doc = JsonDocument.Parse(await merge.Content.ReadAsStringAsync()))
            Assert.Equal("merge-source-invalid", doc.RootElement.GetProperty("code").GetString());

        // Nothing was written: the feed row still awaits review, the template is as it was.
        await using var after = Db.NewDbContext();
        var loserRow = await after.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
        Assert.Null(loserRow.IsMergedInto);
        Assert.True(loserRow.NeedsReview);
        var templateRow = await after.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == template.Id);
        Assert.False(templateRow.IsMergeWinner);
        Assert.Equal(template.PostedAt, templateRow.PostedAt);
    }

    /// <summary>Fold <paramref name="loser"/> into <paramref name="winner"/>; the ledger
    /// checks clean before and after.</summary>
    private async Task MergeIntoAsync(HttpClient client, SyntheticLedger ledger, Guid loser, Guid winner)
    {
        await AssertConsistentAsync(client, ledger, "before the merge");
        var merge = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.True(merge.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"merge failed: {(int)merge.StatusCode} {await merge.Content.ReadAsStringAsync()}");
        await using (var db = Db.NewDbContext())
            Assert.Equal(winner, (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);
        await AssertConsistentAsync(client, ledger, "after the merge");
    }

    /// <summary>The winner is gone, the loser merged into nothing and listed on
    /// <paramref name="accountId"/>'s register, and the whole consistency check healthy.</summary>
    private async Task AssertLoserRestoredAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, Guid winner, Guid loser)
    {
        await using (var db = Db.NewDbContext())
        {
            Assert.False(await db.TxnHeaders.AsNoTracking().AnyAsync(h => h.Id == winner));
            Assert.Null((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser)).IsMergedInto);
        }
        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountId}&limit=100"))!;
        Assert.Single(page.Entries, e => e.Txn is not null && e.Txn.HeaderId == loser);
        await AssertConsistentAsync(client, ledger, "after the winner was deleted");
    }

    private async Task<decimal> QuantityAsync(Guid holdingsAccountId, Guid securityId)
    {
        await using var db = Db.NewDbContext();
        return await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId)
            .Select(h => h.Quantity).SingleAsync();
    }

    private async Task<DateOnly> DayOfAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        var postedAt = await db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == headerId).Select(h => h.PostedAt).SingleAsync();
        return DateOnly.FromDateTime(postedAt.ToUniversalTime());
    }

    private async Task<decimal> CostBasisAsync(Guid holdingsAccountId, Guid securityId)
    {
        await using var db = Db.NewDbContext();
        return await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId)
            .Select(h => h.CostBasis).SingleAsync();
    }

    /// <summary>The consistency check, through its endpoint, reports every projection
    /// healthy; otherwise the failure names each disagreeing figure.</summary>
    private static async Task AssertConsistentAsync(HttpClient client, SyntheticLedger ledger, string when)
    {
        using var doc = JsonDocument.Parse(await (await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/balances/consistency", null)).EnsureSuccessStatusCode()
            .Content.ReadAsStringAsync());
        var report = doc.RootElement;
        var failing = report.GetProperty("projections").EnumerateArray()
            .Where(p => !p.GetProperty("healthy").GetBoolean())
            .Select(p => p.GetProperty("projection").GetString() + ": "
                         + string.Join("; ", p.GetProperty("mismatches").EnumerateArray().Select(m =>
                             $"{m.GetProperty("scope").GetString()} {m.GetProperty("field").GetString()}"
                             + $" stored {m.GetProperty("stored").GetDecimal()} expected {m.GetProperty("expected").GetDecimal()}")))
            .ToList();
        Assert.True(report.GetProperty("healthy").GetBoolean(),
            $"{when}, the consistency check is unhealthy: "
            + (failing.Count > 0 ? string.Join(" | ", failing) : "oversold positions listed"));
    }

    // `Candidate_matches_on_its_override_amount_not_its_raw_amount` lived here,
    // with a helper that inserted a txn_leg_overrides row. Both are gone with
    // migration 230, and the honest reason is worth recording: the test was
    // justified by a claim that "the Moneydance importer writes leg overrides",
    // which was false — that importer only ANALYZEs the table. Nothing in the
    // repository has ever written one, so no code path can produce one, and the
    // fixture was manufacturing a state the app could not reach. The
    // candidate-amount behaviour it meant to pin is covered by
    // Candidate_amount_is_the_figure_the_register_shows above, on rows the app
    // itself writes.

    /// <summary>
    /// Merging the last unreviewed row clears the account's review count, which
    /// is what lights the sidebar dot.
    /// </summary>
    /// <remarks>
    /// The merge clears the folded-away row's <c>needs_review</c> as it stamps
    /// <c>is_merged_into</c>, and the count reads the register's needs-review view,
    /// which leaves merged rows out besides; either way a folded-away duplicate must
    /// not keep the mark lit with nothing left in the register to review.
    ///
    /// Asserted through the accounts endpoint rather than the repository,
    /// because the endpoint's number is the one the sidebar renders.
    /// </remarks>
    [Fact]
    public async Task Merging_the_last_unreviewed_row_clears_the_accounts_review_count()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 4.321m, amount: 108.47m, price: 25.102985m);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        // The dot is lit: one imported row awaits review.
        Assert.Equal(1, await ReviewCountAsync(client, ledger, brokerage.Id));

        var patch = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        // ...and goes out, because nothing is left to review.
        Assert.Equal(0, await ReviewCountAsync(client, ledger, brokerage.Id));
    }

    /// <summary>The review count the sidebar dot reads, for one account.</summary>
    private static async Task<int> ReviewCountAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId)
    {
        var accounts = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/ledgers/{ledger.LedgerId}/accounts");
        return accounts!
            .Single(a => a.GetProperty("id").GetGuid() == accountId)
            .GetProperty("needsReviewCount").GetInt32();
    }

    /// <summary>
    /// Import the reinvest statement and return the header it created.
    /// </summary>
    /// <summary>
    /// A CLASSIFIED row whose cash moved the OTHER way is not a candidate.
    /// </summary>
    /// <remarks>
    /// <para>A sell that brought in a sum and a buy that spent the same sum on the
    /// same security, the same day, are the same magnitude and opposite events.
    /// Offering one as the other's twin invites a merge that tombstones a real
    /// transaction and silently halves the position.</para>
    ///
    /// <para>The bank rule compares the SIGNED sum on the source account
    /// (<c>g.Sum(...) == targetSourceAmount</c>). The investment query compared
    /// magnitudes — first on the security leg with <c>Math.Abs</c>, then, after
    /// the widening meant to bring it to bank parity, on the brokerage leg with
    /// the same absolute comparison. The result: a feed-delivered sell was
    /// offered the settled buy that closed the same lot.</para>
    ///
    /// <para>The guard holds where the row's direction is a classification's: an
    /// imported SELL carries an ingest action hint. An UNCLASSIFIED row has only
    /// the feed's sign, which is not a direction; see
    /// <see cref="An_unclassified_rows_opposite_direction_twin_is_offered_last"/>.</para>
    /// </remarks>
    [Fact]
    public async Task A_classified_row_whose_cash_moved_the_other_way_is_not_a_candidate()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Settled BUY of the same magnitude: brokerage cash goes OUT.
        var buy = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 1m, amount: 42.50m, price: 42.50m);

        // The anchor: an imported SELL — classified, cash comes IN.
        var loser = await ImportSellAsync(client, ledger, brokerage.Id);

        await using (var db = Db.NewDbContext())
        {
            // Premises: it is classified, and its cash really is inbound.
            var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
            Assert.NotNull(header.IngestActionHint);
            Assert.True(header.NeedsReview);
            var cash = await db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == loser && l.AccountId == brokerage.Id)
                .SumAsync(l => l.Amount);
            Assert.Equal(42.50m, cash);
        }

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        Assert.DoesNotContain(candidates!, c => c.HeaderId == buy);
    }

    /// <summary>
    /// An UNCLASSIFIED row's opposite-direction twin is offered, after the
    /// same-direction ones.
    /// </summary>
    /// <remarks>
    /// <para>A feed row with no action and no ingest action hint has only the
    /// feed's sign, and an aggregator that cannot tell a fund purchase from a sale
    /// reports the cash with its own convention: a buy can arrive as money IN. The
    /// direction guard then hid the row's true twin, so the panel was empty on the
    /// row it exists for. The merge is the person's pick and the chip shows the
    /// twin's own direction, so it is offered, but listed after any twin that
    /// agrees with the feed.</para>
    /// </remarks>
    [Fact]
    public async Task An_unclassified_rows_opposite_direction_twin_is_offered_last()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var income = await ledger.AddCategoryAsync("Dividend Income", kind: "income");
        var securityId = await ledger.AddSecurityAsync("SIGN", ticker: "SIGN");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);

        // Settled BUY: brokerage cash goes OUT (negative).
        var buy = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 1m, amount: 42.50m, price: 42.50m);

        // Settled dividend: brokerage cash comes IN (positive). Same magnitude,
        // same window, same security — the control that proves the filter is
        // about DIRECTION and not just "fewer candidates".
        var inbound = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = date,
                Action = "dividend_cash",
                SecurityId = securityId,
                CategoryAccountId = income.Id,
                Amount = 42.50m,
            });
        Assert.Equal(HttpStatusCode.Created, inbound.StatusCode);
        var inboundId = (await inbound.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        // The anchor: a feed row whose cash also comes IN, at that same magnitude.
        var loser = await ImportCashDividendAsync(client, ledger, brokerage.Id);

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        // Premise: the anchor is unclassified, so its sign is only the feed's.
        await using (var db = Db.NewDbContext())
        {
            var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
            Assert.Null(header.Action);
            Assert.Null(header.IngestActionHint);
        }

        // Both are offered, the one that agrees with the feed first.
        var ids = candidates!.Select(c => c.HeaderId).ToList();
        Assert.Equal(new[] { inboundId, buy }, ids);
    }

    /// <summary>
    /// A settled row with no SECURITY leg is still a merge candidate.
    /// </summary>
    /// <remarks>
    /// <para>The bank candidate rule constrains settledness and nothing else: any
    /// accepted, un-merged, visible row on the same account, same effective
    /// amount, within +/-7 days. The investment rule added a SHAPE constraint on
    /// top — a candidate had to carry a security leg on the Holdings sibling,
    /// with a non-null quantity and action.</para>
    ///
    /// <para>So the commonest duplicate on a brokerage could not be offered. A
    /// dividend the user already recorded as cash has an income leg and a
    /// brokerage cash leg and no holdings leg at all; the same dividend arriving
    /// again from the broker's feed found nothing to fold into, and the merge
    /// panel stayed empty on exactly the row it exists for.</para>
    /// </remarks>
    [Fact]
    public async Task A_settled_cash_dividend_is_offered_as_a_candidate()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var income = await ledger.AddCategoryAsync("Dividend Income", kind: "income");
        var securityId = await ledger.AddSecurityAsync("DIVX", ticker: "DIVX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The settled twin: a dividend already recorded as CASH. The action x
        // field matrix requires a security — it names what PAID the dividend —
        // but the event holds no position, so there is no holdings leg and no
        // quantity. That is the shape a rule keyed on a holdings leg cannot see.
        var settled = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
                Action = "dividend_cash",
                SecurityId = securityId,
                CategoryAccountId = income.Id,
                Amount = 108.47m,
            });
        Assert.Equal(HttpStatusCode.Created, settled.StatusCode);
        var settledId = (await settled.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        // Premise: it really has no holdings/security leg to be matched on.
        await using (var db = Db.NewDbContext())
        {
            var hasSecurityLeg = await db.TxnLegs.AsNoTracking()
                .AnyAsync(l => l.HeaderId == settledId && l.Quantity != null);
            Assert.False(hasSecurityLeg,
                "fixture drifted: this candidate is supposed to be cash-shape.");
        }

        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        var only = Assert.Single(candidates!);
        Assert.Equal(settledId, only.HeaderId);
        // The chip has no ticker or share count to show, and says so with nulls
        // rather than being dropped.
        Assert.Null(only.SecurityTicker);
        Assert.Null(only.Shares);
    }

    /// <summary>
    /// A feed-delivered brokerage CASH row gets a merge panel of its own.
    /// </summary>
    /// <remarks>
    /// The other side of the same gap. An <c>INVBANKTRAN</c> — a cash movement in
    /// the brokerage's cash sub-account — imports as a bank-shape row: no action,
    /// no security, no holdings leg. It anchors fine (the brokerage owns a
    /// Holdings sibling, which is what the anchor keys on), and then every
    /// candidate was filtered out by the shape rule, so the row that most needs
    /// deduping was the one that could never be deduped.
    /// </remarks>
    [Fact]
    public async Task A_feed_delivered_brokerage_cash_row_is_offered_its_twin()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var income = await ledger.AddCategoryAsync("Dividend Income", kind: "income");
        var securityId = await ledger.AddSecurityAsync("CASHX", ticker: "CASHX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var settled = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
                Action = "dividend_cash",
                SecurityId = securityId,
                CategoryAccountId = income.Id,
                Amount = 42.50m,
            });
        Assert.Equal(HttpStatusCode.Created, settled.StatusCode);
        var settledId = (await settled.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();

        var loser = await ImportCashDividendAsync(client, ledger, brokerage.Id);

        // Premise: the imported row is bank-shape and awaiting review.
        await using (var db = Db.NewDbContext())
        {
            var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
            Assert.Null(header.Action);
            Assert.True(header.NeedsReview);
        }

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        var only = Assert.Single(candidates!);
        Assert.Equal(settledId, only.HeaderId);
    }

    /// <summary>
    /// A candidate imported earlier and already accepted names its origin, so the
    /// chip's tooltip says the merge keeps an imported row rather than the
    /// person's own entry.
    /// </summary>
    [Fact]
    public async Task An_accepted_imported_candidate_names_its_origin()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The same cash row, delivered by two files: the first is accepted, and
        // the second arrives awaiting review with the first as its twin.
        var accepted = await ImportCashDividendAsync(client, ledger, brokerage.Id);
        var approve = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{accepted}/approve", null);
        Assert.True(approve.IsSuccessStatusCode, $"approve answered {approve.StatusCode}");
        var loser = await ImportCashDividendAsync(client, ledger, brokerage.Id, fitId: "INV-FITID-CASHDIV-2");

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        var only = Assert.Single(candidates!);
        Assert.Equal(accepted, only.HeaderId);
        Assert.Equal("file_import", only.Origin);
    }

    /// <summary>
    /// A candidate reads like its row in the edited brokerage's register: its
    /// amount, shares, derived action and other account are the row's.
    /// </summary>
    /// <remarks>
    /// <para>The chip stands for a register row, so a figure that differs from
    /// the row's makes the chip unrecognisable. Reading the legs directly gives
    /// the holdings leg's view instead: a sell's chip comes out negative where
    /// its row is cash coming in, a buy's positive where its row is cash going
    /// out, a fee's row includes the fee the principal leaves out, and a cash
    /// transfer has no action at all because its <c>Xfr</c> is derived for the
    /// register only.</para>
    /// <para>Each candidate is compared with the row the register endpoint itself
    /// returns for that header in that brokerage, which is what the person sees.
    /// The anchor is an imported reinvest: it moves no cash, so no direction rule
    /// excludes either sign, and its share count and total each match a shape.</para>
    /// </remarks>
    [Theory]
    [InlineData("sell")]
    [InlineData("buy")]
    [InlineData("dividend")]
    [InlineData("cash_transfer")]
    [InlineData("buy_with_fee")]
    public async Task Candidate_reads_like_its_register_row(string shape)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var checking = await ledger.AddBankAccountAsync("checking");
        var income = await ledger.AddCategoryAsync("Dividend Income", kind: "income");
        var fees = await ledger.AddCategoryAsync("Brokerage Fees");
        var securityId = await ledger.AddSecurityAsync("ROWX", ticker: "ROWX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        // The anchor's share count and total (see OfxReinvestStatement).
        const decimal anchorShares = 4.321m;
        const decimal anchorTotal = 108.47m;

        Guid candidateId;
        switch (shape)
        {
            case "sell":
                // A position to sell from, well outside the window and matching
                // nothing.
                await BuyAsync(client, ledger, brokerage.Id, securityId,
                    date.AddDays(-40), shares: 10m, amount: 250m, price: 25m);
                candidateId = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    PostedAt = date,
                    Action = "sell",
                    SecurityId = securityId,
                    Shares = -anchorShares,
                    Price = 26m,
                    Amount = 112.35m,
                });
                break;
            case "buy":
                candidateId = await BuyAsync(client, ledger, brokerage.Id, securityId,
                    date, shares: anchorShares, amount: 112.35m, price: 26m);
                break;
            case "dividend":
                candidateId = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    PostedAt = date,
                    Action = "dividend_cash",
                    SecurityId = securityId,
                    CategoryAccountId = income.Id,
                    Amount = anchorTotal,
                });
                break;
            case "cash_transfer":
            {
                // Entered from the bank side: an action-less header whose
                // brokerage leg sits opposite an asset account.
                var resp = await client.PostAsJsonAsync(
                    $"/api/ledgers/{ledger.LedgerId}/transactions",
                    new CreateTransactionRequest
                    {
                        PostedAt = date,
                        Payee = "Funding",
                        SourceAccountId = checking.Id,
                        Postings = [new TransactionPosting { CounterpartyAccountId = brokerage.Id, Amount = -anchorTotal }],
                    });
                Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
                candidateId = (await resp.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("headerId").GetGuid();
                break;
            }
            case "buy_with_fee":
                candidateId = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    PostedAt = date,
                    Action = "buy",
                    SecurityId = securityId,
                    Shares = anchorShares,
                    Price = 26m,
                    Amount = 112.35m,
                    FeeAccountId = fees.Id,
                    FeeAmount = 4.95m,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");
        var candidate = Assert.Single(candidates!);
        Assert.Equal(candidateId, candidate.HeaderId);

        var page = await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={brokerage.Id}&starting_at={candidateId}&limit=1");
        var entry = Assert.Single(page!.Entries);
        var row = Assert.IsType<InvestmentRowDto>(entry.Txn);
        Assert.Equal(candidateId, row.HeaderId);

        // What makes each shape a case: the row reads differently from the legs.
        switch (shape)
        {
            case "sell":
                Assert.True(row.Amount > 0m, $"a sell's row is cash coming in; got {row.Amount}");
                break;
            case "buy":
                Assert.True(row.Amount < 0m, $"a buy's row is cash going out; got {row.Amount}");
                break;
            case "cash_transfer":
                Assert.Equal("Xfr", row.DerivedAction);
                Assert.Equal(checking.Id, row.CounterpartyAccountId);
                break;
            case "buy_with_fee":
                Assert.NotNull(row.FeeAmount);
                break;
        }

        Assert.Equal(row.Amount, candidate.Amount);
        Assert.Equal(row.Quantity, candidate.Shares);
        Assert.Equal(row.DerivedAction, candidate.DerivedAction);
        Assert.Equal(row.CounterpartyAccountId, candidate.CounterpartyAccountId);
        Assert.Equal(row.CounterpartyAccountName, candidate.CounterpartyAccountName);
    }

    /// <summary>
    /// A feed row for cash leaving the brokerage is offered the settled dividend
    /// reinvest it pairs with, though that reinvest moves no cash on the account.
    /// </summary>
    /// <remarks>
    /// A reinvest books income in and a purchase out on the brokerage, so its cash
    /// sums to zero and a match on the cash leg cannot see it. Its principal is on
    /// the holdings leg, so the rule has to read that leg too.
    /// </remarks>
    [Fact]
    public async Task A_settled_dividend_reinvest_is_offered_for_a_cash_row_of_its_principal()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var income = await ledger.AddCategoryAsync("Dividend Income", kind: "income");
        var securityId = await ledger.AddSecurityAsync("MMKT", ticker: "MMKT");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        var reinvest = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id,
            PostedAt = date,
            Action = "dividend_reinvest",
            SecurityId = securityId,
            CategoryAccountId = income.Id,
            Shares = 312.40m,
            Price = 1m,
            Amount = 312.40m,
        });
        // Decoys: a reinvest of a different principal, and the same principal
        // outside the window.
        await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id,
            PostedAt = date,
            Action = "dividend_reinvest",
            SecurityId = securityId,
            CategoryAccountId = income.Id,
            Shares = 80m,
            Price = 1m,
            Amount = 80m,
        });
        await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id,
            PostedAt = date.AddDays(20),
            Action = "dividend_reinvest",
            SecurityId = securityId,
            CategoryAccountId = income.Id,
            Shares = 312.40m,
            Price = 1m,
            Amount = 312.40m,
        });

        var loser = await ImportCashOutAsync(client, ledger, brokerage.Id);

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        var only = Assert.Single(candidates!);
        Assert.Equal(reinvest, only.HeaderId);
    }

    /// <summary>The cash-out feed row of the test above, on the statement's date.</summary>
    private static async Task<Guid> ImportCashOutAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId)
    {
        var statement = OfxCashDividendStatement
            .Replace("<TRNTYPE>CREDIT", "<TRNTYPE>DEBIT")
            .Replace("<TRNAMT>42.50", "<TRNAMT>-312.40")
            .Replace("INV-FITID-CASHDIV", "INV-FITID-CASHOUT");
        var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(statement));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(body, "file", "statement.qfx");
        content.Add(new StringContent(brokerageId.ToString()), "accountId");
        content.Add(new StringContent("inv:brokerX:INV-0001"), "providerAccountId");

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = ledger.NewDbContext();
        return (await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId
                && h.ExternalId == "INV-FITID-CASHOUT")).Id;
    }

    private static async Task<Guid> CreateInvestmentAsync(
        HttpClient client, SyntheticLedger ledger, CreateInvestmentTransactionRequest request)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions", request);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();
    }

    /// <summary>An imported SELL: classified (it carries an action hint), cash IN.</summary>
    private static async Task<Guid> ImportSellAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId)
    {
        var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(OfxSellStatement));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(body, "file", "statement.qfx");
        content.Add(new StringContent(brokerageId.ToString()), "accountId");
        content.Add(new StringContent("inv:brokerX:INV-0001"), "providerAccountId");

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = ledger.NewDbContext();
        return (await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId
                && h.ExternalId == "INV-FITID-MERGE-SELL")).Id;
    }

    /// <param name="total">The statement's TOTAL as written, its own by default.</param>
    private static async Task<Guid> ImportReinvestAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, string? total = null)
    {
        var content = new MultipartFormDataContent();
        var statement = total is null
            ? OfxReinvestStatement
            : OfxReinvestStatement.Replace("<TOTAL>-108.47", "<TOTAL>" + total, StringComparison.Ordinal);
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(statement));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(body, "file", "statement.qfx");
        content.Add(new StringContent(brokerageId.ToString()), "accountId");
        content.Add(new StringContent("inv:brokerX:INV-0001"), "providerAccountId");

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = ledger.NewDbContext();
        return (await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId
                && h.ExternalId == "INV-FITID-MERGE-REINV")).Id;
    }

    /// <summary>
    /// Record the provider→security mapping the importer resolves the ticker hint
    /// through, so ingest_security_id comes back non-NULL.
    /// </summary>
    private async Task MapProviderSecurityAsync(
        SyntheticLedger ledger, string providerSecurityId, Guid securityId)
    {
        await using var db = Db.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO provider_security_mappings
                (id, ledger_id, provider_key, provider_security_id, security_id)
            VALUES (gen_random_uuid(), {ledger.LedgerId}, 'ofx',
                    {providerSecurityId}, {securityId})
            """);
    }

    /// <summary>
    /// A REINVEST whose TOTAL is the only usable number: the cash movement is zero
    /// and UNITS x UNITPRICE rounds to a cent short of the TOTAL the statement settles.
    /// </summary>
    /// <summary>
    /// Import an <c>INVBANKTRAN</c> — a cash movement inside an investment
    /// statement. Routes through the BANK-shape mapper, so the row lands with no
    /// action and no security: the feed-delivered brokerage cash row.
    /// </summary>
    private static async Task<Guid> ImportCashDividendAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, string fitId = "INV-FITID-CASHDIV")
    {
        var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(
            OfxCashDividendStatement.Replace("INV-FITID-CASHDIV", fitId, StringComparison.Ordinal)));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-ofx");
        content.Add(body, "file", "statement.qfx");
        content.Add(new StringContent(brokerageId.ToString()), "accountId");
        content.Add(new StringContent("inv:brokerX:INV-0001"), "providerAccountId");

        var resp = await client.PostAsync(
            $"/api/ledgers/{ledger.LedgerId}/ingest/ofx/import", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = ledger.NewDbContext();
        return (await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.LedgerId == ledger.LedgerId
                && h.ExternalId == fitId)).Id;
    }

    /// <summary>
    /// A cash dividend delivered as INVBANKTRAN rather than INCOME — the same
    /// money, in the shape a feed uses when it does not classify the event.
    /// </summary>
    private const string OfxCashDividendStatement = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE

        <OFX>
        <SIGNONMSGSRSV1>
        <SONRS>
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <DTSERVER>20260201120000
        <LANGUAGE>ENG
        </SONRS>
        </SIGNONMSGSRSV1>
        <INVSTMTMSGSRSV1>
        <INVSTMTTRNRS>
        <TRNUID>0
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <INVSTMTRS>
        <DTASOF>20260131120000
        <CURDEF>USD
        <INVACCTFROM>
        <BROKERID>brokerX
        <ACCTID>INV-0001
        </INVACCTFROM>
        <INVTRANLIST>
        <DTSTART>20260101
        <DTEND>20260131
        <INVBANKTRAN>
        <STMTTRN>
        <TRNTYPE>CREDIT
        <DTPOSTED>20260120
        <TRNAMT>42.50
        <FITID>INV-FITID-CASHDIV
        <NAME>DIVIDEND RECEIVED
        </STMTTRN>
        <SUBACCTFUND>CASH
        </INVBANKTRAN>
        </INVTRANLIST>
        </INVSTMTRS>
        </INVSTMTTRNRS>
        </INVSTMTMSGSRSV1>
        </OFX>
        """;

    private const string OfxReinvestStatement = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE

        <OFX>
        <SIGNONMSGSRSV1>
        <SONRS>
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <DTSERVER>20260201120000
        <LANGUAGE>ENG
        </SONRS>
        </SIGNONMSGSRSV1>
        <INVSTMTMSGSRSV1>
        <INVSTMTTRNRS>
        <TRNUID>0
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <INVSTMTRS>
        <DTASOF>20260131120000
        <CURDEF>USD
        <INVACCTFROM>
        <BROKERID>brokerX
        <ACCTID>INV-0001
        </INVACCTFROM>
        <INVTRANLIST>
        <DTSTART>20260101
        <DTEND>20260131
        <REINVEST>
        <INVTRAN>
        <FITID>INV-FITID-MERGE-REINV
        <DTTRADE>20260120
        </INVTRAN>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <INCOMETYPE>DIV
        <TOTAL>-108.47
        <SUBACCTSEC>CASH
        <UNITS>4.321
        <UNITPRICE>25.10
        </REINVEST>
        </INVTRANLIST>
        </INVSTMTRS>
        </INVSTMTTRNRS>
        </INVSTMTMSGSRSV1>
        <SECLISTMSGSRSV1>
        <SECLIST>
        <STOCKINFO>
        <SECINFO>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <SECNAME>Fake Test Stock
        <TICKER>FAKE
        </SECINFO>
        </STOCKINFO>
        </SECLIST>
        </SECLISTMSGSRSV1>
        </OFX>
        """;

    /// <summary>A one-unit SELL on the reinvest statement's date; its total is the
    /// magnitude of the settled buy in the direction test.</summary>
    private const string OfxSellStatement = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        SECURITY:NONE
        ENCODING:USASCII
        CHARSET:1252
        COMPRESSION:NONE
        OLDFILEUID:NONE
        NEWFILEUID:NONE

        <OFX>
        <SIGNONMSGSRSV1>
        <SONRS>
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <DTSERVER>20260201120000
        <LANGUAGE>ENG
        </SONRS>
        </SIGNONMSGSRSV1>
        <INVSTMTMSGSRSV1>
        <INVSTMTTRNRS>
        <TRNUID>0
        <STATUS><CODE>0<SEVERITY>INFO</STATUS>
        <INVSTMTRS>
        <DTASOF>20260131120000
        <CURDEF>USD
        <INVACCTFROM>
        <BROKERID>brokerX
        <ACCTID>INV-0001
        </INVACCTFROM>
        <INVTRANLIST>
        <DTSTART>20260101
        <DTEND>20260131
        <SELLSTOCK>
        <INVSELL>
        <INVTRAN>
        <FITID>INV-FITID-MERGE-SELL
        <DTTRADE>20260120
        </INVTRAN>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <UNITS>1
        <UNITPRICE>42.50
        <TOTAL>42.50
        <SUBACCTSEC>CASH
        <SUBACCTFUND>CASH
        </INVSELL>
        <SELLTYPE>SELL
        </SELLSTOCK>
        </INVTRANLIST>
        </INVSTMTRS>
        </INVSTMTTRNRS>
        </INVSTMTMSGSRSV1>
        <SECLISTMSGSRSV1>
        <SECLIST>
        <STOCKINFO>
        <SECINFO>
        <SECID>
        <UNIQUEID>FAKE0001
        <UNIQUEIDTYPE>CUSIP
        </SECID>
        <SECNAME>Fake Test Stock
        <TICKER>FAKE
        </SECINFO>
        </STOCKINFO>
        </SECLIST>
        </SECLISTMSGSRSV1>
        </OFX>
        """;

}
