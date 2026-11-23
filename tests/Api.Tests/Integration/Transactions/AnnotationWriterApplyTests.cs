using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Storing a plan on a transaction that is being created reads nothing: a header and
/// legs that are not in the database yet have no tag pairings to diff against.
/// </summary>
/// <remarks>
/// Every create path stores its plan this way, ingest once per imported row, so a read
/// here would be a round trip per row of an import.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AnnotationWriterApplyTests : DatabaseTest
{
    public AnnotationWriterApplyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_new_transactions_plan_is_stored_without_a_read()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var tagId = await ledger.AddBareTagAsync("food");

        var counter = new CommandCounter();
        await using var db = Db.NewRequestDbContextAsUser(ledger.UserId, counter);

        var header = TxnHeaderRow.NewTransaction(Guid.NewGuid(), ledger.LedgerId, bank.Id);
        header.Payee = "Market";
        header.PostedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        header.TransactedAt = header.PostedAt;
        TxnLegRow[] legs =
        [
            new() { Id = Guid.NewGuid(), HeaderId = header.Id, LedgerId = ledger.LedgerId, AccountId = bank.Id, PostingIndex = 0, Amount = -5m },
            new() { Id = Guid.NewGuid(), HeaderId = header.Id, LedgerId = ledger.LedgerId, AccountId = groceries.Id, PostingIndex = 0, Amount = 5m },
        ];
        db.TxnHeaders.Add(header);
        db.TxnLegs.AddRange(legs);
        var shape = AnnotationWriter.Shape(null, null,
            legs.Select(l => new AnnotationWriter.PlannedLeg(l.PostingIndex, l.AccountId, l.Id)),
            AnnotationWriter.AccountFacts.None);
        var plan = AnnotationPlanner.Seed(shape, new AnnotationIntent<Guid>(
            bank.Id, FieldEdit<string>.Set("weekly shop"), FieldEdit<IReadOnlyList<Guid>>.Set([tagId])));
        Assert.True(plan.IsValid);

        counter.Count = 0;
        await AnnotationWriter.ApplyAsync(db, header, legs, plan, CancellationToken.None);
        Assert.Equal(0, counter.Count);

        await db.SaveChangesUnderLedgerLockAsync(ledger.LedgerId);
        var stored = await ledger.LoadNotesAsync(header.Id);
        Assert.True(stored.Header.IsEmpty);
        Assert.Equal(new Notes<string>("weekly shop", ["food"]), stored.For(new LegKey(0, bank.Id)));
        Assert.True(stored.For(new LegKey(0, groceries.Id)).IsEmpty);
    }

    [Fact]
    public async Task Tagging_many_transactions_at_once_costs_the_round_trips_of_tagging_one()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var fuel = await ledger.AddCategoryAsync("Fuel");
        var headers = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            // One-splits and splits, some already tagged, so every kind of diff runs.
            Guid id;
            if (i % 2 == 0)
                id = await ledger.ResolveHeaderIdAsync((await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -5m - i, Day(i))).FromTxnId);
            else
                (_, id) = await ledger.AddMultiSplitAsync(bank.Id, [(groceries.Id, -2m), (fuel.Id, -3m - i)], Day(i));
            if (i % 3 == 0) await ledger.SetSideTagsAsync(id, bank.Id, ["old"], bank.Id);
            headers.Add(id);
        }

        async Task<int> CommandsToTagAsync(IReadOnlyList<Guid> ids)
        {
            // Wired as a request is: the count includes the save's recompute of the stored
            // notes, which is batched too, so it does not grow with the rows either.
            var counter = new CommandCounter();
            await using var db = Db.NewRequestDbContextAsUser(ledger.UserId, counter);
            var outcome = await new Coffer.Api.Db.Repositories.TransactionsRepository(db, TimeProvider.System)
                .SetTransactionTagsAsync(ledger.LedgerId, ids, bank.Id, ["new"], dryRun: false);
            Assert.Equal(Coffer.Api.Db.Repositories.TransactionsRepository.SetTagsResult.Ok, outcome.Result);
            return counter.Count;
        }

        var forTwo = await CommandsToTagAsync(headers.Take(2).ToList());
        var forSix = await CommandsToTagAsync(headers);

        Assert.Equal(forTwo, forSix);
        // Every one counts the new set from the bank's register: a one-split's own, a
        // split's whole-transaction set.
        foreach (var id in headers)
            Assert.All(await ledger.ShownForAsync(id, bank.Id), s => Assert.Equal(["new"], s.InPlayTags));
    }

    private static DateTime Day(int i) => new(2026, 3, 1 + i, 12, 0, 0, DateTimeKind.Utc);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
