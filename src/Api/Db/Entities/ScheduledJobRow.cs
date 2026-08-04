namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>scheduled_jobs</c> (mig 136). One row per (ledger,
/// job_type): the per-ledger daily schedule + the worker's polling bookkeeping.
/// A single background worker dispatches each due row by <see cref="JobType"/>
/// to a registered handler.
/// </summary>
internal sealed class ScheduledJobRow
{
    public Guid LedgerId { get; init; }
    public string JobType { get; init; } = string.Empty;
    public bool Enabled { get; set; }
    public short HourLocal { get; set; }
    public short MinuteLocal { get; set; }
    /// <summary>IANA tz the hour/minute are interpreted in (mig 137); null →
    /// server-local fallback.</summary>
    public string? Timezone { get; set; }
    public Guid ConfiguredByUserId { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
}
