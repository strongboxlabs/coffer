using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Write + read gateway for <c>feed_connections</c>: connect, list,
/// rename and delete. A sync's writes to the row (status,
/// <c>last_synced_at</c>) are the ingest orchestrator's.
/// </summary>
public sealed class FeedConnectionsRepository
{
    private readonly AppDbContext _db;

    public FeedConnectionsRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Insert a new SimpleFIN connection row. The caller has already
    /// (a) verified the user's ledger grant, (b) exchanged the setup
    /// token for the access URL, and (c) sealed the URL under the
    /// ledger's LEK via <see cref="Crypto.LedgerKeyService"/>. This
    /// method just persists.
    /// </summary>
    public async Task<FeedConnectionRow> CreateSimpleFinAsync(
        Guid ledgerId,
        Guid createdByUserId,
        byte[] accessUrlCiphertext,
        string? institutionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessUrlCiphertext);

        var row = new FeedConnectionRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Provider = "simplefin",
            Status = "active",
            AccessUrlCiphertext = accessUrlCiphertext,
            InstitutionName = institutionName,
            CreatedByUserId = createdByUserId,
        };
        _db.FeedConnections.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// List every feed connection in the supplied ledger, ordered by
    /// most-recently-synced (NULLS LAST — never-synced rows fall to
    /// the bottom so the working set stays near the top of the SPA's
    /// list). RLS handles the per-user grant check; this filter is
    /// just the ledger scope.
    /// </summary>
    public async Task<IReadOnlyList<FeedConnectionRow>> ListByLedgerAsync(
        Guid ledgerId,
        CancellationToken cancellationToken = default) =>
        await _db.FeedConnections
            .AsNoTracking()
            .Where(c => c.LedgerId == ledgerId)
            .OrderByDescending(c => c.LastSyncedAt ?? DateTime.MinValue)
            .ThenByDescending(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Delete a connection by id. Returns the count of rows actually
    /// removed (0 = not found / hidden by RLS, 1 = success). Schema
    /// cascade: <c>accounts.feed_connection_id</c> + <c>sync_runs.feed_connection_id</c>
    /// both ON DELETE SET NULL, so no children block the delete.
    /// </summary>
    /// <remarks>
    /// The delete takes the ledger write lock (migration 275) first. A sync's final save
    /// holds that lock while it updates the mapped accounts' watermark, then inserts rows
    /// whose foreign key names the connection (a bank account the bank listed for the first
    /// time); the delete holds the connection row, then its cascade updates those accounts.
    /// Without the lock each would wait for a row the other holds, and Postgres would cancel
    /// one. With it, the delete waits for the save to commit, or the save for the delete; a
    /// wait past the lock's bound throws <see cref="LedgerWriteBusyException"/>, answered as
    /// the busy refusal, with nothing deleted.
    /// </remarks>
    public async Task<int> DeleteAsync(
        Guid ledgerId,
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var deleted = await _db.FeedConnections
            .Where(c => c.Id == connectionId && c.LedgerId == ledgerId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>The longest name a connection may have, after trimming.</summary>
    public const int NameMaxLength = 80;

    public enum NameCheck { Ok, Required, TooLong }

    /// <summary>The rename's rule, on the trimmed name: 1 to <see cref="NameMaxLength"/>
    /// characters. Two connections may share a name.</summary>
    public static NameCheck CheckName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return NameCheck.Required;
        if (trimmed.Length > NameMaxLength) return NameCheck.TooLong;
        return NameCheck.Ok;
    }

    /// <summary>
    /// Rename a connection: write its trimmed <paramref name="name"/> to
    /// <c>institution_name</c>, the one column that names it, and return the row as it now
    /// stands, or null when the ledger has no such connection. The name must already have
    /// passed <see cref="CheckName"/>. Syncs never write the column, so the name stays
    /// until the next rename.
    /// </summary>
    /// <remarks>
    /// One statement, with no ledger write lock (ADR-0112 D3). It recomputes nothing and
    /// holds one row, the connection's, and once it holds that row it waits for nothing: it
    /// changes no key a foreign key reads, and the table has no trigger. A sync's save or a
    /// delete that holds the row keeps it waiting while it holds nothing, so no wait between
    /// them closes a cycle.
    /// </remarks>
    public async Task<FeedConnectionRow?> RenameAsync(
        Guid ledgerId,
        Guid connectionId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        var updated = await _db.FeedConnections
            .Where(c => c.Id == connectionId && c.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.InstitutionName, trimmed), cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0) return null;
        return await _db.FeedConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == connectionId && c.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns true when <paramref name="connectionId"/> exists in
    /// <paramref name="ledgerId"/>. Used by the per-connection
    /// accounts endpoint as a pre-flight guard before the directory
    /// query — without it, callers could probe whether a UUID is a
    /// connection in another ledger.
    /// </summary>
    public async Task<bool> BelongsToLedgerAsync(
        Guid ledgerId, Guid connectionId,
        CancellationToken cancellationToken = default) =>
        await _db.FeedConnections.AsNoTracking()
            .AnyAsync(c => c.Id == connectionId && c.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Per-connection bank-side account directory (slice 2c.4)
    /// joined to the current Coffer binding. Returns one
    /// <see cref="Contracts.FeedConnectionAccountDto"/> per
    /// SimpleFIN account the bank has surfaced on this connection,
    /// with <see cref="Contracts.FeedConnectionAccountDto.BoundLedgerAccountId"/>
    /// populated when the user has mapped a Coffer account to it.
    /// Sorted by name so the SPA list is deterministic without an
    /// extra client-side sort.
    /// </summary>
    public async Task<IReadOnlyList<Contracts.FeedConnectionAccountDto>>
        ListConnectionAccountsAsync(
            Guid connectionId,
            CancellationToken cancellationToken = default) =>
        await (from d in _db.FeedConnectionAccounts.AsNoTracking()
               where d.FeedConnectionId == connectionId
               join a in _db.Accounts.AsNoTracking()
                   on new { Conn = (Guid?)d.FeedConnectionId, Ext = (string?)d.ExternalId }
                   equals new { Conn = a.FeedConnectionId, Ext = a.ExternalId }
                   into bindingGroup
               from binding in bindingGroup.DefaultIfEmpty()
               orderby d.Name
               select new Contracts.FeedConnectionAccountDto(
                   d.ExternalId,
                   d.Name,
                   d.OrgName,
                   d.Currency,
                   d.Balance,
                   d.LastSeenAt,
                   binding == null ? (Guid?)null : binding.Id,
                   binding == null ? null : binding.Name,
                   binding == null ? (DateTime?)null : binding.LastSimpleFinSyncAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
