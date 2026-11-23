using System.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// <see cref="ReadSnapshot"/>, the one way a read of several statements takes one state.
/// On a context with no transaction it opens a REPEATABLE READ one, which disposing rolls
/// back; on a context inside one, it joins it and leaves it to its owner.
/// </summary>
/// <remarks>
/// A tag is the probe: a row of its own table, written through EF, which no recompute
/// reads.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReadSnapshotTests : DatabaseTest
{
    public ReadSnapshotTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task With_no_transaction_open_it_reads_one_state_and_rolls_back_when_disposed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewDbContext();
        Assert.Null(db.Database.CurrentTransaction);

        List<string> seen;
        await using (var snapshot = await ReadSnapshot.BeginAsync(db, CancellationToken.None))
        {
            Assert.NotNull(snapshot);
            Assert.Same(snapshot, db.Database.CurrentTransaction);
            Assert.Equal(IsolationLevel.RepeatableRead, snapshot.GetDbTransaction().IsolationLevel);

            // The first read fixes the state: a write another connection commits after it
            // is not seen by the next.
            seen = await TagNamesAsync(db, ledger);
            await using (var other = Db.NewDbContext())
                await AddTagAsync(other, ledger, "committed alongside");
            Assert.Equal(seen, await TagNamesAsync(db, ledger));

            // A write made inside it is seen inside it...
            await AddTagAsync(db, ledger, "written inside");
            Assert.Contains("written inside", await TagNamesAsync(db, ledger));
        }

        // ...and rolled back with it, leaving the context with no transaction.
        Assert.Null(db.Database.CurrentTransaction);
        await using var after = Db.NewDbContext();
        Assert.Equal([.. seen.Append("committed alongside").Order(StringComparer.Ordinal)],
            await TagNamesAsync(after, ledger));
    }

    [Fact]
    public async Task Inside_an_open_transaction_it_joins_it_and_leaves_it_open()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewDbContext();
        await using var owner = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await AddTagAsync(db, ledger, "written by the owner");

        await using (var snapshot = await ReadSnapshot.BeginAsync(db, CancellationToken.None))
        {
            Assert.Null(snapshot);
            Assert.Same(owner, db.Database.CurrentTransaction);
            // The read is the owner's: its write, not yet committed, is there.
            Assert.Contains("written by the owner", await TagNamesAsync(db, ledger));
        }

        // Disposing the joined read ended nothing: the owner's transaction is still open,
        // and its write commits with it.
        Assert.Same(owner, db.Database.CurrentTransaction);
        await owner.CommitAsync();
        await using var after = Db.NewDbContext();
        Assert.Contains("written by the owner", await TagNamesAsync(after, ledger));
    }

    [Fact]
    public async Task Asked_for_one_state_inside_a_weaker_transaction_it_refuses_and_leaves_it_open()
    {
        // Each statement of a READ COMMITTED transaction reads what committed before it,
        // so a read that exists for one state is refused there rather than joining.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewDbContext();
        await using var owner = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await AddTagAsync(db, ledger, "written by the owner");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadSnapshot.BeginAsync(db, CancellationToken.None, requireRepeatableRead: true));

        // The refusal ended nothing: the owner's transaction is still open and commits.
        Assert.Same(owner, db.Database.CurrentTransaction);
        await owner.CommitAsync();
        await using var after = Db.NewDbContext();
        Assert.Contains("written by the owner", await TagNamesAsync(after, ledger));
    }

    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task Asked_for_one_state_inside_a_transaction_that_holds_one_it_joins_it(IsolationLevel level)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewDbContext();
        await using var owner = await db.Database.BeginTransactionAsync(level);
        var seen = await TagNamesAsync(db, ledger);

        await using (var snapshot = await ReadSnapshot.BeginAsync(db, CancellationToken.None, requireRepeatableRead: true))
        {
            Assert.Null(snapshot);
            Assert.Same(owner, db.Database.CurrentTransaction);
            // The owner's state, fixed at its first read: a write committed since is not seen.
            await using (var other = Db.NewDbContext())
                await AddTagAsync(other, ledger, "committed alongside");
            Assert.Equal(seen, await TagNamesAsync(db, ledger));
        }
        Assert.Same(owner, db.Database.CurrentTransaction);
    }

    private static async Task AddTagAsync(AppDbContext db, SyntheticLedger ledger, string name)
    {
        db.Tags.Add(new TagRow { Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, Name = name });
        await db.SaveChangesAsync();
    }

    private static async Task<List<string>> TagNamesAsync(AppDbContext db, SyntheticLedger ledger) =>
        (await db.Tags.AsNoTracking()
            .Where(t => t.LedgerId == ledger.LedgerId)
            .Select(t => t.Name)
            .ToListAsync())
        .Order(StringComparer.Ordinal)
        .ToList();
}
