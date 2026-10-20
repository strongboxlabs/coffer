using System.Data;

using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db;

/// <summary>
/// Runs a read-then-write as one SERIALIZABLE transaction, retrying when Postgres aborts it
/// for a conflicting concurrent one (FU-090).
/// </summary>
/// <remarks>
/// <para>For the invariants that count rows and then change one of them: a ledger keeps at
/// least one owner, the install at least one enabled admin. Counted and updated in two
/// statements with nothing between them, two concurrent demotions each counted two, each
/// demoted one, and left none. Under SERIALIZABLE, Postgres sees that each transaction read
/// what the other wrote and aborts one with 40001; the retry counts again, finds the last
/// one, and refuses. No row lock, so no raw SQL: the isolation level is the lock.</para>
/// <para>The body runs again on a retry, on a fresh context, so it must be a plain read and
/// write against <c>db</c> with no effect outside the transaction.</para>
/// </remarks>
public static class SerializableTransaction
{
    private const int MaxAttempts = 5;

    public static async Task<T> RunAsync<T>(
        ServiceDbContextFactory factory, Func<AppDbContext, CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(body);
        for (var attempt = 1; ; attempt++)
        {
            await using var db = factory.Create();
            await using var tx = await db.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await body(db, cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsSerializationConflict(ex))
            {
                // Rolled back when the transaction is disposed; the next attempt starts over.
            }
        }
    }

    /// <summary>40001 serialization failure or 40P01 deadlock, however EF wrapped it.</summary>
    internal static bool IsSerializationConflict(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is Npgsql.PostgresException { SqlState: "40001" or "40P01" })
                return true;
        return false;
    }
}
