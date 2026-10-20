using Microsoft.EntityFrameworkCore;

using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

using Coffer.Api.Db;

namespace Coffer.Api.Backup;

/// <summary>
/// What a restore resets in the database it has built, before that database goes live:
/// every way in that was granted before, and the Google Drive identity.
/// </summary>
/// <remarks>
/// <para><b>Access (FU-076).</b> A restore used to bring back every session, MCP token and
/// OAuth grant exactly as it was at backup time, revoked ones included, and every pending
/// invite. Incident response backfired: revoke the attacker, restore a backup from before
/// the incident, and their access came back. So a restore now signs everyone out: every
/// cookie session, manual MCP token, OAuth authorization and OAuth token is revoked, and
/// every pending invite is removed. Revoked, not deleted, for the first four, with the
/// same <c>revoked_at</c> / status the per-user revocation sets, so the rows read as
/// revoked rather than vanishing; an invite is removed because that is what revoking one
/// is. Users with their admin and disabled flags, passkeys and recovery codes come back as
/// backed up: clearing passkeys would lock out an install that moved domains. OAuth client
/// registrations stay, so a connector signs in again rather than registering anew.</para>
/// <para><b>Drive (ADR-0074).</b> The restored install starts Drive-disconnected and mints
/// a fresh install id on reconnect: left intact, the connection would resolve to the
/// SOURCE install's Drive folder and, once reconnected, mirror into and delete from it.
/// What the source install put on Drive is not this install's to delete, so the record of
/// what was uploaded through that connection goes too (FU-064).</para>
/// <para>Run on the incoming database after its migrations, inside one transaction, so a
/// database that carries the restore's marker has been reset in full.</para>
/// </remarks>
internal static class RestoreReset
{
    /// <summary>What was reset, for the log.</summary>
    public sealed record Result(int Sessions, int McpTokens, int OauthAuthorizations, int OauthTokens, int Invites);

    public static async Task<Result> ApplyAsync(AppDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var sessions = await db.AuthSessions
            .Where(s => s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)nowUtc), ct)
            .ConfigureAwait(false);
        var mcpTokens = await db.McpAccessTokens
            .Where(t => t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, _ => (DateTime?)nowUtc), ct)
            .ConfigureAwait(false);
        // The status OpenIddict's own revocation sets; validation rejects anything else.
        var oauthTokens = await db.Set<OpenIddictEntityFrameworkCoreToken>()
            .Where(t => t.Status == null || t.Status != OpenIddictConstants.Statuses.Revoked)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, _ => OpenIddictConstants.Statuses.Revoked), ct)
            .ConfigureAwait(false);
        var oauthAuthorizations = await db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .Where(a => a.Status == null || a.Status != OpenIddictConstants.Statuses.Revoked)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, _ => OpenIddictConstants.Statuses.Revoked), ct)
            .ConfigureAwait(false);
        var invites = await db.Invites
            .Where(i => i.ConsumedAt == null)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        await db.DriveSync.ExecuteUpdateAsync(s => s
                .SetProperty(d => d.OauthCiphertext, _ => null)
                .SetProperty(d => d.FolderId, _ => null)
                .SetProperty(d => d.FolderName, _ => null)
                .SetProperty(d => d.ConnectedEmail, _ => null)
                .SetProperty(d => d.InstallId, _ => null)
                .SetProperty(d => d.Enabled, _ => false)
                .SetProperty(d => d.LastSyncAt, _ => null)
                .SetProperty(d => d.LastSyncStatus, _ => null)
                .SetProperty(d => d.LastSyncError, _ => null), ct)
            .ConfigureAwait(false);
        await db.DriveMirroredBackups.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return new Result(sessions, mcpTokens, oauthAuthorizations, oauthTokens, invites);
    }
}
