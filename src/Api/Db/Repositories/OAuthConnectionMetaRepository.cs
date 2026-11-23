using Microsoft.EntityFrameworkCore;

using Npgsql;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// EF Core gateway to <c>oauth_connection_meta</c>: the owner-chosen name and last-used time
/// of an OAuth connection to <c>/mcp</c>, keyed on its OpenIddict authorization id. Connects
/// via the service-role factory throughout, like <see cref="McpTokensRepository"/>: the
/// last-used stamp runs after authentication and the admin list reads across users. Every
/// write names the owning user and matches it, so the per-user RLS policy is a second line,
/// not the only one.
/// </summary>
public sealed class OAuthConnectionMetaRepository
{
    /// <summary>What is recorded about one connection.</summary>
    public sealed record Meta(string? Label, DateTime? LastUsedAt);

    private readonly ServiceDbContextFactory _serviceFactory;

    public OAuthConnectionMetaRepository(ServiceDbContextFactory serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    /// <summary>
    /// What is recorded for these authorizations. A connection with no row — one made before
    /// the table existed, or never named or used since — is absent from the result.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Meta>> ListAsync(
        IReadOnlyCollection<string> authorizationIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizationIds);
        if (authorizationIds.Count == 0)
            return new Dictionary<string, Meta>();

        await using var db = _serviceFactory.Create();
        var rows = await db.OAuthConnectionMeta.AsNoTracking()
            .Where(m => authorizationIds.Contains(m.AuthorizationId))
            .Select(m => new { m.AuthorizationId, m.Label, m.LastUsedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.AuthorizationId, r => new Meta(r.Label, r.LastUsedAt));
    }

    /// <summary>
    /// Record a use of this connection at <paramref name="now"/>. Returns false when the
    /// authorization no longer exists, or is not this user's.
    /// </summary>
    public async Task<bool> TouchAsync(
        string authorizationId, Guid userId, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();

        // The row exists after the first use, so the common case is the one UPDATE.
        if (await StampAsync(db, authorizationId, userId, now, cancellationToken).ConfigureAwait(false))
            return true;
        if (!await EnsureRowAsync(db, authorizationId, userId, cancellationToken).ConfigureAwait(false))
            return false;
        return await StampAsync(db, authorizationId, userId, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Name this connection; null clears the name, falling back to the client's own. Returns
    /// false when the authorization no longer exists, or is not this user's.
    /// </summary>
    public async Task<bool> SetLabelAsync(
        string authorizationId, Guid userId, string? label, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        if (!await EnsureRowAsync(db, authorizationId, userId, cancellationToken).ConfigureAwait(false))
            return false;
        var rows = await db.OAuthConnectionMeta
            .Where(m => m.AuthorizationId == authorizationId && m.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Label, _ => label), cancellationToken)
            .ConfigureAwait(false);
        return rows == 1;
    }

    private static async Task<bool> StampAsync(
        AppDbContext db, string authorizationId, Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        var rows = await db.OAuthConnectionMeta
            .Where(m => m.AuthorizationId == authorizationId && m.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, _ => (DateTime?)now), cancellationToken)
            .ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>
    /// Make sure this connection has a row. False when the authorization does not exist (the
    /// foreign key refuses the insert) — a stamp or a name for nothing is not recorded. Two
    /// requests making the row at once is not an error: the loser finds it already there.
    /// </summary>
    private static async Task<bool> EnsureRowAsync(
        AppDbContext db, string authorizationId, Guid userId, CancellationToken cancellationToken)
    {
        if (await db.OAuthConnectionMeta.AnyAsync(m => m.AuthorizationId == authorizationId, cancellationToken)
                .ConfigureAwait(false))
            return true;

        db.OAuthConnectionMeta.Add(new OAuthConnectionMetaRow { AuthorizationId = authorizationId, UserId = userId });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            db.ChangeTracker.Clear();
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
        })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
