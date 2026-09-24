using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Reproductions for the two data-costing divergences between the bank and
/// investment PATCH paths (parity audit, 2026-09-22).
/// </summary>
/// <remarks>
/// <para>Both come from one design choice.
/// <c>InvestmentTransactionsRepository.PatchAsync</c> removes every leg on the
/// header and rebuilds them with fresh ids, and writes header fields straight
/// onto <c>txn_headers</c>. The bank path keeps the legs a request names by
/// <c>legId</c> and mutates them in place, and routes header fields through the
/// ADR-0003 override layer.</para>
///
/// <para>These are written as OBSERVABLE consequences rather than assertions
/// about which table gets written, so they stay honest whichever way the fix
/// goes: one says a cleared row stays cleared, the other says an edited date is
/// the date the register shows. Neither prescribes a mechanism.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InvestmentPatchParityTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentPatchParityTests(PostgresFixture fixture) => _fixture = fixture;

    private static DateTime Utc(int y, int m, int d) =>
        new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

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
        return (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("headerId").GetGuid();
    }

    /// <summary>
    /// Clearing a brokerage row and then editing it must not un-clear it.
    /// </summary>
    /// <remarks>
    /// The investment register offers reconciliation both per-row and in bulk,
    /// so this is reachable with two ordinary clicks and a save. The edit here
    /// changes only the memo — nothing about the money — which is what makes the
    /// loss surprising: a reconciled brokerage account drifts, and
    /// <c>cleared_at</c> / <c>cleared_by_user_id</c> go with it, leaving no trace
    /// that it ever was reconciled.
    /// </remarks>
    [Fact]
    public async Task Editing_a_cleared_investment_row_keeps_it_cleared()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("PARX", ticker: "PARX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        var headerId = await BuyAsync(client, ledger, brokerage.Id, securityId,
            postedAt, shares: 10m, amount: 1000m, price: 100m);

        var cleared = await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}/recon-status",
            new SetReconStatusRequest { Status = "cleared", AccountId = brokerage.Id });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);

        // Premise: it really is cleared, with the audit pair written.
        await using (var db = _fixture.NewDbContext())
        {
            var before = await db.TxnLegRecon.AsNoTracking()
                .Join(db.TxnLegs.AsNoTracking(), r => r.LegId, l => l.Id, (r, l) => new { r, l })
                .Where(x => x.l.HeaderId == headerId && x.l.AccountId == brokerage.Id)
                .Select(x => x.r)
                .SingleAsync();
            Assert.Equal("cleared", before.Status);
            Assert.NotNull(before.ClearedAt);
        }

        // An edit that touches nothing about the money.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                Amount = 1000m,
                Memo = "corrected memo",
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: "
            + await patch.Content.ReadAsStringAsync());

        await using (var db = _fixture.NewDbContext())
        {
            var after = await db.TxnLegRecon.AsNoTracking()
                .Join(db.TxnLegs.AsNoTracking(), r => r.LegId, l => l.Id, (r, l) => new { r, l })
                .Where(x => x.l.HeaderId == headerId && x.l.AccountId == brokerage.Id)
                .Select(x => x.r)
                .SingleOrDefaultAsync();

            Assert.True(after is not null,
                "the reconciliation overlay was destroyed by an unrelated edit");
            Assert.Equal("cleared", after!.Status);
            Assert.NotNull(after.ClearedAt);
        }
    }

    /// <summary>
    /// Editing a merge survivor's date changes the date the register shows.
    /// </summary>
    /// <remarks>
    /// <para>This was skipped for two releases as a KNOWN bug. A merge stamped a
    /// <c>posted_at</c> OVERRIDE on the winner while the investment PATCH wrote
    /// the canonical column, and <c>resolved_transactions</c> COALESCEd the
    /// override over it — so a later edit landed UNDERNEATH the stamp and was
    /// invisible. The user retyped the date, saved successfully, and the register
    /// did not move. Nothing ever deleted an override row, so it stayed that way.</para>
    ///
    /// <para>Migration 230 removed the layer the two writers disagreed about:
    /// there is one date column now and both paths assign it. Migration 229 had
    /// already moved the FIFO walk onto the effective date, which was the
    /// entanglement that kept this deferred — the walk and the register could not
    /// be allowed to disagree about where a row sits.</para>
    ///
    /// <para>Asserted against <c>resolved_transactions</c> rather than any
    /// particular table, because that view is what the register renders.</para>
    /// </remarks>
    [Fact]
    public async Task Editing_a_merge_survivors_date_moves_what_the_register_shows()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("PARX", ticker: "PARX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var winnerDate = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        var loserDate = new DateTime(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);

        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            winnerDate, shares: 10m, amount: 1000m, price: 100m);
        var loser = await BuyAsync(client, ledger, brokerage.Id, securityId,
            loserDate, shares: 10m, amount: 1000m, price: 100m);

        // Make the loser mergeable the way ingest does — an unreviewed row. This
        // is the one fabricated bit, and it stands in for an import purely to
        // keep the test about the EDIT rather than about ingest.
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE txn_headers SET needs_review = TRUE WHERE id = {loser}");
        }

        var merge = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = winner });
        Assert.Equal(HttpStatusCode.NoContent, merge.StatusCode);

        // Premise: the survivor adopted the loser's date.
        Assert.Equal(loserDate, await ShownDateAsync(winner, brokerage.Id));

        // Now the user corrects it to a third date.
        var corrected = new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{winner}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = corrected,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                Amount = 1000m,
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: "
            + await patch.Content.ReadAsStringAsync());

        Assert.Equal(corrected, await ShownDateAsync(winner, brokerage.Id));
    }

    /// <summary>The effective date the register renders for a header.</summary>
    private async Task<DateTime> ShownDateAsync(Guid headerId, Guid accountId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.ResolvedTransactions.AsNoTracking()
            .Where(rv => rv.HeaderId == headerId && rv.AccountId == accountId)
            .Select(rv => rv.PostedAt)
            .FirstAsync();
    }
    /// <summary>
    /// The reconciliation stamp survives an AMOUNT edit, not just a cosmetic one.
    /// </summary>
    /// <remarks>
    /// Deliberate, and the deliberate part is worth stating: preserving the leg
    /// preserves <c>cleared_at</c> / <c>cleared_by_user_id</c> even when the figure
    /// that was reconciled changes, so "reconciled" can attest to a number edited
    /// after the fact. The alternative — drop to uncleared on an amount change —
    /// was considered and rejected in favour of matching the bank, which has
    /// behaved this way since migration 171. Re-clearing is one click; silently
    /// diverging from the bank on the same gesture is not recoverable by the user.
    /// </remarks>
    [Fact]
    public async Task The_reconciliation_stamp_survives_an_amount_edit()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("PARX", ticker: "PARX");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        var headerId = await BuyAsync(client, ledger, brokerage.Id, securityId,
            postedAt, shares: 10m, amount: 1000m, price: 100m);

        await client.PutAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}/recon-status",
            new SetReconStatusRequest { Status = "cleared", AccountId = brokerage.Id });

        // The money itself changes.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 11m,
                Price = 100m,
                Amount = 1100m,
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);

        await using var db = _fixture.NewDbContext();
        var recon = await db.TxnLegRecon.AsNoTracking()
            .Join(db.TxnLegs.AsNoTracking(), r => r.LegId, l => l.Id, (r, l) => new { r, l })
            .Where(x => x.l.HeaderId == headerId && x.l.AccountId == brokerage.Id)
            .Select(x => x.r)
            .SingleOrDefaultAsync();
        Assert.NotNull(recon);
        Assert.Equal("cleared", recon!.Status);
    }

    /// <summary>
    /// A date-only edit still re-derives the FIFO consumption order.
    /// </summary>
    /// <remarks>
    /// This is the regression that keeping leg identity introduces, and the reason
    /// the recompute is now called explicitly. <c>HoldingsRecomputeInterceptor</c>
    /// watches <c>txn_legs</c> and nothing else; it fired on every PATCH only
    /// because every PATCH deleted every leg. Once legs survive, an edit that
    /// changes no leg field produces no entry for it to see — and a DATE is exactly
    /// such an edit, while also being the thing the FIFO walk orders by. Lots,
    /// realized gains and cost basis would keep the pre-edit ordering.
    ///
    /// Anchored on realized gain rather than on lot rows: it is the number a person
    /// would file, and it differs by the full spread between the two lots.
    /// </remarks>
    [Fact]
    public async Task A_date_only_edit_re_derives_the_fifo_order()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("FIFX", ticker: "FIFX");
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Cheap lot first, dear lot second.
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            shares: 10m, amount: 100m, price: 10m);
        var dear = await BuyAsync(client, ledger, brokerage.Id, securityId,
            new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc),
            shares: 10m, amount: 200m, price: 20m);

        var sell = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
                Action = "sell",
                SecurityId = securityId,
                // Negative shares: a disposal, per the create contract.
                Shares = -10m,
                Price = 30m,
            });
        Assert.Equal(HttpStatusCode.Created, sell.StatusCode);

        // FIFO took the cheap lot: 300 proceeds - 100 basis.
        Assert.Equal(200m, await RealizedGainAsync(holdingsAccountId, securityId));

        // Move the DEAR buy in front of it. Nothing else changes — same shares,
        // same price, same amount, same account, same security.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{dear}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc),
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 20m,
                Amount = 200m,
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: "
            + await patch.Content.ReadAsStringAsync());

        // Now the dear lot is the one FIFO consumed: 300 - 200.
        Assert.Equal(100m, await RealizedGainAsync(holdingsAccountId, securityId));
    }

    /// <summary>
    /// A PATCH leaves exactly one lot per acquisition leg.
    /// </summary>
    /// <remarks>
    /// The lot delete is header-wide and deliberately kept that way. It used to be
    /// belt-and-braces over <c>lots.leg_id ON DELETE CASCADE</c> (mig 123); with the
    /// leg surviving, the cascade is no longer a backstop and the delete is the only
    /// thing standing between an edit and a second lot on the same leg. That failure
    /// is silent — <c>holdings.quantity</c> is recomputed and stays right while the
    /// lots table doubles underneath it.
    /// </remarks>
    [Fact]
    public async Task A_patch_leaves_exactly_one_lot_per_acquisition_leg()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("LOTZ", ticker: "LOTZ");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var headerId = await BuyAsync(client, ledger, brokerage.Id, securityId,
            postedAt, shares: 10m, amount: 1000m, price: 100m);

        for (var i = 0; i < 2; i++)
        {
            var patch = await client.PatchAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
                new PatchInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id,
                    PostedAt = postedAt,
                    Action = "buy",
                    SecurityId = securityId,
                    Shares = 10m + i,
                    Price = 100m,
                    Amount = 1000m + (100m * i),
                });
            Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent);
        }

        await using var db = _fixture.NewDbContext();
        var legIds = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .Select(l => l.Id).ToListAsync();
        var lotsPerLeg = await db.Lots.AsNoTracking()
            .Where(lot => legIds.Contains(lot.LegId))
            .GroupBy(lot => lot.LegId)
            .Select(g => g.Count())
            .ToListAsync();
        Assert.All(lotsPerLeg, n => Assert.Equal(1, n));
    }

    /// <summary>
    /// Changing the action reshapes the postings without tripping the posting
    /// uniqueness index.
    /// </summary>
    /// <remarks>
    /// The renumber is the delicate part of keeping legs: a kept leg shifting down
    /// into an index a doomed leg still occupies violates
    /// <c>uq_txn_legs_posting (header_id, posting_index, account_id)</c>, and EF
    /// gives no ordering guarantee between the delete and the update that wants the
    /// vacated slot. Kept legs are parked at a large offset and flushed first, the
    /// way the bank reshape does it.
    /// </remarks>
    [Fact]
    public async Task Changing_the_action_reshapes_postings_without_an_index_collision()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("RSHP", ticker: "RSHP");
        var income = await ledger.AddCategoryAsync("Dividends");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var headerId = await BuyAsync(client, ledger, brokerage.Id, securityId,
            postedAt, shares: 10m, amount: 1000m, price: 100m);

        // buy -> dividend_reinvest: a different posting shape on the same header.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "dividend_reinvest",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                Amount = 1000m,
                CategoryAccountId = income.Id,
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: "
            + await patch.Content.ReadAsStringAsync());

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .Select(l => new { l.AccountId, l.PostingIndex })
            .ToListAsync();
        // No parked index survived the save, and no (index, account) is duplicated.
        Assert.All(legs, l => Assert.True(l.PostingIndex < 1000,
            $"a leg is still parked at the shift offset: {l.PostingIndex}"));
        Assert.Equal(legs.Count, legs.Select(l => (l.AccountId, l.PostingIndex)).Distinct().Count());
    }

    /// <summary>
    /// An in-kind transfer can still be edited. It is excluded from leg reuse, so
    /// this guards the branch the exclusion creates.
    /// </summary>
    /// <remarks>
    /// <c>transfer_shares</c> keeps destroy-and-rebuild because its FIFO plan is
    /// computed from committed lots AFTER the leg drop, precisely so the plan cannot
    /// see this transfer's own effect. Leave its legs in place and the plan consumes
    /// against lots the transfer is still holding down, which surfaces as a spurious
    /// insufficient-shares rejection or a double move. There was no coverage of
    /// PATCHing one at all before this.
    /// </remarks>
    [Fact]
    public async Task An_in_kind_transfer_can_still_be_edited()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var source = await ledger.AddInvestmentAccountAsync("source");
        var dest = await ledger.AddInvestmentAccountAsync("dest");
        var securityId = await ledger.AddSecurityAsync("XFER", ticker: "XFER");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        await BuyAsync(client, ledger, source.Id, securityId,
            new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc),
            shares: 100m, amount: 1000m, price: 10m);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = source.Id,
                PostedAt = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc),
                Action = "transfer_shares",
                SecurityId = securityId,
                Shares = 15m,
                TransferAccountId = dest.Id,
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var headerId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("headerId").GetGuid();

        // Move a different number of shares.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = source.Id,
                PostedAt = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc),
                Action = "transfer_shares",
                SecurityId = securityId,
                Shares = 25m,
                TransferAccountId = dest.Id,
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: "
            + await patch.Content.ReadAsStringAsync());

        await using var db = _fixture.NewDbContext();
        var destQty = await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == dest.HoldingsAccountId!.Value
                && h.SecurityId == securityId)
            .Select(h => h.Quantity).SingleAsync();
        var sourceQty = await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == source.HoldingsAccountId!.Value
                && h.SecurityId == securityId)
            .Select(h => h.Quantity).SingleAsync();
        Assert.Equal(25m, destQty);
        Assert.Equal(75m, sourceQty);
    }

    // `A_stale_leg_override_does_not_survive_the_edit_that_supersedes_it` lived
    // here. It seeded a txn_leg_overrides row with raw SQL and asserted the edit
    // cleared it. Migration 230 dropped that table — zero rows in every
    // database, no writer anywhere in the repository — so the premise went with
    // it. What the test was really protecting, that a reused leg keeps its
    // txn_leg_recon row across an edit, is covered on its own above.

    /// <summary>
    /// The FIFO walk orders by the EFFECTIVE date, so a curated date re-sorts
    /// lot consumption the way the register says it should.
    /// </summary>
    /// <remarks>
    /// Migration 229. Every holdings function used to read the raw
    /// <c>txn_headers.posted_at</c> while the register renders
    /// <c>COALESCE(o.posted_at, h.posted_at)</c>, so a row could sit in one place
    /// on screen and in another in the walk that decides which lots a sale
    /// consumes, what basis it books and which period the gain lands in.
    ///
    /// Not hypothetical: an investment merge stamps a posted_at override on the
    /// SURVIVOR, so every merge winner already had a displayed date its cost
    /// basis did not know about.
    ///
    /// The override is written directly rather than through an edit, because the
    /// point is the WALK's reading of it — the edit path that produces one is a
    /// separate concern (and its own open bug).
    /// </remarks>
    [Fact]
    public async Task The_fifo_walk_orders_by_the_effective_date()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("EFFX", ticker: "EFFX");
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Cheap lot first by RAW date, dear lot second.
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            Utc(2026, 3, 1), shares: 10m, amount: 100m, price: 10m);
        var dear = await BuyAsync(client, ledger, brokerage.Id, securityId,
            Utc(2026, 3, 10), shares: 10m, amount: 200m, price: 20m);

        var sell = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = Utc(2026, 4, 1),
                Action = "sell",
                SecurityId = securityId,
                Shares = -10m,
                Price = 30m,
            });
        Assert.Equal(HttpStatusCode.Created, sell.StatusCode);

        // FIFO took the cheap lot: 300 proceeds - 100 basis.
        Assert.Equal(200m, await RealizedGainAsync(holdingsAccountId, securityId));

        // Curate the DEAR buy to before the cheap one. Raw dates are untouched,
        // so only an effective-date reading can see this.
        await ledger.EditHeaderAsync(dear, postedAt: Utc(2026, 2, 1));
        await RecomputeHoldingsAsync(holdingsAccountId, securityId);

        // The dear lot is now first, so the sale consumed it: 300 - 200.
        Assert.Equal(100m, await RealizedGainAsync(holdingsAccountId, securityId));
    }

    /// <summary>
    /// Re-derive one holding. Writing an override directly moves no legs, so no
    /// interceptor fires — the walk has to be asked.
    /// </summary>
    private async Task RecomputeHoldingsAsync(Guid holdingsAccountId, Guid securityId)
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT recompute_holdings_for_account_security({holdingsAccountId}, {securityId})");
    }

    /// <summary>Total realized gain booked for one holding.</summary>
    private async Task<decimal> RealizedGainAsync(Guid holdingsAccountId, Guid securityId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.RealizedGains.AsNoTracking()
            .Where(g => g.AccountId == holdingsAccountId && g.SecurityId == securityId)
            .SumAsync(g => g.RealizedGain);
    }

}
