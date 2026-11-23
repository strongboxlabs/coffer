using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Every writer of a header sets or hands over <c>originating_account_id</c> (migration
/// 263), and leaves the ledger inside its invariants.
/// </summary>
/// <remarks>
/// <para>The owner is the account a transaction was entered from, and a reshape hands
/// it to the side that does it: a split made from the other side, an event made from a
/// bank-shape row, an event moved to another brokerage. A reminder's template stays with
/// the source it was created from, since an edit cannot change that source. A repoint never changes it while the owner keeps a leg, and follows the
/// legs when it does not — recategorize, category merge, split-line recategorize, move
/// and bulk move, including the move that clears an action off a cash transfer.</para>
///
/// <para>Each test ends by checking the invariants over its whole ledger
/// (<see cref="OriginatingAccountCheck"/>), so a writer that stamps the right header
/// and breaks another is caught too.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class OriginatingAccountWriterTests : DatabaseTest
{
    public OriginatingAccountWriterTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int month, int day) => new(2026, month, day, 12, 0, 0, DateTimeKind.Utc);

    // ----- bank -----

    [Fact]
    public async Task Bank_create_is_owned_by_the_register_it_is_entered_from()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var a = await ledger.AddBankAccountAsync("Account A");
        var b = await ledger.AddBankAccountAsync("Account B");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var fromA = await CreateBankAsync(client, ledger, a.Id, (category.Id, -12m));
        // A transfer, entered from either of its sides.
        var transferFromA = await CreateBankAsync(client, ledger, a.Id, (b.Id, -20m));
        var transferFromB = await CreateBankAsync(client, ledger, b.Id, (a.Id, 20m));
        var split = await CreateBankAsync(client, ledger, a.Id, (category.Id, -5m), (category.Id, -7m));

        Assert.Equal(a.Id, await OwnerAsync(fromA));
        Assert.Equal(a.Id, await OwnerAsync(transferFromA));
        Assert.Equal(b.Id, await OwnerAsync(transferFromB));
        Assert.Equal(a.Id, await OwnerAsync(split));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Splitting_a_one_split_from_its_other_side_hands_it_to_that_side()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var a = await ledger.AddBankAccountAsync("Account A");
        var b = await ledger.AddBankAccountAsync("Account B");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Entered from A; B is the other side.
        var header = await CreateBankAsync(client, ledger, a.Id, (b.Id, -60m));
        Assert.Equal(a.Id, await OwnerAsync(header));

        var bLeg = await LegIdAsync(header, b.Id);
        await PatchPostingsAsync(client, ledger, header, b.Id,
            new TransactionPosting { LegId = bLeg, CounterpartyAccountId = a.Id, Amount = 60m },
            new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -10m });

        Assert.Equal(b.Id, await OwnerAsync(header));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Reshaping_a_split_from_its_own_side_keeps_the_owner()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var c1 = await ledger.AddCategoryAsync("Category A");
        var c2 = await ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var header = await CreateBankAsync(client, ledger, bank.Id, (c1.Id, -5m), (c2.Id, -7m));
        var first = (await SourceLegIdsAsync(header, bank.Id))[0];
        // Collapse to one line.
        await PatchPostingsAsync(client, ledger, header, bank.Id,
            new TransactionPosting { LegId = first, CounterpartyAccountId = c1.Id, Amount = -12m });

        Assert.Equal(bank.Id, await OwnerAsync(header));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Recategorizing_a_one_split_entered_from_its_category_follows_the_leg()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var from = await ledger.AddCategoryAsync("Category A");
        var to = await ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Owned by its category: the state a Moneydance row homed on the category
        // has (the API enters from real accounts only).
        var ownedByCategory = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(from.Id, bank.Id, 25m, Utc(6, 2))).FromTxnId);
        var ownedByBank = await CreateBankAsync(client, ledger, bank.Id, (from.Id, -25m));

        // A context wired as production wires it: the recategorize is a tracked save, whose
        // derived state (the stored memo-and-tags presentation among it) the interceptors write.
        await using (var db = Db.NewServiceFactory().Create())
        {
            var repo = new TransactionsRepository(db, TimeProvider.System);
            Assert.Equal(TransactionsRepository.RecategorizeResult.Ok,
                (await repo.RecategorizeAsync(ledger.LedgerId, ownedByCategory, to.Id, dryRun: false)).Result);
            Assert.Equal(TransactionsRepository.RecategorizeResult.Ok,
                (await repo.RecategorizeAsync(ledger.LedgerId, ownedByBank, to.Id, dryRun: false)).Result);
        }

        Assert.Equal(to.Id, await OwnerAsync(ownedByCategory));
        Assert.Equal(bank.Id, await OwnerAsync(ownedByBank));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Recategorizing_the_lines_of_a_split_owned_by_its_category_follows_them()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var from = await ledger.AddCategoryAsync("Category A");
        var to = await ledger.AddCategoryAsync("Category B");

        // Every line between the category and the bank, owned by the category (a
        // Moneydance split homed on it).
        var (_, header) = await ledger.AddMultiSplitAsync(from.Id, [(bank.Id, 5m), (bank.Id, 7m)], Utc(6, 2));
        Assert.Equal(from.Id, await OwnerAsync(header));

        // Wired as production wires it, for the same reason as above.
        await using (var db = Db.NewServiceFactory().Create())
        {
            var outcome = await new TransactionsRepository(db, TimeProvider.System)
                .RecategorizeSplitPostingsAsync(ledger.LedgerId, header, from.Id, to.Id, dryRun: false);
            Assert.Equal(TransactionsRepository.SplitPostingRecategorizeResult.Ok, outcome.Result);
        }

        Assert.Equal(to.Id, await OwnerAsync(header));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Merging_a_category_hands_what_it_owned_to_the_target_and_nothing_else()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var source = await ledger.AddCategoryAsync("Category A");
        var target = await ledger.AddCategoryAsync("Category B");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var ownedBySource = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(source.Id, bank.Id, 9m, Utc(6, 2))).FromTxnId);
        var ownedByBank = await CreateBankAsync(client, ledger, bank.Id, (source.Id, -9m));
        // Each owned by one side, with an own value on each side of one of them.
        await ledger.SetSideMemoAsync(ownedBySource, source.Id, "Memo A", source.Id);
        await ledger.SetSideTagsAsync(ownedBySource, source.Id, ["Tag A"], source.Id);
        await ledger.SetSideMemoAsync(ownedByBank, bank.Id, "Memo B", bank.Id);
        await ledger.SetSideTagsAsync(ownedByBank, bank.Id, ["Tag B"], bank.Id);
        await ledger.SetSideMemoAsync(ownedByBank, source.Id, "Memo C", bank.Id);
        var before = await ShownOnEachSideAsync(ledger, bank.Id, source.Id, ownedBySource, ownedByBank);

        await using (var db = Db.NewDbContext())
        {
            var outcome = await new AccountsRepository(db, new LegDerivedRecomputeService(db), TimeProvider.System)
                .MergeCategoryAsync(ledger.LedgerId, source.Id, target.Id, dryRun: false);
            Assert.Equal(AccountsRepository.MergeCategoryResult.Ok, outcome.Result);
        }

        Assert.Equal(target.Id, await OwnerAsync(ownedBySource));
        Assert.Equal(bank.Id, await OwnerAsync(ownedByBank));
        // The bank shows what it did; the target shows what the merged category did.
        Assert.Equal(before, await ShownOnEachSideAsync(ledger, bank.Id, target.Id, ownedBySource, ownedByBank));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    /// <summary>What <paramref name="kept"/> and <paramref name="moved"/> show for each
    /// one-split, through the one presentation rule; compared before and after a
    /// repoint, with <paramref name="moved"/> the account the legs moved off, then onto.</summary>
    private static async Task<List<(string? Kept, string? Moved)>> ShownOnEachSideAsync(
        SyntheticLedger ledger, Guid kept, Guid moved, params Guid[] headers)
    {
        var shown = new List<(string?, string?)>();
        foreach (var header in headers)
            shown.Add((Shown(await ledger.ShownForAsync(header, kept)), Shown(await ledger.ShownForAsync(header, moved))));
        return shown;
    }

    [Fact]
    public async Task Moving_a_transaction_hands_it_over_only_when_its_owner_was_moved()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var a = await ledger.AddBankAccountAsync("Account A");
        var b = await ledger.AddBankAccountAsync("Account B");
        var c = await ledger.AddBankAccountAsync("Account C");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var ownerMoved = await CreateBankAsync(client, ledger, a.Id, (category.Id, -3m));
        var otherSideMoved = await CreateBankAsync(client, ledger, a.Id, (b.Id, -4m));
        foreach (var header in new[] { ownerMoved, otherSideMoved })
        {
            await ledger.SetSideMemoAsync(header, a.Id, "Memo A", a.Id);
            await ledger.SetSideTagsAsync(header, a.Id, ["Tag A"], a.Id);
        }
        await ledger.SetSideMemoAsync(otherSideMoved, b.Id, "Memo B", a.Id);
        var ownerMovedBefore = await ShownOnEachSideAsync(ledger, category.Id, a.Id, ownerMoved);
        var otherSideBefore = await ShownOnEachSideAsync(ledger, a.Id, b.Id, otherSideMoved);

        await MoveAsync(client, ledger, ownerMoved, a.Id, c.Id);
        await MoveAsync(client, ledger, otherSideMoved, b.Id, c.Id);

        Assert.Equal(c.Id, await OwnerAsync(ownerMoved));
        Assert.Equal(a.Id, await OwnerAsync(otherSideMoved));
        // The side that stayed shows what it did; the target shows what the moved side did.
        Assert.Equal(ownerMovedBefore, await ShownOnEachSideAsync(ledger, category.Id, c.Id, ownerMoved));
        Assert.Equal(otherSideBefore, await ShownOnEachSideAsync(ledger, a.Id, c.Id, otherSideMoved));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Bulk_moving_a_selection_hands_over_what_the_source_owned()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var a = await ledger.AddBankAccountAsync("Account A");
        var b = await ledger.AddBankAccountAsync("Account B");
        var c = await ledger.AddBankAccountAsync("Account C");
        var category = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var ownedByA = await CreateBankAsync(client, ledger, a.Id, (category.Id, -3m));
        var ownedByB = await CreateBankAsync(client, ledger, b.Id, (a.Id, 4m));
        await ledger.SetSideMemoAsync(ownedByA, a.Id, "Memo A", a.Id);
        await ledger.SetSideTagsAsync(ownedByA, a.Id, ["Tag A"], a.Id);
        await ledger.SetSideMemoAsync(ownedByA, category.Id, "Memo C", a.Id);
        await ledger.SetSideMemoAsync(ownedByB, b.Id, "Memo B", b.Id);
        await ledger.SetSideTagsAsync(ownedByB, b.Id, ["Tag B"], b.Id);
        var ownedByABefore = await ShownOnEachSideAsync(ledger, category.Id, a.Id, ownedByA);
        var ownedByBBefore = await ShownOnEachSideAsync(ledger, b.Id, a.Id, ownedByB);

        await BulkMoveAsync(client, ledger, a.Id, c.Id, ownedByA, ownedByB);

        Assert.Equal(c.Id, await OwnerAsync(ownedByA));
        Assert.Equal(b.Id, await OwnerAsync(ownedByB));
        Assert.Equal(ownedByABefore, await ShownOnEachSideAsync(ledger, category.Id, c.Id, ownedByA));
        Assert.Equal(ownedByBBefore, await ShownOnEachSideAsync(ledger, b.Id, c.Id, ownedByB));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Moving_a_cash_transfer_off_its_brokerage_clears_the_action_and_hands_it_to_the_bank()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var checking = await ledger.AddBankAccountAsync("Account A");
        var savings = await ledger.AddBankAccountAsync("Account B");
        var other = await ledger.AddBankAccountAsync("Account C");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var single = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, Action = "transfer", TransferAccountId = checking.Id,
            Amount = 100m, PostedAt = Utc(3, 1), Payee = "Payee A", Memo = "Memo A",
        });
        var bulk = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, Action = "transfer", TransferAccountId = other.Id,
            Amount = 50m, PostedAt = Utc(3, 2), Payee = "Payee B", Memo = "Memo B",
        });
        Assert.Equal(brokerage.Id, await OwnerAsync(single));

        // What each register shows before: the bank side, and the brokerage side. The
        // memo and tags are read through the one presentation rule, which the
        // register readers move to; the rest through the register itself.
        var before = new Dictionary<Guid, RegisterRowDto>();
        var notesBefore = new Dictionary<Guid, (string? Bank, string? Brokerage)>();
        foreach (var (header, bank) in new[] { (single, checking.Id), (bulk, other.Id) })
        {
            before[header] = Assert.Single(await RegisterRowsAsync(client, ledger, bank, header));
            Assert.Single(await RegisterRowsAsync(client, ledger, brokerage.Id, header));
            notesBefore[header] = (Shown(await ledger.ShownForAsync(header, bank)),
                Shown(await ledger.ShownForAsync(header, brokerage.Id)));
        }

        await MoveAsync(client, ledger, single, brokerage.Id, savings.Id);
        await BulkMoveAsync(client, ledger, brokerage.Id, savings.Id, bulk);

        foreach (var (header, bank) in new[] { (single, checking.Id), (bulk, other.Id) })
        {
            Assert.Null(await ActionAsync(header));
            Assert.Equal(savings.Id, await OwnerAsync(header));

            // The untouched bank's row reads as before, now facing the target bank; the
            // target shows the moved side with the brokerage's amount; the brokerage
            // shows nothing.
            var kept = Assert.Single(await RegisterRowsAsync(client, ledger, bank, header));
            Assert.Equal(Display(before[header]), Display(kept));
            Assert.Equal(savings.Id, kept.CounterpartyAccountId);
            var moved = Assert.Single(await RegisterRowsAsync(client, ledger, savings.Id, header));
            Assert.Equal(Display(before[header]) with { Amount = -before[header].Amount }, Display(moved));
            Assert.Equal(bank, moved.CounterpartyAccountId);
            Assert.Empty(await RegisterRowsAsync(client, ledger, brokerage.Id, header));

            // The untouched bank shows the same memo and tags; the target bank shows
            // what the brokerage showed, now as its own.
            Assert.Equal(notesBefore[header].Bank, Shown(await ledger.ShownForAsync(header, bank)));
            Assert.Equal(notesBefore[header].Brokerage, Shown(await ledger.ShownForAsync(header, savings.Id)));
        }
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    private static string? Shown(IReadOnlyList<Coffer.Domain.Annotations.ShownNotes<string>> rows)
    {
        var row = Assert.Single(rows);
        return $"{row.Memo}|{string.Join(",", row.Tags)}";
    }

    /// <summary>A register row's own fields; its memo and tags are compared through
    /// the presentation rule (see the move test).</summary>
    private sealed record RowDisplay(string? Payee, decimal Amount, DateTime PostedAt, string Status);

    private static RowDisplay Display(RegisterRowDto r) =>
        new(r.Payee, r.Amount, r.PostedAt, r.Status);

    private static async Task<List<RegisterRowDto>> RegisterRowsAsync(
        HttpClient client, SyntheticLedger ledger, Guid accountId, Guid headerId)
    {
        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={accountId}&limit=100"))!;
        return page.Entries
            .SelectMany(e => e.Txn is { } t
                ? new[] { t }
                : (IEnumerable<RegisterRowDto>)(e.Legs ?? Array.Empty<RegisterRowDto>()))
            .Where(r => r.HeaderId == headerId)
            .ToList();
    }

    // ----- investment -----

    [Fact]
    public async Task Investment_create_is_owned_by_its_brokerage_and_an_in_kind_transfer_by_its_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var dest = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var funding = await ledger.AddInvestmentAccountAsync("Brokerage C");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");
        var fee = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A buy funded from another brokerage, with a fee: only the event's own
        // brokerage is on every posting.
        var buy = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = source.Id, Action = "buyx", SecurityId = security, Shares = 10m, Price = 20m,
            TransferAccountId = funding.Id, FeeAccountId = fee.Id, FeeAmount = 3m, PostedAt = Utc(1, 5),
        });
        var transfer = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = source.Id, Action = "transfer_shares", SecurityId = security, Shares = 4m,
            TransferAccountId = dest.Id, PostedAt = Utc(2, 5),
        });

        Assert.Equal(source.Id, await OwnerAsync(buy));
        Assert.Equal(source.Id, await OwnerAsync(transfer));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Turning_a_bank_side_import_into_an_event_hands_it_to_the_brokerage()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var bank = await ledger.AddBankAccountAsync("Account A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // An imported transfer from the bank into the brokerage, entered on the bank
        // side; the investment editor turns it into an Xfr.
        var headerId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            var header = TxnHeaderRow.NewIngested(headerId, ledger.LedgerId, bank.Id,
                origin: "file_import", externalId: $"imp-{headerId:N}");
            header.ProviderKey = "qif";
            header.Payee = "Payee A";
            header.PostedAt = Utc(4, 1);
            header.TransactedAt = Utc(4, 1);
            db.TxnHeaders.Add(header);
            db.TxnLegs.AddRange(
                Leg(headerId, ledger.LedgerId, bank.Id, -100m),
                Leg(headerId, ledger.LedgerId, brokerage.Id, 100m));
            await db.SaveChangesAsync();
        }
        // Written past every writer: store the presentation as the upgrade's backfill does.
        await ledger.RecomputeNotesAsync(headerId);

        await PatchInvestmentAsync(client, ledger, headerId, new PatchInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, Action = "transfer", TransferAccountId = bank.Id,
            Amount = 100m, PostedAt = Utc(4, 1),
        });

        Assert.Equal("transfer", await ActionAsync(headerId));
        Assert.Equal(brokerage.Id, await OwnerAsync(headerId));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Turning_a_hinted_feed_row_into_an_event_keeps_it_with_its_brokerage()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var bank = await ledger.AddBankAccountAsync("Account A");
        var uncategorized = await ledger.AddCategoryAsync("Category A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A brokerage feed row the sync classified as a transfer, imported into the
        // brokerage against a category, as the orchestrator writes it.
        var headerId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            var header = TxnHeaderRow.NewIngested(headerId, ledger.LedgerId, brokerage.Id,
                origin: "online_import", externalId: $"feed-{headerId:N}");
            header.ProviderKey = "simplefin";
            header.IngestActionHint = "transfer";
            header.Payee = "Payee A";
            header.PostedAt = Utc(4, 2);
            header.TransactedAt = Utc(4, 2);
            db.TxnHeaders.Add(header);
            db.TxnLegs.AddRange(
                Leg(headerId, ledger.LedgerId, brokerage.Id, 100m),
                Leg(headerId, ledger.LedgerId, uncategorized.Id, -100m));
            await db.SaveChangesAsync();
        }

        await PatchInvestmentAsync(client, ledger, headerId, new PatchInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, Action = "transfer", TransferAccountId = bank.Id,
            Amount = 100m, PostedAt = Utc(4, 2),
        });

        Assert.Equal("transfer", await ActionAsync(headerId));
        Assert.Equal(brokerage.Id, await OwnerAsync(headerId));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Turning_a_Moneydance_bank_home_row_into_an_event_hands_it_to_the_brokerage()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var bank = await ledger.AddBankAccountAsync("Account A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A Moneydance transfer from the bank into the brokerage, homed on the bank and
        // downloaded there: the importer's row, owned by its home. The API entity has no
        // import-source field, so the row is written as the importer writes it.
        var headerId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            var externalId = $"md-{headerId:N}";
            var postedAt = Utc(4, 3);
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO txn_headers (id, ledger_id, origin, provider_key, external_id, import_source,
                                         provider_raw_payload, payee, posted_at, transacted_at,
                                         originating_account_id)
                VALUES ({{headerId}}, {{ledger.LedgerId}}, 'online_import', 'ofx', {{externalId}},
                        'moneydance_export', '{"acctid": "md-bank"}'::jsonb, 'Payee A', {{postedAt}}, {{postedAt}},
                        {{bank.Id}})
                """);
            db.TxnLegs.AddRange(
                Leg(headerId, ledger.LedgerId, bank.Id, -100m),
                Leg(headerId, ledger.LedgerId, brokerage.Id, 100m));
            await db.SaveChangesAsync();
        }
        // Written past every writer: store the presentation as the upgrade's backfill does.
        await ledger.RecomputeNotesAsync(headerId);

        await PatchInvestmentAsync(client, ledger, headerId, new PatchInvestmentTransactionRequest
        {
            BrokerageAccountId = brokerage.Id, Action = "transfer", TransferAccountId = bank.Id,
            Amount = 100m, PostedAt = Utc(4, 3),
        });

        Assert.Equal("transfer", await ActionAsync(headerId));
        Assert.Equal(brokerage.Id, await OwnerAsync(headerId));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Converting_a_sell_and_buy_into_an_in_kind_transfer_is_owned_by_the_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var source = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var dest = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A transfer recorded as a sale in the source and a purchase in the destination.
        await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = source.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 10m,
            PostedAt = Utc(1, 5),
        });
        var sell = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = source.Id, Action = "sell", SecurityId = security, Shares = -10m, Price = 15m,
            PostedAt = Utc(6, 1),
        });
        var buy = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = dest.Id, Action = "buy", SecurityId = security, Shares = 10m, Price = 15m,
            PostedAt = Utc(6, 1),
        });

        var converted = await PostAsync<CreateInvestmentTransactionResponse>(client,
            $"/api/ledgers/{ledger.LedgerId}/in-kind-transfers/convert",
            new ConvertInKindTransferRequest { SellHeaderId = sell, BuyHeaderId = buy });

        Assert.Equal("transfer_shares", await ActionAsync(converted.HeaderId));
        Assert.Equal(source.Id, await OwnerAsync(converted.HeaderId));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Moving_an_event_to_another_brokerage_hands_it_over()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var first = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var second = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var bank = await ledger.AddBankAccountAsync("Account A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var header = await CreateInvestmentAsync(client, ledger, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = first.Id, Action = "transfer", TransferAccountId = bank.Id,
            Amount = 40m, PostedAt = Utc(5, 1),
        });
        await PatchInvestmentAsync(client, ledger, header, new PatchInvestmentTransactionRequest
        {
            BrokerageAccountId = second.Id, Action = "transfer", TransferAccountId = bank.Id,
            Amount = 40m, PostedAt = Utc(5, 1),
        });

        Assert.Equal(second.Id, await OwnerAsync(header));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    // ----- reminders -----

    [Fact]
    public async Task Reminder_templates_and_their_occurrences_are_owned_by_the_source_they_are_posted_from()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var otherBank = await ledger.AddBankAccountAsync("Account B");
        var category = await ledger.AddCategoryAsync("Category A");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var bankReminder = await CreateBankReminderAsync(client, ledger, bank.Id, category.Id);
        var investmentReminder = await PostAsync<ReminderDetail>(client,
            $"/api/ledgers/{ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1),
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = brokerage.Id, Action = "transfer", TransferAccountId = bank.Id,
                    Amount = 75m, PostedAt = Utc(1, 1),
                },
            });
        Assert.Equal(bank.Id, await TemplateOwnerAsync(bankReminder.Id));
        Assert.Equal(brokerage.Id, await TemplateOwnerAsync(investmentReminder.Id));

        // The auto-post path clones the template: the template's owner.
        var fired = await ReminderCloneFire.PostedAsync(
            Db, ledger.LedgerId, bankReminder.Id, new DateOnly(2026, 1, 1));
        Assert.Equal(bank.Id, await OwnerAsync(fired));

        // Posted by hand from another source: that source.
        var postedFromOther = await PostAsync<FireReminderResponse>(client,
            $"/api/ledgers/{ledger.LedgerId}/reminders/{bankReminder.Id}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = new DateOnly(2026, 2, 1), SourceAccountId = otherBank.Id,
                Postings = [new TransactionPosting { CounterpartyAccountId = category.Id, Amount = -30m }],
            });
        Assert.Equal(otherBank.Id, await OwnerAsync(postedFromOther.HeaderId));
        Assert.Equal(bank.Id, await TemplateOwnerAsync(bankReminder.Id));

        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task An_investment_reminder_posted_from_another_brokerage_is_that_brokerages_and_the_template_stays()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var first = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var second = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var bank = await ledger.AddBankAccountAsync("Account A");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        CreateInvestmentTransactionRequest TransferFrom(Guid brokerage) => new()
        {
            BrokerageAccountId = brokerage, Action = "transfer", TransferAccountId = bank.Id,
            Amount = 75m, PostedAt = Utc(1, 1),
        };
        var reminder = await PostAsync<ReminderDetail>(client,
            $"/api/ledgers/{ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1),
                Transaction = TransferFrom(first.Id),
            });
        Assert.Equal(first.Id, await TemplateOwnerAsync(reminder.Id));

        // Posted by hand from the other brokerage: the occurrence is that brokerage's,
        // and the template stays with its own.
        var posted = await PostAsync<FireReminderResponse>(client,
            $"/api/ledgers/{ledger.LedgerId}/reminders/{reminder.Id}/fire/investment",
            new FireInvestmentReminderRequest { OccurrenceDate = new DateOnly(2026, 1, 1), Transaction = TransferFrom(second.Id) });
        Assert.Equal(second.Id, await OwnerAsync(posted.HeaderId));
        Assert.Equal(first.Id, await TemplateOwnerAsync(reminder.Id));

        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task A_managed_loan_reminder_is_owned_by_the_paying_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("Account A");
        var interest = await ledger.AddCategoryAsync("Category A");
        var escrow = await ledger.AddBankAccountAsync("Account B");
        var loanId = Guid.NewGuid();
        await using (var db = Db.NewDbContext())
        {
            db.Accounts.Add(new AccountRow
            {
                Id = loanId, LedgerId = ledger.LedgerId, Name = "Account C",
                AccountType = "loan", CurrencyCode = "USD", OpeningBalance = -100000m, IsActive = true,
            });
            await db.SaveChangesAsync();
            db.LoanTerms.Add(new LoanTermsRow
            {
                AccountId = loanId, LedgerId = ledger.LedgerId,
                OriginalPrincipal = 100000m, AnnualInterestRate = 4.00m, Points = 0m,
                PaymentCount = 360, PaymentsPerYear = 12,
                EscrowAmount = 100m, InterestAccountId = interest.Id, EscrowAccountId = escrow.Id,
                PaymentIsComputed = true, FixedPayment = null,
            });
            await db.SaveChangesAsync();
        }
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var setup = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/accounts/{loanId}/payment-reminder",
            new SetupPaymentReminderRequest(checking.Id, new DateOnly(2026, 4, 10)));
        Assert.True(setup.IsSuccessStatusCode, await setup.Content.ReadAsStringAsync());
        var reminderId = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reminderId").GetGuid();

        Assert.Equal(checking.Id, await TemplateOwnerAsync(reminderId));
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    [Fact]
    public async Task Firing_a_reminder_whose_template_has_no_lines_is_refused_and_writes_nothing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var templateId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var nextDue = new DateOnly(2026, 2, 1);
        await using (var db = Db.NewDbContext())
        {
            // A Moneydance reminder with no field values: a template without lines, and
            // so without an owner. No factory builds that, hence SQL.
            await TxnHeaderSeed.InsertLeglessHeaderAsync(db, templateId, ledger.LedgerId,
                $"mdreminder:{seriesId:N}", "Payee A", null, Utc(1, 1), isTemplate: true);
            db.RecurringTransactions.Add(new RecurringTransactionRow
            {
                Id = seriesId, LedgerId = ledger.LedgerId, ExternalId = $"rem-{seriesId:N}",
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", TemplateHeaderId = templateId,
                StartDate = new DateOnly(2026, 1, 1), NextDueDate = nextDue, IsActive = true,
                Origin = "moneydance_import",
            });
            await db.SaveChangesAsync();
        }

        // A refusal the auto-post job can set aside and name, not an exception.
        var refused = await ReminderCloneFire.FireAsync(
            Db, ledger.LedgerId, seriesId, new DateOnly(2026, 2, 1));
        Assert.Equal(RemindersRepository.FireOutcome.TemplateHasNoLines, refused.Outcome);
        Assert.Null(refused.HeaderId);

        await using (var db = Db.NewDbContext())
        {
            Assert.False(await db.TxnHeaders.AsNoTracking().AnyAsync(h => h.RecurringTransactionId == seriesId),
                "the refused fire committed an occurrence");
            Assert.False(await db.RecurringOccurrenceExceptions.AsNoTracking()
                .AnyAsync(e => e.RecurringTransactionId == seriesId), "the refused fire skipped an occurrence");
            Assert.Equal(nextDue, await db.RecurringTransactions.AsNoTracking()
                .Where(r => r.Id == seriesId).Select(r => r.NextDueDate).SingleAsync());
        }
        await AssertInvariantsAsync(ledger.LedgerId);
    }

    // ----- helpers -----

    private static TxnLegRow Leg(Guid headerId, Guid ledgerId, Guid accountId, decimal amount, int posting = 0) => new()
    {
        Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId,
        AccountId = accountId, PostingIndex = posting, Amount = amount,
    };

    private async Task<Guid> CreateBankAsync(
        HttpClient client, SyntheticLedger ledger, Guid sourceAccountId,
        params (Guid Counterparty, decimal Amount)[] postings)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Utc(6, 1),
                SourceAccountId = sourceAccountId,
                Postings = postings.Select(p => new TransactionPosting
                {
                    CounterpartyAccountId = p.Counterparty, Amount = p.Amount,
                }).ToArray(),
            });
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task PatchPostingsAsync(
        HttpClient client, SyntheticLedger ledger, Guid headerId, Guid sourceAccountId,
        params TransactionPosting[] items)
    {
        var resp = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{headerId}",
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings { SourceAccountId = sourceAccountId, Items = items },
            });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> CreateInvestmentAsync(
        HttpClient client, SyntheticLedger ledger, CreateInvestmentTransactionRequest request)
    {
        var resp = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/investment-transactions", request);
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task PatchInvestmentAsync(
        HttpClient client, SyntheticLedger ledger, Guid headerId, PatchInvestmentTransactionRequest request)
    {
        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}", request);
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>One transaction moved: a bulk move of an explicit selection of one.</summary>
    private static Task MoveAsync(HttpClient client, SyntheticLedger ledger, Guid headerId, Guid from, Guid to) =>
        BulkMoveAsync(client, ledger, from, to, headerId);

    private static async Task BulkMoveAsync(
        HttpClient client, SyntheticLedger ledger, Guid from, Guid to, params Guid[] headerIds)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/transactions/bulk-move-account",
            new
            {
                selection = new
                {
                    kind = "explicit",
                    accountId = from,
                    statusFilter = "all",
                    selectedAt = DateTime.UtcNow.AddMinutes(1),
                    excludeIds = Array.Empty<Guid>(),
                    headerIds,
                },
                targetAccountId = to,
            });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static Task<ReminderDetail> CreateBankReminderAsync(
        HttpClient client, SyntheticLedger ledger, Guid sourceId, Guid categoryId) =>
        PostAsync<ReminderDetail>(client, $"/api/ledgers/{ledger.LedgerId}/reminders",
            new CreateReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = new DateOnly(2026, 1, 1),
                Payee = "Payee A", SourceAccountId = sourceId,
                Postings = [new TransactionPosting { CounterpartyAccountId = categoryId, Amount = -30m }],
            });

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var resp = await client.PostAsJsonAsync(url, body);
        Assert.True(resp.IsSuccessStatusCode, $"{url}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Guid?> OwnerAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == headerId).Select(h => h.OriginatingAccountId).SingleAsync();
    }

    private async Task<Guid?> TemplateOwnerAsync(Guid reminderId)
    {
        await using var db = Db.NewDbContext();
        var templateId = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == reminderId).Select(r => r.TemplateHeaderId).SingleAsync();
        return await OwnerAsync(templateId!.Value);
    }

    private async Task<string?> ActionAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking().Where(h => h.Id == headerId).Select(h => h.Action).SingleAsync();
    }

    private async Task<Guid> LegIdAsync(Guid headerId, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == accountId).Select(l => l.Id).SingleAsync();
    }

    private async Task<List<Guid>> SourceLegIdsAsync(Guid headerId, Guid accountId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == accountId)
            .OrderBy(l => l.PostingIndex).Select(l => l.Id).ToListAsync();
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private Task AssertInvariantsAsync(Guid ledgerId) => OriginatingAccountCheck.AssertHoldsAsync(Db, ledgerId);
}
