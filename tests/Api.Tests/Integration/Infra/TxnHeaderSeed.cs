using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Adding a header a test builds through the <see cref="TxnHeaderRow"/> factories, with
/// its <c>created_at</c> pinned.
/// </summary>
/// <remarks>
/// The factories are the only way to build a header (migration 263), and they leave
/// <c>created_at</c> to the column default, as every writer does. Tests that pin it —
/// so a timestamp predicate cannot race the container's clock — set it on the tracked
/// entry instead of on the object.
/// </remarks>
internal static class TxnHeaderSeed
{
    public static void AddHeader(this AppDbContext db, TxnHeaderRow header, DateTime? createdAt = null)
    {
        var entry = db.TxnHeaders.Add(header);
        if (createdAt is { } at) entry.Property(h => h.CreatedAt).CurrentValue = at;
    }

    /// <summary>
    /// Save <paramref name="db"/>'s pending changes in a transaction that takes
    /// <paramref name="ledgerId"/>'s write lock first (migration 275), as every writer that
    /// can recompute does, and commit it. Returns what <c>SaveChangesAsync</c> returned.
    /// </summary>
    /// <remarks>A save whose interceptors recompute balances or holdings (a request or
    /// service context) is refused without the lock: the recompute functions take it, and
    /// refuse a transaction that wrote before taking it. Inside a transaction the caller
    /// opened, the lock is taken in that transaction (it must be the first statement there
    /// too) and the caller commits.</remarks>
    public static async Task<int> SaveChangesUnderLedgerLockAsync(
        this AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var saved = 0;
        await db.SeedUnderLedgerLockAsync(
            ledgerId, async () => saved = await db.SaveChangesAsync(cancellationToken), cancellationToken);
        return saved;
    }

    /// <summary>
    /// Run <paramref name="seed"/> (raw SQL, explicit recompute calls, saves) in a
    /// transaction that takes <paramref name="ledgerId"/>'s write lock first, and commit it;
    /// inside a transaction the caller opened, take the lock there and leave the commit to
    /// the caller.
    /// </summary>
    public static async Task SeedUnderLedgerLockAsync(
        this AppDbContext db, Guid ledgerId, Func<Task> seed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        await using var write = await db.BeginLedgerWriteAsync(ledgerId, cancellationToken);
        await seed();
        await write.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// A header with no legs, written immediately as SQL: it names no account, so its
    /// originating account is NULL (mig 263) — the one state the factories, which every
    /// writer goes through, cannot build. For tests whose subject is the header alone.
    /// </summary>
    public static Task InsertLeglessHeaderAsync(
        AppDbContext db, Guid id, Guid ledgerId, string? externalId, string payee, string? memo,
        DateTime postedAt, bool isTemplate, Guid? seriesId = null, DateOnly? occurrenceDate = null) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO txn_headers (id, ledger_id, origin, external_id, payee, memo, posted_at, transacted_at,
                                     is_recurring_template, recurring_transaction_id, occurrence_date,
                                     originating_account_id)
            VALUES ({id}, {ledgerId}, 'manual', {externalId}, {payee}, {memo}, {postedAt}, {postedAt},
                    {isTemplate}, {seriesId}, {occurrenceDate}, NULL)
            """);
}
