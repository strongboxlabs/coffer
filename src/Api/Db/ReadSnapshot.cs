using System.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Coffer.Api.Db;

/// <summary>
/// One state for a read of several statements: what each statement reads is the same
/// committed state, so two of them cannot disagree about a write that committed between
/// them.
/// </summary>
/// <remarks>
/// <para>With no transaction open on the context, <see cref="BeginAsync"/> opens a
/// REPEATABLE READ one and returns it; the caller disposes it when the read is done, and
/// disposing a transaction nothing committed rolls it back. The read writes nothing, so
/// nothing is lost.</para>
/// <para>With one already open, the read joins it and <see cref="BeginAsync"/> returns
/// null: the statements then read that transaction's state, and it stays open for its
/// owner to commit or roll back. A caller inside a write reads what the write has done so
/// far, and a context holds one transaction at a time, so a second one could not be
/// opened there anyway. A transaction below REPEATABLE READ gives each statement the
/// state committed before it, so joining one loses the one state; a read that exists for
/// it (a consistency check, whose stored rows and walk must agree) asks to be refused
/// there instead (<c>requireRepeatableRead</c>), and a reader that only wants agreement
/// with its caller's writes joins any.</para>
/// <para>Use it as <c>await using var snapshot = await ReadSnapshot.BeginAsync(db, ct);</c>,
/// or in a block when only some of the method's reads belong to the state; a null is
/// disposed as nothing.</para>
/// </remarks>
internal static class ReadSnapshot
{
    /// <summary>
    /// A REPEATABLE READ transaction for the read, or null when the context already has a
    /// transaction open, which the read then joins.
    /// </summary>
    /// <param name="db">The context the read runs on.</param>
    /// <param name="cancellationToken">Cancels opening the transaction.</param>
    /// <param name="requireRepeatableRead">Refuse, rather than join, an open transaction
    /// below REPEATABLE READ: thrown as <see cref="InvalidOperationException"/>, with that
    /// transaction left open for its owner.</param>
    internal static async Task<IDbContextTransaction?> BeginAsync(
        AppDbContext db, CancellationToken cancellationToken, bool requireRepeatableRead = false)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is not { } open)
            return await db.Database
                .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
                .ConfigureAwait(false);

        var level = open.GetDbTransaction().IsolationLevel;
        if (requireRepeatableRead && level is not (IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
            throw new InvalidOperationException(
                $"This read needs one state across its statements, and the open transaction it would join is at {level}, "
                + "where each statement reads what committed before it. Run it outside that transaction, or in one at "
                + "REPEATABLE READ or SERIALIZABLE.");
        return null;
    }
}
