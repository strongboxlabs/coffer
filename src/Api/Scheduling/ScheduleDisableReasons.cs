namespace Coffer.Api.Scheduling;

/// <summary>
/// Why a scheduled job is disabled, when a person is not the one who disabled it
/// (<c>scheduled_jobs.disabled_reason</c> / <c>global_scheduled_jobs.disabled_reason</c>,
/// mig 216).
/// </summary>
/// <remarks>
/// <para>
/// NULL is the common case and means two things that need no distinguishing: the job is
/// enabled, or a person turned it off. Everything else is something that happened TO the
/// job, and a panel showing the difference is the point of the column.
/// </para>
/// <para>
/// Not a DB CHECK, matching this repo's position on vocabulary columns (migs 191, 212,
/// 214): the values live here and are validated on write, so adding one is a code change
/// rather than a migration. The trade is that a bad value is a bug rather than a write
/// error, which is why <see cref="All"/> exists and the tests assert against it.
/// </para>
/// <para>
/// CURRENT STATE, NOT HISTORY. Every value here is cleared when the job is re-enabled,
/// because the question it answers is "why is this off right now". The durable record of
/// a disable is the notification published at the moment it happens.
/// </para>
/// </remarks>
public static class ScheduleDisableReasons
{
    /// <summary>
    /// The scheduler gave up after
    /// <see cref="SchedulerRunner.DisableAfterConsecutiveFailures"/> consecutive
    /// failures.
    /// </summary>
    public const string ConsecutiveFailures = "consecutive-failures";

    /// <summary>
    /// The backup job lost its usable key material — a cross-install restore left a
    /// passphrase sealed to a KEK this deployment does not have, so
    /// <c>KekReconciliationService</c> switched the job off rather than let it fail
    /// nightly against a secret it cannot open.
    /// </summary>
    public const string KeyMaterialMissing = "key-material-missing";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { ConsecutiveFailures, KeyMaterialMissing };
}
