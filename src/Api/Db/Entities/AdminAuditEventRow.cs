namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>admin_audit_events</c> (migration 191,
/// ADR-0092 D2) — the durable record of deployment-level admin actions on key
/// material.
/// </summary>
/// <remarks>
/// Append-only by convention: nothing in the API updates or deletes these rows,
/// and <c>AuditRetentionService</c> deliberately doesn't prune them (see the
/// migration for why). Every property is <c>init</c>-only so that convention is
/// enforced by the type rather than by discipline.
/// </remarks>
public sealed class AdminAuditEventRow
{
    public Guid Id { get; init; }

    public DateTime OccurredAt { get; init; }

    /// <summary>Stable event name — see <c>AdminAuditActions</c>.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>Who acted. Null once that user is deleted (FK SET NULL), because
    /// the event has to outlive the account.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>
    /// Human-readable context. MUST NOT carry key material, passphrases, or
    /// ciphertext — any admin can read this table.
    /// </summary>
    public string? Detail { get; init; }
}
