using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Write + read gateway for <c>feed_connections</c>. Phase 5 slice 1
/// surfaces just the create-on-connect path; list / delete / sync-now
/// land in subsequent slices.
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
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Delete a connection by id. Returns the count of rows actually
    /// removed (0 = not found / hidden by RLS, 1 = success). Schema
    /// cascade: <c>accounts.feed_connection_id</c> + <c>sync_runs.feed_connection_id</c>
    /// both ON DELETE SET NULL, so no children block the delete.
    /// </summary>
    public async Task<int> DeleteAsync(
        Guid ledgerId,
        Guid connectionId,
        CancellationToken cancellationToken = default) =>
        await _db.FeedConnections
            .Where(c => c.Id == connectionId && c.LedgerId == ledgerId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

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
