using System.Net;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;

using Coffer.Api.Contracts;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Ingest;

/// <summary>
/// A PENDING transaction, and the moment it stops being one (dimension 14).
/// </summary>
/// <remarks>
/// <para>No import path can produce it at all. <c>TransactionMapper</c>,
/// <c>InvestmentTransactionMapper</c> and <c>ReminderMapper</c> each hard-code
/// <c>IsPending: false</c>, so a Moneydance fixture is structurally incapable
/// of carrying one — the same shape of answer as <c>reconciling</c>, which the
/// importer also cannot emit. <b>The only writer that ever sets it true is the
/// feed ingest path</b>, from a provider's own pending flag.</para>
///
/// <para>So the coverage cannot come from a fixture. <c>SimpleFinClientTests</c>
/// proves the wire's <c>pending</c> parses; these tests pin what is downstream of
/// that, which is the whole reason the column exists: a transaction arrives pending, the bank posts it a day later under the SAME
/// provider id, and the row has to FLIP rather than duplicate.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class PendingTransactionLifecycleTests : DatabaseTest
{
    public PendingTransactionLifecycleTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>The same transaction id, twice: pending, then posted.</summary>
    private static string Body(bool pending) => $$"""
        {"connections":[
          {"conn_id":"c-P","name":"Bank P","org_id":"bankp","sfin_url":"https://sfin/bankp"}
        ],"errlist":[],"accounts":[
          {"id":"sf-p","conn_id":"c-P","name":"Checking","currency":"USD","balance":"100.00","transactions":[
            {"id":"pending-1","posted":1715000000,"amount":"-42.50","description":"HOLD AT PUMP","pending":{{(pending ? "true" : "false")}}}
          ]}
        ]}
        """;

    [Fact]
    public async Task A_pending_transaction_lands_pending_then_flips_when_the_bank_posts_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var keys = Db.NewLedgerKeyService();
        await using (var seed = Db.NewDbContext())
        {
            var row = await seed.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
            row.WrappedLek = keys.CreateWrappedLek();
            row.LekKekId = keys.CurrentKekId;
            row.LekCreatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        // One mutable body so the SECOND sync can answer differently to the
        // first — which is the whole scenario. A fixed stub could only ever
        // test one half of a lifecycle.
        var pending = true;
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient(() => Body(pending)));
        using var client = await AuthedClientAsync(factory, ledger);

        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/feed-connections",
            new CreateFeedConnectionRequest { SetupToken = SetupToken("https://x/c") });
        var connection = (await created.Content.ReadFromJsonAsync<FeedConnectionSummary>())!;

        var bank = await ledger.AddBankAccountAsync("checking");
        var mapped = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connection.Id,
                SimpleFinAccountId = "sf-p",
            }),
        });
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);

        async Task SyncAsync()
        {
            var resp = await client.PostAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync",
                content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        async Task<(int Count, bool IsPending, Guid? Owner)> StateAsync()
        {
            await using var db = Db.NewDbContext();
            var rows = await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == ledger.LedgerId)
                .ToListAsync();
            return (rows.Count, rows.Count == 1 && rows[0].IsPending,
                    rows.Count == 1 ? rows[0].OriginatingAccountId : null);
        }

        await SyncAsync();
        var afterFirst = await StateAsync();
        Assert.Equal(1, afterFirst.Count);
        Assert.True(afterFirst.IsPending,
            "a transaction the bank reported as pending was stored as posted");
        // Owned by the account it was imported into (mig 263).
        Assert.Equal(bank.Id, afterFirst.Owner);

        // The bank posts it. Same provider id, same amount.
        pending = false;
        await SyncAsync();
        var afterSecond = await StateAsync();

        // ONE row, not two. The dedup key and the pending flip are the same
        // mechanism, and getting the first right while getting the second
        // wrong would leave the ledger with a permanent phantom hold.
        Assert.Equal(1, afterSecond.Count);
        Assert.False(afterSecond.IsPending,
            "the bank posted it, but the row is still flagged pending");
        // A resync of the existing row leaves its owner alone.
        Assert.Equal(bank.Id, afterSecond.Owner);
    }

    // ----- promote-on-clear: the cleared figure (ADR-0114 D15) -----

    /// <summary>
    /// A row the user approved while pending clears at a figure finer than its currency:
    /// the synced leg takes it rounded and the category leg its negation, the originals
    /// are seeded with the bank's figure and the rounded mark, the row goes back to
    /// review, and the promotion's audit records the figure written to the leg.
    /// </summary>
    [Fact]
    public async Task A_cleared_figure_finer_than_its_currency_is_rounded_seeded_and_held_for_review()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.50", pending: true);
        var headerId = await feed.HeaderIdAsync();
        await feed.PatchAsync(headerId, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        Assert.False((await feed.HeaderAsync(headerId)).NeedsReview);

        await feed.SyncAsync("-42.505", pending: false);

        var header = await feed.HeaderAsync(headerId);
        Assert.False(header.IsPending);
        Assert.True(header.NeedsReview, "a rounded promotion was not held for review");
        var legs = await feed.LegsAsync(headerId);
        Assert.Equal(-42.51m, legs.Single(l => l.AccountId == feed.Bank).Amount);
        Assert.Equal(42.51m, legs.Single(l => l.AccountId != feed.Bank).Amount);
        var originals = await feed.OriginalsAsync(headerId);
        Assert.NotNull(originals);
        Assert.Equal(-42.505m, originals!.Amount);
        Assert.True(originals.RoundedAtImport);
        Assert.Equal("Corner Cafe", originals.Payee);
        Assert.Equal("CORNER CAFE 0042", originals.Memo);
        var promotion = await feed.PromotionAsync(headerId);
        Assert.Equal(-42.50m, promotion.WasAmount);
        Assert.Equal(-42.51m, promotion.BecameAmount);
    }

    /// <summary>A row seeded at insert clears at a figure that fits: the originals take
    /// the cleared figure and lose the rounded mark, and keep their payee and memo.</summary>
    [Fact]
    public async Task A_seeded_row_that_clears_at_a_figure_that_fits_restates_its_originals()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.505", pending: true);
        var headerId = await feed.HeaderIdAsync();
        Assert.True((await feed.OriginalsAsync(headerId))!.RoundedAtImport);

        await feed.SyncAsync("-43.00", pending: false);

        Assert.Equal(-43.00m, (await feed.LegsAsync(headerId)).Single(l => l.AccountId == feed.Bank).Amount);
        var originals = await feed.OriginalsAsync(headerId);
        Assert.Equal(-43.00m, originals!.Amount);
        Assert.False(originals.RoundedAtImport);
        Assert.Equal("Corner Cafe", originals.Payee);
        Assert.Equal("CORNER CAFE 0042", originals.Memo);
        Assert.Equal(-43.00m, (await feed.PromotionAsync(headerId)).BecameAmount);
    }

    /// <summary>A row whose originals a payee edit captured clears with rounding: the
    /// originals take the bank's figure and the rounded mark and keep the captured
    /// payee; the header keeps the edit.</summary>
    [Fact]
    public async Task An_edited_row_that_clears_with_rounding_restates_only_the_amount_and_the_mark()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.50", pending: true);
        var headerId = await feed.HeaderIdAsync();
        await feed.PatchAsync(headerId, new PatchTransactionRequest { Payee = "Payee A" }, HttpStatusCode.NoContent);
        var captured = await feed.OriginalsAsync(headerId);
        Assert.Equal(("Corner Cafe", -42.50m, false), (captured!.Payee, captured.Amount, captured.RoundedAtImport));

        await feed.SyncAsync("-42.505", pending: false);

        var originals = await feed.OriginalsAsync(headerId);
        Assert.Equal(-42.505m, originals!.Amount);
        Assert.True(originals.RoundedAtImport);
        Assert.Equal("Corner Cafe", originals.Payee);
        Assert.Equal("CORNER CAFE 0042", originals.Memo);
        var header = await feed.HeaderAsync(headerId);
        Assert.Equal("Payee A", header.Payee);
        Assert.True(header.NeedsReview);
        Assert.Equal(-42.51m, (await feed.LegsAsync(headerId)).Single(l => l.AccountId == feed.Bank).Amount);
    }

    /// <summary>A row with no originals that clears at a figure that fits writes none,
    /// and the user's review state is left.</summary>
    [Fact]
    public async Task A_row_with_no_originals_that_clears_at_a_figure_that_fits_writes_none()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.50", pending: true);
        var headerId = await feed.HeaderIdAsync();
        await feed.PatchAsync(headerId, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);

        await feed.SyncAsync("-45.000", pending: false);

        Assert.Null(await feed.OriginalsAsync(headerId));
        Assert.False((await feed.HeaderAsync(headerId)).NeedsReview);
        Assert.Equal(-45.000m, (await feed.LegsAsync(headerId)).Single(l => l.AccountId == feed.Bank).Amount);
    }

    /// <summary>A pending row folded into a settled manual entry clears with rounding: its
    /// legs and originals take the cleared figure, and it stays merged and out of review,
    /// since a merge loser is never awaiting review (ADR-0114 D15).</summary>
    [Fact]
    public async Task A_merged_loser_that_clears_with_rounding_is_restated_but_not_held_for_review()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.50", pending: true);
        var loser = await feed.HeaderIdAsync();
        var winner = await feed.ManualRowAsync(-42.50m);
        await feed.PatchAsync(winner, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        var merge = await feed.Client.PostAsJsonAsync(
            $"/api/ledgers/{feed.Ledger.LedgerId}/transactions/{loser}/merge",
            new MergeTransactionRequest { FromHeaderId = winner });
        Assert.True(merge.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            await merge.Content.ReadAsStringAsync());
        Assert.False((await feed.HeaderAsync(loser)).NeedsReview);

        await feed.SyncAsync("-42.505", pending: false);

        var header = await feed.HeaderAsync(loser);
        Assert.False(header.IsPending);
        Assert.Equal(winner, header.IsMergedInto);
        Assert.False(header.NeedsReview, "a merge loser was put back in the review queue");
        Assert.Equal(-42.51m, (await feed.LegsAsync(loser)).Single(l => l.AccountId == feed.Bank).Amount);
        var originals = await feed.OriginalsAsync(loser);
        Assert.Equal(-42.505m, originals!.Amount);
        Assert.True(originals.RoundedAtImport);
    }

    // ----- promote-on-clear of a merge loser: its winner (ADR-0114 D15) -----

    /// <summary>A pending row folded into a settled manual entry clears at another figure:
    /// the loser is restated and stays out of review, and the row the user kept goes back
    /// to review with its own amounts as they were, since the bank's figure lands only on
    /// the folded-away row, which no register shows.</summary>
    [Fact]
    public async Task A_merge_winner_whose_pending_loser_clears_at_another_figure_is_held_for_review()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.50", pending: true);
        var loser = await feed.HeaderIdAsync();
        var winner = await feed.ManualRowAsync(-42.50m);
        await feed.PatchAsync(winner, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(loser, winner);
        Assert.False((await feed.HeaderAsync(winner)).NeedsReview);

        await feed.SyncAsync("-45.00", pending: false);

        Assert.True((await feed.HeaderAsync(winner)).NeedsReview,
            "the loser cleared at another figure, but the kept row was not put back in review");
        var winnerLegs = await feed.LegsAsync(winner);
        Assert.Equal(-42.50m, winnerLegs.Single(l => l.AccountId == feed.Bank).Amount);
        Assert.Equal(42.50m, winnerLegs.Single(l => l.AccountId != feed.Bank).Amount);
        var loserRow = await feed.HeaderAsync(loser);
        Assert.Equal(winner, loserRow.IsMergedInto);
        Assert.False(loserRow.NeedsReview, "a merge loser was put back in the review queue");
        Assert.Equal(-45.00m, (await feed.LegsAsync(loser)).Single(l => l.AccountId == feed.Bank).Amount);
    }

    /// <summary>A pending loser that clears at the figure it held leaves its winner's
    /// review state alone.</summary>
    [Fact]
    public async Task A_merge_winner_whose_pending_loser_clears_at_the_figure_it_held_stays_out_of_review()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.50", pending: true);
        var loser = await feed.HeaderIdAsync();
        var winner = await feed.ManualRowAsync(-45.00m);
        await feed.PatchAsync(winner, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(loser, winner);

        await feed.SyncAsync("-42.50", pending: false);

        Assert.False((await feed.HeaderAsync(winner)).NeedsReview,
            "the loser cleared at the figure it held, but the kept row was put back in review");
        Assert.False((await feed.HeaderAsync(loser)).IsPending);
    }

    /// <summary>A split winner is held for review the same way, every line as it was.</summary>
    [Fact]
    public async Task A_split_merge_winner_whose_pending_loser_clears_at_another_figure_is_held_for_review()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.50", pending: true);
        var loser = await feed.HeaderIdAsync();
        var winner = await feed.ManualSplitRowAsync(-30.00m, -12.50m);
        await feed.PatchAsync(winner, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(loser, winner);
        var before = (await feed.LegsAsync(winner)).Select(l => (l.Id, l.Amount)).OrderBy(l => l.Id).ToList();

        await feed.SyncAsync("-45.00", pending: false);

        Assert.True((await feed.HeaderAsync(winner)).NeedsReview,
            "the loser cleared at another figure, but the kept split was not put back in review");
        Assert.Equal(before, (await feed.LegsAsync(winner)).Select(l => (l.Id, l.Amount)).OrderBy(l => l.Id).ToList());
    }

    /// <summary>A winner since folded into another row: the row at the end of the chain,
    /// the one a register shows, is held for review, and the winner in between, itself a
    /// loser now, is not.</summary>
    [Fact]
    public async Task A_pending_loser_whose_winner_was_merged_again_holds_the_last_winner_for_review()
    {
        await using var feed = await FeedAsync();
        feed.Second = ("-20.00", true);
        await feed.SyncAsync("-42.50", pending: true);
        var first = await feed.FeedRowIdAsync("pending-1");
        var second = await feed.FeedRowIdAsync("pending-2");
        var middle = await feed.ManualRowAsync(-62.50m);
        await feed.PatchAsync(middle, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(first, middle);
        await feed.MergeAsync(second, middle);

        // The first clears at another figure, which puts the middle row in review, and
        // the user then folds that row into another.
        await feed.SyncAsync("-45.00", pending: false);
        Assert.True((await feed.HeaderAsync(middle)).NeedsReview);
        var last = await feed.ManualRowAsync(-65.00m, "groceries");
        await feed.PatchAsync(last, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(middle, last);

        feed.Second = ("-21.00", false);
        await feed.SyncAsync("-45.00", pending: false);

        Assert.True((await feed.HeaderAsync(last)).NeedsReview,
            "the loser cleared at another figure, but the row at the end of its chain was not put back in review");
        var middleRow = await feed.HeaderAsync(middle);
        Assert.Equal(last, middleRow.IsMergedInto);
        Assert.False(middleRow.NeedsReview, "a merge loser was put back in the review queue");
    }

    /// <summary>
    /// A currency change refused for two transfers whose amounts would differ in one
    /// currency (ADR-0114 D13): one on a feed row merged into a row that was itself merged
    /// into another later, the other on that middle row. The refusal names the last row
    /// once, the one a register shows, and promises no way back, since deleting it
    /// un-merges only the middle row (settled by Claude, 2026-11-18). Before the middle row
    /// is merged away, the way back is promised. The chain is reached as a person reaches
    /// it: a pending loser clears at another figure, which puts its winner back in review,
    /// and the person folds that winner into another row.
    /// </summary>
    /// <remarks>Lives here for the feed this class drives; the refusal's wording and parser
    /// are <see cref="Accounts.AccountCurrencyRelabelTests"/>'.</remarks>
    [Fact]
    public async Task A_pair_merged_down_a_chain_is_refused_naming_the_last_row_with_no_way_back()
    {
        await using var feed = await FeedAsync();
        feed.Second = ("-20.00", true);
        await feed.SyncAsync("-42.50", pending: true);
        var first = await feed.FeedRowIdAsync("pending-1");
        var pair = await feed.FeedRowIdAsync("pending-2");
        var eur = (await feed.Ledger.AddBankAccountAsync("Bank B", currencyCode: "EUR")).Id;
        var middle = await feed.ManualTransferAsync(eur, -62.50m, 57.00m);
        await feed.PatchAsync(middle, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.SaveAsTransferAsync(pair, eur, -20.00m, 18.00m);
        await feed.MergeAsync(first, middle);
        await feed.MergeAsync(pair, middle);
        await feed.SyncAsync("-45.00", pending: false);
        Assert.True((await feed.HeaderAsync(middle)).NeedsReview);

        // The anchor: the merged-away pair's winner, entered by hand, is in a register, so
        // the way back is promised; the middle row's own pair is in a register too.
        var anchor = await Accounts.AccountCurrencyRelabelTests.PairRefusalAsync(await feed.SetCurrencyAsync(eur, "USD"));
        Assert.Equal(
            (2, Accounts.AccountCurrencyRelabelTests.PairDetail("USD") + Accounts.AccountCurrencyRelabelTests.MixedSentence
                + Accounts.AccountCurrencyRelabelTests.RestoreSentence),
            (anchor.Count, anchor.Detail));
        Assert.Equal(middle, Assert.Single(anchor.MergedInto!).HeaderId);

        var last = await feed.ManualRowAsync(-65.00m, "groceries");
        await feed.PatchAsync(last, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(middle, last);

        var refusal = await Accounts.AccountCurrencyRelabelTests.PairRefusalAsync(await feed.SetCurrencyAsync(eur, "USD"));
        Assert.Equal(
            (2, Accounts.AccountCurrencyRelabelTests.PairDetail("USD") + Accounts.AccountCurrencyRelabelTests.MergedAwaySentence),
            (refusal.Count, refusal.Detail));
        Assert.Equal(last, Assert.Single(refusal.MergedInto!).HeaderId);
        Assert.Equal(middle, (await feed.HeaderAsync(pair)).IsMergedInto);
    }

    /// <summary>A feed-row winner the user deleted stays out of review when its loser
    /// clears at another figure, since a soft delete resolves the row (ADR-0052 D3).</summary>
    [Fact]
    public async Task A_deleted_merge_winner_stays_out_of_review_when_its_loser_clears_at_another_figure()
    {
        await using var feed = await FeedAsync();
        feed.Second = ("-42.50", true);
        await feed.SyncAsync("-42.50", pending: true);
        var loser = await feed.FeedRowIdAsync("pending-1");
        var winner = await feed.FeedRowIdAsync("pending-2");
        await feed.PatchAsync(winner, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(loser, winner);
        var delete = await feed.Client.DeleteAsync($"/api/ledgers/{feed.Ledger.LedgerId}/transactions/{winner}");
        Assert.True(delete.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            await delete.Content.ReadAsStringAsync());
        Assert.True((await feed.HeaderAsync(winner)).IsHidden);

        await feed.SyncAsync("-45.00", pending: false);

        var winnerRow = await feed.HeaderAsync(winner);
        Assert.True(winnerRow.IsHidden);
        Assert.False(winnerRow.NeedsReview, "a deleted row was put back in the review queue");
        Assert.Equal(-45.00m, (await feed.LegsAsync(loser)).Single(l => l.AccountId == feed.Bank).Amount);
    }

    /// <summary>A pending row deleted before it clears with rounding: its legs and
    /// originals take the cleared figure, and it stays hidden and out of review, since a
    /// soft delete resolves the row (ADR-0052 D3).</summary>
    [Fact]
    public async Task A_deleted_row_that_clears_with_rounding_is_restated_but_not_held_for_review()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.50", pending: true);
        var headerId = await feed.HeaderIdAsync();
        var delete = await feed.Client.DeleteAsync($"/api/ledgers/{feed.Ledger.LedgerId}/transactions/{headerId}");
        Assert.True(delete.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            await delete.Content.ReadAsStringAsync());
        var deleted = await feed.HeaderAsync(headerId);
        Assert.True(deleted.IsHidden);
        Assert.False(deleted.NeedsReview);

        await feed.SyncAsync("-42.505", pending: false);

        var header = await feed.HeaderAsync(headerId);
        Assert.False(header.IsPending);
        Assert.True(header.IsHidden);
        Assert.False(header.NeedsReview, "a deleted row was put back in the review queue");
        Assert.Equal(-42.51m, (await feed.LegsAsync(headerId)).Single(l => l.AccountId == feed.Bank).Amount);
        var originals = await feed.OriginalsAsync(headerId);
        Assert.Equal(-42.505m, originals!.Amount);
        Assert.True(originals.RoundedAtImport);
    }

    // ----- promote-on-clear of a split (ADR-0114 D15, decided by the owner, 2026-11-17) -----

    /// <summary>A one-posting row takes the cleared figure on its synced leg, and its
    /// category leg the negation, with the user's review state left.</summary>
    [Fact]
    public async Task A_one_posting_row_takes_the_cleared_figure_on_both_legs()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.50", pending: true);
        var headerId = await feed.HeaderIdAsync();
        await feed.PatchAsync(headerId, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);

        await feed.SyncAsync("-45.00", pending: false);

        var legs = await feed.LegsAsync(headerId);
        Assert.Equal(-45.00m, legs.Single(l => l.AccountId == feed.Bank).Amount);
        Assert.Equal(45.00m, legs.Single(l => l.AccountId != feed.Bank).Amount);
        Assert.False((await feed.HeaderAsync(headerId)).NeedsReview);
        Assert.Equal((-42.50m, -45.00m), await feed.PromotionFiguresAsync(headerId));
    }

    /// <summary>A pending row the user split clears at the split's total: every line stays
    /// as split, the account shows the total once, and the row stays out of review.</summary>
    [Fact]
    public async Task A_split_that_clears_at_its_total_keeps_every_line_and_stays_out_of_review()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-100.00", pending: true);
        var headerId = await feed.HeaderIdAsync();
        var (food, gas) = await feed.SplitFromBankAsync(headerId, approve: true, -60.00m, -40.00m);

        await feed.SyncAsync("-100.00", pending: false);

        var header = await feed.HeaderAsync(headerId);
        Assert.False(header.IsPending);
        Assert.False(header.NeedsReview, "a split that cleared at its own total was put in review");
        var legs = await feed.LegsAsync(headerId);
        Assert.Equal(-100.00m, legs.Where(l => l.AccountId == feed.Bank).Sum(l => l.Amount));
        Assert.Equal(60.00m, legs.Single(l => l.AccountId == food).Amount);
        Assert.Equal(40.00m, legs.Single(l => l.AccountId == gas).Amount);
        Assert.Equal(-100.00m, (await feed.OriginalsAsync(headerId))!.Amount);
    }

    /// <summary>A split that clears at another figure keeps every line and the account the
    /// split's total, goes to review, and has the bank's figure on file.</summary>
    [Fact]
    public async Task A_split_that_clears_at_another_figure_keeps_its_lines_and_is_held_for_review()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-100.00", pending: true);
        var headerId = await feed.HeaderIdAsync();
        var (food, gas) = await feed.SplitFromBankAsync(headerId, approve: true, -60.00m, -40.00m);

        await feed.SyncAsync("-105.00", pending: false);

        Assert.True((await feed.HeaderAsync(headerId)).NeedsReview,
            "a split that cleared at another figure was not put in review");
        var legs = await feed.LegsAsync(headerId);
        Assert.Equal(-100.00m, legs.Where(l => l.AccountId == feed.Bank).Sum(l => l.Amount));
        Assert.Equal(60.00m, legs.Single(l => l.AccountId == food).Amount);
        Assert.Equal(40.00m, legs.Single(l => l.AccountId == gas).Amount);
        var originals = await feed.OriginalsAsync(headerId);
        Assert.Equal(-105.00m, originals!.Amount);
        Assert.False(originals.RoundedAtImport);
        Assert.Equal((-100.00m, -100.00m), await feed.PromotionFiguresAsync(headerId));
        var header = await feed.HeaderAsync(headerId);
        Assert.Equal((-105.00m, false), (header.ReviewClearedAmount, header.ReviewClearedByMatch));
        // The To review list's read carries the reason, as sent, on the split's lines.
        var row = await feed.ReviewRowAsync(headerId);
        Assert.Equal("-105.00", row.GetProperty("reviewClearedAmount").GetString());
        Assert.False(row.GetProperty("reviewClearedByMatch").GetBoolean());
    }

    /// <summary>A split made from the transfer's other side, whose first posting has no leg
    /// on the synced account: nothing is rewritten on either account, the row goes to
    /// review because the bank's side does not add up to the cleared figure, and the
    /// bank's figure is on file.</summary>
    [Fact]
    public async Task A_split_whose_first_posting_has_no_synced_leg_keeps_its_lines_and_is_restated()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-100.00", pending: true);
        var headerId = await feed.HeaderIdAsync();
        var savings = (await feed.Ledger.AddBankAccountAsync("savings")).Id;
        var groceries = (await feed.Ledger.AddCategoryAsync("groceries")).Id;
        var bankLeg = (await feed.LegsAsync(headerId)).Single(l => l.AccountId == feed.Bank).Id;
        await feed.PatchAsync(headerId, new PatchTransactionRequest
        {
            Approve = true,
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = feed.Bank,
                Items = [new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = savings, Amount = -100.00m }],
            },
        }, HttpStatusCode.OK);
        var savingsLeg = (await feed.LegsAsync(headerId)).Single(l => l.AccountId == savings).Id;
        await feed.PatchAsync(headerId, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = savings,
                Items =
                [
                    new TransactionPosting { CounterpartyAccountId = groceries, Amount = -20.00m },
                    new TransactionPosting { LegId = savingsLeg, CounterpartyAccountId = feed.Bank, Amount = 100.00m },
                ],
            },
        }, HttpStatusCode.OK);
        var split = await feed.LegsAsync(headerId);
        Assert.DoesNotContain(split, l => l.PostingIndex == 0 && l.AccountId == feed.Bank);
        var before = split.Select(l => (l.Id, l.Amount)).OrderBy(l => l.Id).ToList();

        await feed.SyncAsync("-105.00", pending: false);

        Assert.Equal(before, (await feed.LegsAsync(headerId)).Select(l => (l.Id, l.Amount)).OrderBy(l => l.Id).ToList());
        var header = await feed.HeaderAsync(headerId);
        Assert.False(header.IsPending);
        Assert.True(header.NeedsReview, "the bank's side does not add up to the cleared figure, but the row was not put in review");
        Assert.Equal(-105.00m, (await feed.OriginalsAsync(headerId))!.Amount);
        Assert.Equal((-100.00m, -100.00m), await feed.PromotionFiguresAsync(headerId));
    }

    /// <summary>A split pending row folded into a settled entry clears at its total: the
    /// loser keeps its lines, so its legs do not count the figure twice, and the winner,
    /// whose loser cleared at the figure it held, stays out of review.</summary>
    [Fact]
    public async Task A_split_merge_loser_that_clears_at_its_total_keeps_its_lines_and_leaves_its_winner()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-100.00", pending: true);
        var loser = await feed.HeaderIdAsync();
        await feed.SplitFromBankAsync(loser, approve: false, -60.00m, -40.00m);
        var winner = await feed.ManualRowAsync(-100.00m);
        await feed.PatchAsync(winner, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(loser, winner);

        await feed.SyncAsync("-100.00", pending: false);

        Assert.False((await feed.HeaderAsync(winner)).NeedsReview,
            "the split loser cleared at its own total, but the kept row was put back in review");
        var loserLegs = await feed.LegsAsync(loser);
        Assert.Equal(-100.00m, loserLegs.Where(l => l.AccountId == feed.Bank).Sum(l => l.Amount));
        Assert.Equal([-60.00m, -40.00m], loserLegs.Where(l => l.AccountId == feed.Bank).OrderBy(l => l.PostingIndex).Select(l => l.Amount));
        Assert.False((await feed.HeaderAsync(loser)).NeedsReview);
    }

    /// <summary>A split merge loser that clears at another figure keeps its lines and holds
    /// its winner for review.</summary>
    [Fact]
    public async Task A_split_merge_loser_that_clears_at_another_figure_holds_its_winner_for_review()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-100.00", pending: true);
        var loser = await feed.HeaderIdAsync();
        await feed.SplitFromBankAsync(loser, approve: false, -60.00m, -40.00m);
        var winner = await feed.ManualRowAsync(-100.00m);
        await feed.PatchAsync(winner, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(loser, winner);

        await feed.SyncAsync("-105.00", pending: false);

        Assert.True((await feed.HeaderAsync(winner)).NeedsReview,
            "the split loser cleared at another figure, but the kept row was not put back in review");
        var loserLegs = await feed.LegsAsync(loser);
        Assert.Equal([-60.00m, -40.00m], loserLegs.Where(l => l.AccountId == feed.Bank).OrderBy(l => l.PostingIndex).Select(l => l.Amount));
        var loserRow = await feed.HeaderAsync(loser);
        Assert.False(loserRow.NeedsReview, "a merge loser was put back in the review queue");
        Assert.Equal(-105.00m, (await feed.OriginalsAsync(loser))!.Amount);
    }

    // ----- why a promote-on-clear held the row (ADR-0114 D15) -----

    /// <summary>A feed-row winner rounded at its own import, approved, then held because its
    /// loser cleared at another figure: the row carries the loser's figure as the reason,
    /// beside the rounded mark left from its import, so the review list can say which.</summary>
    [Fact]
    public async Task A_winner_held_for_its_loser_carries_the_losers_figure_over_its_own_rounding()
    {
        await using var feed = await FeedAsync();
        feed.Second = ("-42.505", true);
        await feed.SyncAsync("-42.51", pending: true);
        var loser = await feed.FeedRowIdAsync("pending-1");
        var winner = await feed.FeedRowIdAsync("pending-2");
        Assert.True((await feed.OriginalsAsync(winner))!.RoundedAtImport);
        await feed.PatchAsync(winner, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(loser, winner);

        await feed.SyncAsync("-45.00", pending: false);

        var header = await feed.HeaderAsync(winner);
        Assert.True(header.NeedsReview);
        Assert.Equal((-45.00m, true), (header.ReviewClearedAmount, header.ReviewClearedByMatch));
        var row = await feed.ReviewRowAsync(winner);
        Assert.Equal("-45.00", row.GetProperty("reviewClearedAmount").GetString());
        Assert.True(row.GetProperty("reviewClearedByMatch").GetBoolean());
        Assert.True(row.GetProperty("roundedAtImport").GetBoolean());
        var loserRow = await feed.HeaderAsync(loser);
        Assert.Equal((null, false), (loserRow.ReviewClearedAmount, loserRow.ReviewClearedByMatch));
    }

    /// <summary>A winner held for its loser, whose own pending figure then clears with
    /// rounding: the rounding is the reason now, so the loser's figure is dropped.</summary>
    [Fact]
    public async Task A_later_rounding_replaces_the_cleared_figure_as_the_reason()
    {
        await using var feed = await FeedAsync();
        feed.Second = ("-42.50", true);
        await feed.SyncAsync("-42.50", pending: true);
        var loser = await feed.FeedRowIdAsync("pending-1");
        var winner = await feed.FeedRowIdAsync("pending-2");
        await feed.PatchAsync(winner, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);
        await feed.MergeAsync(loser, winner);
        await feed.SyncAsync("-45.00", pending: false);
        Assert.Equal(-45.00m, (await feed.HeaderAsync(winner)).ReviewClearedAmount);

        feed.Second = ("-42.505", false);
        await feed.SyncAsync("-45.00", pending: false);

        var header = await feed.HeaderAsync(winner);
        Assert.True(header.NeedsReview);
        Assert.Equal((null, false), (header.ReviewClearedAmount, header.ReviewClearedByMatch));
        Assert.True((await feed.OriginalsAsync(winner))!.RoundedAtImport);
    }

    /// <summary>A rounded promotion of a row no other hold explains keeps no cleared
    /// figure: the rounded note is its reason.</summary>
    [Fact]
    public async Task A_rounded_promotion_keeps_no_cleared_figure()
    {
        await using var feed = await FeedAsync();
        await feed.SyncAsync("-42.50", pending: true);
        var headerId = await feed.HeaderIdAsync();
        await feed.PatchAsync(headerId, new PatchTransactionRequest { Approve = true }, HttpStatusCode.NoContent);

        await feed.SyncAsync("-42.505", pending: false);

        var header = await feed.HeaderAsync(headerId);
        Assert.True(header.NeedsReview);
        Assert.Equal((null, false), (header.ReviewClearedAmount, header.ReviewClearedByMatch));
        var row = await feed.ReviewRowAsync(headerId);
        Assert.True(!row.TryGetProperty("reviewClearedAmount", out var figure) || figure.ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.False(row.GetProperty("reviewClearedByMatch").GetBoolean());
    }

    private sealed class Feed : IAsyncDisposable
    {
        private readonly TestDatabase _db;
        public required SyntheticLedger Ledger { get; init; }
        public required ApiFactory Factory { get; init; }
        public required HttpClient Client { get; init; }
        public required Guid ConnectionId { get; init; }
        public required Guid Bank { get; init; }
        public string Amount { get; set; } = "0";
        public bool Pending { get; set; } = true;

        /// <summary>A second feed transaction, <c>pending-2</c>, sent beside the first
        /// when set.</summary>
        public (string Amount, bool Pending)? Second { get; set; }

        public Feed(TestDatabase db) { _db = db; }

        public string Body() => $$"""
            {"connections":[
              {"conn_id":"c-P","name":"Bank P","org_id":"bankp","sfin_url":"https://sfin/bankp"}
            ],"errlist":[],"accounts":[
              {"id":"sf-p","conn_id":"c-P","name":"Checking","currency":"USD","balance":"100.00","transactions":[
                {"id":"pending-1","posted":1715000000,"amount":"{{Amount}}","payee":"Corner Cafe","description":"CORNER CAFE 0042","pending":{{(Pending ? "true" : "false")}}}{{SecondRow()}}
              ]}
            ]}
            """;

        private string SecondRow() => Second is { } second
            ? $$""",{"id":"pending-2","posted":1715000000,"amount":"{{second.Amount}}","payee":"Corner Cafe","description":"CORNER CAFE 0043","pending":{{(second.Pending ? "true" : "false")}}}"""
            : "";

        public async Task SyncAsync(string amount, bool pending)
        {
            Amount = amount;
            Pending = pending;
            var resp = await Client.PostAsync(
                $"/api/ledgers/{Ledger.LedgerId}/feed-connections/{ConnectionId}/sync", content: null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        /// <summary>Fold <paramref name="loser"/> into <paramref name="winner"/> through
        /// the merge endpoint.</summary>
        public async Task MergeAsync(Guid loser, Guid winner)
        {
            var merge = await Client.PostAsJsonAsync(
                $"/api/ledgers/{Ledger.LedgerId}/transactions/{loser}/merge",
                new MergeTransactionRequest { FromHeaderId = winner });
            Assert.True(merge.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
                await merge.Content.ReadAsStringAsync());
        }

        /// <summary>The feed row with the given provider id.</summary>
        public async Task<Guid> FeedRowIdAsync(string providerId)
        {
            await using var db = _db.NewDbContext();
            return await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == Ledger.LedgerId && h.ExternalId != null && h.ExternalId.EndsWith(providerId))
                .Select(h => h.Id).SingleAsync();
        }

        /// <summary>A manual row on the bank account split across categories, one per
        /// amount, on the feed row's posted day.</summary>
        public async Task<Guid> ManualSplitRowAsync(params decimal[] amounts)
        {
            var postings = new List<TransactionPosting>();
            for (var i = 0; i < amounts.Length; i++)
            {
                var category = await Ledger.AddCategoryAsync($"split-{i}");
                postings.Add(new TransactionPosting { CounterpartyAccountId = category.Id, Amount = amounts[i] });
            }
            return await CreateManualAsync(postings);
        }

        /// <summary>Split the feed row on the bank account into one line per amount, each
        /// to a category of its own (the first named food, the second gas), through the
        /// editor's PATCH; approved too when <paramref name="approve"/>.</summary>
        public async Task<(Guid Food, Guid Gas)> SplitFromBankAsync(Guid headerId, bool approve, decimal first, decimal second)
        {
            var food = (await Ledger.AddCategoryAsync("food")).Id;
            var gas = (await Ledger.AddCategoryAsync("gas")).Id;
            var bankLeg = (await LegsAsync(headerId)).Single(l => l.AccountId == Bank).Id;
            await PatchAsync(headerId, new PatchTransactionRequest
            {
                Approve = approve ? true : null,
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = Bank,
                    Items =
                    [
                        new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = food, Amount = first },
                        new TransactionPosting { CounterpartyAccountId = gas, Amount = second },
                    ],
                },
            }, HttpStatusCode.OK);
            Assert.Equal(!approve, (await HeaderAsync(headerId)).NeedsReview);
            return (food, gas);
        }

        /// <summary>The row the To review list draws for the header on the bank account:
        /// the entry's own row, or a split's first line.</summary>
        public async Task<System.Text.Json.JsonElement> ReviewRowAsync(Guid headerId)
        {
            var response = await Client.GetAsync(
                $"/api/ledgers/{Ledger.LedgerId}/transactions/entries?needs_review=true&limit=50");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                if (e.GetProperty("accountId").GetGuid() != Bank) continue;
                var entry = e.GetProperty("entry");
                var row = entry.GetProperty("kind").GetString() == "txn"
                    ? entry.GetProperty("txn")
                    : entry.GetProperty("legs")[0];
                if (row.GetProperty("headerId").GetGuid() == headerId) return row.Clone();
            }
            throw new Xunit.Sdk.XunitException($"no review entry for {headerId}: {body}");
        }

        /// <summary>The promotion audit's figures for the row: what the account held before
        /// and after.</summary>
        public async Task<(decimal Was, decimal Became)> PromotionFiguresAsync(Guid headerId)
        {
            var promotion = await PromotionAsync(headerId);
            return (promotion.WasAmount, promotion.BecameAmount);
        }

        public async Task PatchAsync(Guid headerId, PatchTransactionRequest request, HttpStatusCode expected)
        {
            var resp = await Client.PatchAsJsonAsync($"/api/ledgers/{Ledger.LedgerId}/transactions/{headerId}", request);
            Assert.True(resp.StatusCode == expected, await resp.Content.ReadAsStringAsync());
        }

        /// <summary>A manual row on the bank account against a category, on the feed
        /// row's posted day.</summary>
        public async Task<Guid> ManualRowAsync(decimal amount, string category = "dining")
        {
            var account = await Ledger.AddCategoryAsync(category);
            return await CreateManualAsync(
                [new TransactionPosting { CounterpartyAccountId = account.Id, Amount = amount }]);
        }

        /// <summary>A manual transfer from the bank account to <paramref name="to"/>, with a
        /// second amount, on the feed row's posted day.</summary>
        public Task<Guid> ManualTransferAsync(Guid to, decimal amount, decimal counterpartyAmount) =>
            CreateManualAsync(
                [new TransactionPosting { CounterpartyAccountId = to, Amount = amount, CounterpartyAmount = counterpartyAmount }]);

        /// <summary>The bank editor's save of a row as a transfer from the bank account to
        /// <paramref name="to"/> with a second amount, leaving it awaiting review.</summary>
        public async Task SaveAsTransferAsync(Guid headerId, Guid to, decimal amount, decimal counterpartyAmount)
        {
            var bankLeg = (await LegsAsync(headerId)).Single(l => l.AccountId == Bank).Id;
            await PatchAsync(headerId, new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = Bank,
                    Items = [new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = to, Amount = amount, CounterpartyAmount = counterpartyAmount }],
                },
            }, HttpStatusCode.OK);
            Assert.True((await HeaderAsync(headerId)).NeedsReview);
        }

        /// <summary>The account editor's currency change of <paramref name="accountId"/>.</summary>
        public Task<HttpResponseMessage> SetCurrencyAsync(Guid accountId, string currencyCode) =>
            Client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{Ledger.LedgerId}/accounts/{accountId}")
            {
                Content = JsonContent.Create(new UpdateAccountRequest { CurrencyCode = currencyCode }),
            });

        private async Task<Guid> CreateManualAsync(List<TransactionPosting> postings)
        {
            var resp = await Client.PostAsJsonAsync(
                $"/api/ledgers/{Ledger.LedgerId}/transactions",
                new CreateTransactionRequest
                {
                    PostedAt = DateTimeOffset.FromUnixTimeSeconds(1715000000).UtcDateTime,
                    Payee = "Payee A",
                    SourceAccountId = Bank,
                    Postings = postings,
                });
            Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
            return (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
                .GetProperty("headerId").GetGuid();
        }

        public async Task<Guid> HeaderIdAsync()
        {
            await using var db = _db.NewDbContext();
            return await db.TxnHeaders.AsNoTracking()
                .Where(h => h.LedgerId == Ledger.LedgerId).Select(h => h.Id).SingleAsync();
        }

        public async Task<Coffer.Api.Db.Entities.TxnHeaderRow> HeaderAsync(Guid headerId)
        {
            await using var db = _db.NewDbContext();
            return await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == headerId);
        }

        public async Task<IReadOnlyList<Coffer.Api.Db.Entities.TxnLegRow>> LegsAsync(Guid headerId)
        {
            await using var db = _db.NewDbContext();
            return await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId).ToListAsync();
        }

        public async Task<Coffer.Api.Db.Entities.TxnHeaderOriginalRow?> OriginalsAsync(Guid headerId)
        {
            await using var db = _db.NewDbContext();
            return await db.TxnHeaderOriginals.AsNoTracking().SingleOrDefaultAsync(o => o.HeaderId == headerId);
        }

        public async Task<Coffer.Api.Db.Entities.LedgerOperationPromotionRow> PromotionAsync(Guid headerId)
        {
            await using var db = _db.NewDbContext();
            return await db.LedgerOperationPromotions.AsNoTracking().SingleAsync(p => p.HeaderId == headerId);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }

    /// <summary>A ledger with a USD bank account bound to a USD feed account whose one
    /// transaction the test sets before each sync.</summary>
    private async Task<Feed> FeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var keys = Db.NewLedgerKeyService();
        await using (var seed = Db.NewDbContext())
        {
            var row = await seed.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
            row.WrappedLek = keys.CreateWrappedLek();
            row.LekKekId = keys.CurrentKekId;
            row.LekCreatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        Feed? feed = null;
        var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<SimpleFinClient>(_ => StubClient(() =>
                feed?.Body() ?? """{"connections":[],"errlist":[],"accounts":[]}"""));
        var client = await AuthedClientAsync(factory, ledger);
        var connection = (await (await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections",
                new CreateFeedConnectionRequest { SetupToken = SetupToken("https://x/c") }))
            .Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        var bank = await ledger.AddBankAccountAsync("checking");
        var mapped = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connection.Id,
                SimpleFinAccountId = "sf-p",
            }),
        });
        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);
        feed = new Feed(Db)
        {
            Ledger = ledger,
            Factory = factory,
            Client = client,
            ConnectionId = connection.Id,
            Bank = bank.Id,
        };
        return feed;
    }

    private static SimpleFinClient StubClient(Func<string> body)
    {
        const string accessUrl = "https://u:p@bridge.simplefin.org/simplefin/access/abc";
        return new SimpleFinClient(new HttpClient(new StubHandler(req =>
            req.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(accessUrl) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body()) })), TimeProvider.System);
    }

    private static string SetupToken(string claimUrl) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request));
    }
}
