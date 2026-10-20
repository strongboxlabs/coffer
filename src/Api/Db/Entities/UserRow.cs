namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>users</c>. The Phase A skeleton
/// (id / display_name / last_opened_ledger_id) gained the WebAuthn columns
/// in migration 015.
/// </summary>
/// <remarks>
/// Class with init-only properties rather than a positional record so EF
/// Core change-tracking and Dapper materialisation share the same shape
/// (records add complications around private parameter binding for EF).
/// </remarks>
public sealed class UserRow
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public bool IsDisabled { get; init; }

    /// <summary>
    /// Global operator/admin flag (ADR-0060). Gates system-wide, cross-user
    /// actions (first consumer: whole-DB backup). Set for the first human user
    /// at setup-complete; distinct from the per-ledger
    /// <c>user_ledger_grants</c>. Migration 138.
    /// </summary>
    public bool IsAdmin { get; init; }

    public Guid? LastOpenedLedgerId { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Bootstrap "system" user seeded by migration 014 / 015. Owns the
    /// default ledger; service-account identity for unattended workers
    /// (importer, future SimpleFIN sync).
    /// </summary>
    public static readonly Guid SystemUserId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");
}
