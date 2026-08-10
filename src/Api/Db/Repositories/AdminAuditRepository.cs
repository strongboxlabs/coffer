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

    public AdminAuditRepository(ServiceDbContextFactory factory) => _factory = factory;

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
            OccurredAt = DateTime.UtcNow,
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
