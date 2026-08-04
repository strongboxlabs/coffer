namespace Coffer.Api.Scheduling;

/// <summary>
/// Known <c>global_scheduled_jobs.job_type</c> values (mig 139 CHECK mirrors
/// this). Deliberately separate from <see cref="JobTypes"/>: those are
/// per-ledger and validated against per-ledger schedule endpoints, whereas
/// these are deployment-global — mixing them would let a per-ledger endpoint
/// accept "backup" (or vice versa).
/// </summary>
public static class GlobalJobTypes
{
    /// <summary>Whole-DB encrypted backup (ADR-0060).</summary>
    public const string Backup = "backup";
}
