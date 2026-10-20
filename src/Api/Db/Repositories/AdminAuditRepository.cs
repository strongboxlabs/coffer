using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Stable vocabulary for <c>admin_audit_events.action</c> (ADR-0092 D2). Lives here
/// rather than as a database CHECK: the webauthn flow CHECK has needed widening
/// three times (migrations 140, 176, 190) purely to admit a new string, and an audit
/// table grows new event types by nature.
/// </summary>
public static class AdminAuditActions
{
    /// <summary>The master KEK was displayed to an admin after a fresh assertion.</summary>
    public const string MasterKeyRevealed = "master-key.revealed";

    /// <summary>The master KEK was rotated; everything wrapped was re-wrapped.</summary>
    public const string MasterKeyRotated = "master-key.rotated";

    /// <summary>A restore adopted the source install's master KEK.</summary>
    public const string MasterKeyAdopted = "master-key.adopted";

    /// <summary>
    /// The master KEK was shown during first-run setup. Distinct from
    /// <see cref="MasterKeyRevealed"/> so an auditor can tell the unavoidable
    /// bootstrap disclosure apart from a deliberate later one — the first has no
    /// fresh-assertion gate (the registration ceremony is the proof) and can only
    /// ever happen once per install.
    /// </summary>
    public const string MasterKeyShownAtSetup = "master-key.shown-at-setup";

    /// <summary>
    /// The stored backup passphrase was displayed to an admin after a fresh assertion
    /// (ADR-0092 D7). Its own action, not folded into
    /// <see cref="MasterKeyRevealed"/>: this is the secret that decrypts every artifact
    /// the install has produced, so an auditor should see it called out by name.
    /// </summary>
    public const string BackupPassphraseRevealed = "backup-passphrase.revealed";

    // Privilege changes (FU-090). Each its own action, so an auditor can ask "who was made
    // an admin" without reading every row. Detail names the target, never a token.

    /// <summary>A user was made an instance admin.</summary>
    public const string UserAdminGranted = "user.admin-granted";

    /// <summary>A user's instance admin flag was removed.</summary>
    public const string UserAdminRevoked = "user.admin-revoked";

    /// <summary>A user was disabled (and their sessions, tokens and grants revoked).</summary>
    public const string UserDisabled = "user.disabled";

    /// <summary>A disabled user was enabled again.</summary>
    public const string UserEnabled = "user.enabled";

    /// <summary>A ledger member's role was changed.</summary>
    public const string MemberRoleChanged = "member.role-changed";

    /// <summary>A member was removed from a ledger.</summary>
    public const string MemberRemoved = "member.removed";

    /// <summary>An invite was issued: by a ledger owner, or by an admin (possibly granting admin).</summary>
    public const string InviteIssued = "invite.issued";

    /// <summary>A pending invite was revoked.</summary>
    public const string InviteRevoked = "invite.revoked";
}

/// <summary>
/// Append-and-read access to <c>admin_audit_events</c> (ADR-0092 D2, migration 191).
/// </summary>
/// <remarks>
/// Service-role only, like every deployment-global table (RLS denies
/// <c>coffer_app</c> outright): the <c>RequireAdmin</c> policy is the boundary, since
/// admin is a deployment-global capability rather than a per-ledger one.
///
/// There is no update or delete. An audit row that could be edited by the party it
/// describes wouldn't be worth writing, and the retention service deliberately
/// leaves these alone — key access is rare and its value is precisely that the
/// record is old.
/// </remarks>
public sealed class AdminAuditRepository
{
    private readonly ServiceDbContextFactory _factory;
    private readonly TimeProvider _clock;

    public AdminAuditRepository(ServiceDbContextFactory factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    /// <summary>
    /// Append one event. <paramref name="detail"/> is free text for the operator's
    /// benefit and must never carry key material, a passphrase, or ciphertext — any
    /// admin can read it back.
    /// </summary>
    public async Task<Guid> AppendAsync(
        string action,
        Guid? actorUserId,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var row = new AdminAuditEventRow
        {
            Id = Guid.NewGuid(),
            OccurredAt = _clock.GetUtcNow().UtcDateTime,
            Action = action.Trim(),
            ActorUserId = actorUserId,
            Detail = detail,
        };

        await using var db = _factory.Create();
        db.AdminAuditEvents.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>Most recent events first, capped.</summary>
    public async Task<IReadOnlyList<AdminAuditEventRow>> RecentAsync(
        int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return Array.Empty<AdminAuditEventRow>();

        await using var db = _factory.Create();
        return await db.AdminAuditEvents.AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
