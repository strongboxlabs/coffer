namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>global_scheduled_jobs</c> (mig 139): a deployment-wide
/// (non-ledger) daily schedule, one row per <see cref="JobType"/>. The single
/// <c>SchedulerService</c> scans this alongside the per-ledger
/// <see cref="ScheduledJobRow"/>. The <c>backup</c> row also carries the
/// master-KEK-sealed backup passphrase (ADR-0060).
/// </summary>
internal sealed class GlobalScheduledJobRow
{
    public string JobType { get; init; } = string.Empty;
    public bool Enabled { get; set; }
    public short HourLocal { get; set; }
    public short MinuteLocal { get; set; }
    /// <summary>IANA tz the hour/minute are interpreted in; null →
    /// server-local fallback (same as <see cref="ScheduledJobRow"/>).</summary>
    public string? Timezone { get; set; }
    /// <summary>The one backup passphrase, sealed under the master KEK
    /// (AES-GCM nonce|ct|tag). Null until an admin sets it. Inert without the
    /// KEK, which never lives in the DB.</summary>
    public byte[]? PassphraseCiphertext { get; set; }
    /// <summary>Admin who last configured this row; null if that user was
    /// later removed (FK ON DELETE SET NULL).</summary>
    public Guid? ConfiguredByUserId { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Consecutive handler failures (mig 194); 0 on success. Drives the
    /// backoff and auto-disable in <c>SchedulerRunner</c>.</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>Truncated message of the newest failure (mig 194) — never a stack
    /// trace or payload; this is surfaced in the SPA.</summary>
    public string? LastError { get; set; }
    public DateTime? LastFailureAt { get; set; }
}
