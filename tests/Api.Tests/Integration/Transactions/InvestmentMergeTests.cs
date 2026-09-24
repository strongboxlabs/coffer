using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
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
/// migration 163 (recompute excludes is_merged_into) + the merge branch's
/// explicit recompute trigger. Atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvestmentMergeTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentMergeTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
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
    /// <see cref="IngestOrchestrator"/>), because trg_validate_posting_role ties
    /// posting_role to header.action and the investment shape therefore only
    /// appears at Accept, which clears needs_review in the same transaction.
    ///
    /// It survives in exactly TWO tests, both of which need a loser that actually
    /// holds shares so that "the loser's shares drop out of holdings" and "its lot
    /// is not consumable" are observable at all. Those guard a DEFENSIVE path:
    /// a real merge loser is bank-shaped and contributes no holdings, so nothing
    /// reachable exercises them today. They are worth keeping as a guard on
    /// migration 163 — but they are not evidence that merge works.
    ///
    /// Everything else in this file now builds its loser with the real OFX
    /// importer. That is not a style preference. An anchor written against this
    /// fixture is exactly how the candidates query shipped unreachable and stayed
    /// green through five passing tests: raw SQL can write a state no code path
    /// produces, and a test over that state proves nothing about the product.
    /// Before adding another caller here, name the shipping code path that writes
    /// the state you want. If there is none, that is a finding about the product.
    /// </remarks>
    private async Task MarkNeedsReviewAsync(Guid headerId)
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE txn_headers SET needs_review = TRUE WHERE id = {headerId}");
    }

    [Fact]
    public async Task Merge_folds_loser_into_winner_and_drops_its_shares_from_holdings()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 97.301m, amount: 1293.13m, price: 13.29m);
        var loser = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 97.374m, amount: 1293.13m, price: 13.28m);
        await MarkNeedsReviewAsync(loser);

        // Both buys are in holdings before the merge (97.301 + 97.374).
        await using (var db0 = _fixture.NewDbContext())
        {
            var qty0 = await db0.Holdings.AsNoTracking()
                .Where(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId)
                .Select(h => h.Quantity).SingleAsync();
            Assert.Equal(97.301m + 97.374m, qty0);
        }

        // Fold the loser into the winner (merge-only PATCH; account_id → survivor entry).
        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}?account_id={brokerage.Id}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = winner });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        // Loser → merged into winner; winner → merge winner.
        var loserRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
        var winnerRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == winner);
        Assert.Equal(winner, loserRow.IsMergedInto);
        Assert.True(winnerRow.IsMergeWinner);

        // Holdings now reflect ONLY the winner's shares — the merged loser dropped out
        // (mig 163 recompute + the merge branch's explicit trigger).
        var qty = await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId)
            .Select(h => h.Quantity).SingleAsync();
        Assert.Equal(97.301m, qty);
    }

    [Fact]
    public async Task Merge_rejects_self_and_settled_editor_with_422()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TDLM", ticker: "TDLM");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 6.584m, amount: 316.37m, price: 48.051337m);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        // Self-merge: rejected.
        var self = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = loser });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        using (var doc = JsonDocument.Parse(await self.Content.ReadAsStringAsync()))
            Assert.Equal("merge-source-invalid", doc.RootElement.GetProperty("code").GetString());

        // Editor is a SETTLED row (winner, not needs_review) → can't be a loser.
        var settledEditor = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{winner}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = loser });
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("LOTX", ticker: "LOTX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Holdings and realized gains key on the HOLDINGS account, not the
        // brokerage cash sleeve the transaction is posted against.
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        var buyDate = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc);
        // Winner: 100 sh for 1000 → unit cost 10.
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            buyDate, shares: 100m, amount: 1000m, price: 10m);
        // Loser: 50 sh for the SAME 1000 → unit cost 20. Same principal keeps it
        // a candidate; the different unit cost is the detector.
        var loser = await BuyAsync(client, ledger, brokerage.Id, securityId,
            buyDate, shares: 50m, amount: 1000m, price: 20m);
        await MarkNeedsReviewAsync(loser);

        var merge = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}?account_id={brokerage.Id}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = winner });
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);

        // DIRECT PROBE: no open lot may remain tied to a leg of the merged loser.
        await using (var db = _fixture.NewDbContext())
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
        // the basis is its 1000 and the gain is 3000 - 1000. If the loser's lot
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

        await using (var db = _fixture.NewDbContext())
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 6.584m, amount: 316.37m, price: 48.051337m);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        await using (var before = _fixture.NewDbContext())
        {
            // Control: nothing is reconciling until the merge says so.
            var pre = await before.ResolvedTransactions.AsNoTracking()
                .Where(rt => rt.HeaderId == winner && rt.AccountId == brokerage.Id)
                .Select(rt => rt.Status).FirstAsync();
            Assert.NotEqual("reconciling", pre);
        }

        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}?account_id={brokerage.Id}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = winner });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
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
    // only sequence a person can perform. The candidates query used to
    // demand a security leg and a non-null action from the edited row —
    // neither of which ingest writes, and neither of which it is allowed
    // to write (trg_validate_posting_role, mig 057) — so it returned an
    // empty list for every real row while five fixture-built tests
    // reported it working.
    // =================================================================

    /// <summary>
    /// An OFX REINVEST lands unreviewed, bank-shaped: no action, no security leg,
    /// and a cash movement of exactly 0.00 because no cash moves. Its settled
    /// twin must still be offered.
    /// </summary>
    /// <remarks>
    /// This is the case that proves the anchor cannot read legs. The row's own
    /// amount is 0.00, so the ONLY number that can match it is ingest_amount —
    /// the wire's TOTAL, carried since migration 228. Before that column existed
    /// there was nothing on a reinvest to match on at all.
    /// </remarks>
    [Fact]
    public async Task Imported_reinvest_is_offered_its_settled_twin()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        // The settled twin: same principal as the statement's TOTAL, but a
        // DIFFERENT share count. Feeds disagree on share rounding, so the
        // principal is the stable half and must be what matches.
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 6.612m, amount: 316.37m, price: 47.8508m);
        // Decoy: same security and date, different principal.
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 10m, amount: 500.00m, price: 50m);
        // Decoy: same principal, 20 days out of the ±7d window.
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            date.AddDays(20), shares: 6.6m, amount: 316.37m, price: 47.93m);

        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        var only = Assert.Single(candidates!);
        Assert.Equal(winner, only.HeaderId);
        Assert.Equal(316.37m, only.Amount);
        // Matched on principal, not shares: the statement says 6.584.
        Assert.Equal(6.612m, only.Shares);
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var headerId = await ImportReinvestAsync(client, ledger, brokerage.Id);

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == headerId);
        Assert.Null(header.Action);
        Assert.True(header.NeedsReview);
        // The detail lives in the carriers instead.
        Assert.Equal(316.37m, header.IngestAmount);
        Assert.Equal(6.584m, header.IngestShares);

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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var otherSecurity = await ledger.AddSecurityAsync("OTHR", ticker: "OTHR");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, otherSecurity,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 6.584m, amount: 316.37m, price: 48.051337m);

        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        await using (var db = _fixture.NewDbContext())
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var mapped = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");
        var otherSecurity = await ledger.AddSecurityAsync("OTHR", ticker: "OTHR");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc);
        var right = await BuyAsync(client, ledger, brokerage.Id, mapped,
            date, shares: 6.584m, amount: 316.37m, price: 48.051337m);
        // Same magnitude, same window, wrong security — must be excluded now.
        await BuyAsync(client, ledger, brokerage.Id, otherSecurity,
            date, shares: 6.584m, amount: 316.37m, price: 48.051337m);

        // The statement's SECLIST resolves UNIQUEID FAKE0001 to ticker FAKE, so
        // this is the mapping the importer looks up.
        await MapProviderSecurityAsync(ledger, "FAKE", mapped);

        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        await using (var db = _fixture.NewDbContext())
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 6.584m, amount: 316.37m, price: 48.051337m);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = winner });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        await using var db = _fixture.NewDbContext();
        var loserRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
        var winnerRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == winner);
        Assert.Equal(winner, loserRow.IsMergedInto);
        Assert.True(winnerRow.IsMergeWinner);
        Assert.Null(winnerRow.IsMergedInto);
        // Same end state the BANK merge reaches. Its editor pairs the stamp with
        // approve: true "to keep state coherent if the row is ever surfaced
        // again"; the investment branch takes a merge-only patch and returned
        // before any approve handling, so its losers alone stayed flagged as
        // awaiting review — which is what kept the sidebar dot lit.
        Assert.False(loserRow.NeedsReview);
    }

    // `Candidate_matches_on_its_override_amount_not_its_raw_amount` lived here,
    // with a helper that inserted a txn_leg_overrides row. Both are gone with
    // migration 230, and the honest reason is worth recording: the test was
    // justified by a claim that "the Moneydance importer writes leg overrides",
    // which was false — that importer only ANALYZEs the table. Nothing in the
    // repository has ever written one, every database holds zero, and the
    // fixture was manufacturing a state the app could not reach. The
    // candidate-amount behaviour it meant to pin is covered by
    // Candidate_amount_is_the_figure_the_register_shows above, on real data.

    /// <summary>
    /// Merging the last unreviewed row clears the account's review count, which
    /// is what lights the sidebar dot.
    /// </summary>
    /// <remarks>
    /// A merge stamps <c>is_merged_into</c> and deliberately leaves
    /// <c>needs_review</c> alone — the flag records how the row arrived, and the
    /// register hides merged rows by other means. But the accounts endpoint
    /// counted every unreviewed, unhidden row with no merged guard, so a
    /// folded-away duplicate kept the dot lit forever: the register showed
    /// nothing to review and the sidebar insisted there was something.
    ///
    /// Asserted through the accounts endpoint rather than the repository,
    /// because the endpoint's number is the one the sidebar renders.
    /// </remarks>
    [Fact]
    public async Task Merging_the_last_unreviewed_row_clears_the_accounts_review_count()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FAKE", ticker: "FAKE");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Utc),
            shares: 6.584m, amount: 316.37m, price: 48.051337m);
        var loser = await ImportReinvestAsync(client, ledger, brokerage.Id);

        // The dot is lit: one imported row awaits review.
        Assert.Equal(1, await ReviewCountAsync(client, ledger, brokerage.Id));

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = winner });
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
    private static async Task<Guid> ImportReinvestAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId)
    {
        var content = new MultipartFormDataContent();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(OfxReinvestStatement));
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
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO provider_security_mappings
                (id, ledger_id, provider_key, provider_security_id, security_id)
            VALUES (gen_random_uuid(), {ledger.LedgerId}, 'ofx',
                    {providerSecurityId}, {securityId})
            """);
    }

    /// <summary>
    /// A REINVEST whose TOTAL is the only usable number: the cash movement is 0.00
    /// and 6.584 x 48.05 rounds to 316.36, not the 316.37 the statement settles.
    /// </summary>
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
        <TOTAL>-316.37
        <SUBACCTSEC>CASH
        <UNITS>6.584
        <UNITPRICE>48.05
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

}
