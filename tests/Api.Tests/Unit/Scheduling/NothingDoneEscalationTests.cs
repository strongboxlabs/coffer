using Coffer.Api.Contracts;
using Coffer.Api.Notifications;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Tests.Unit.Scheduling;

/// <summary>
/// FU-071: a daily job that does nothing — no failure, so no failure count and no
/// auto-disable — used to report a Warning every day for as long as it lasted. A day in,
/// it is coverage that has stopped, and its signal says so at Critical.
/// </summary>
public sealed class NothingDoneEscalationTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static readonly JobRunOutcome Skipped =
        JobRunOutcome.DidNothing("Automatic snapshot skipped: all five slots hold manual snapshots.");

    [Fact]
    public void Doing_nothing_for_more_than_a_day_is_critical_and_says_for_how_long()
    {
        var signal = JobMonitorSignal.For(JobTypes.Snapshot, Skipped, failure: null, Now.AddDays(-3), Now)!;

        Assert.Equal(NotificationSeverity.Critical, signal.Severity);
        Assert.Equal(MonitorSignal.Failure, signal.Signal);
        Assert.Contains("done nothing for 3 day(s)", signal.Summary);
        Assert.Contains("all five slots", signal.Summary);
    }

    [Fact]
    public void Doing_nothing_on_one_run_is_still_a_warning()
    {
        var signal = JobMonitorSignal.For(JobTypes.Snapshot, Skipped, failure: null, Now.AddHours(-23), Now)!;

        Assert.Equal(NotificationSeverity.Warning, signal.Severity);
    }

    [Fact]
    public void Exactly_a_day_is_where_it_escalates()
    {
        var signal = JobMonitorSignal.For(JobTypes.Snapshot, Skipped, failure: null, Now.AddDays(-1), Now)!;

        Assert.Equal(NotificationSeverity.Critical, signal.Severity);
    }

    [Fact]
    public void A_partial_run_never_escalates_however_long_ago_the_last_full_one_was()
    {
        // One provider of three failing for a month is a standing Warning, by design: the
        // job still refreshes the others. Escalating it would be a Critical every day.
        var partial = JobRunOutcome.Degraded("A quote provider failed (1 of 3).");

        var signal = JobMonitorSignal.For(JobTypes.QuoteRefresh, partial, failure: null, Now.AddDays(-30), Now)!;

        Assert.Equal(NotificationSeverity.Warning, signal.Severity);
    }

    [Fact]
    public void A_failure_is_counted_and_disabled_by_the_scheduler_not_escalated_here()
    {
        var signal = JobMonitorSignal.For(
            JobTypes.Snapshot, JobRunOutcome.Ok(), new InvalidOperationException("boom"), Now.AddDays(-9), Now)!;

        Assert.Equal(NotificationSeverity.Warning, signal.Severity);
    }

    [Fact]
    public void The_old_signature_never_escalates()
    {
        Assert.Equal(NotificationSeverity.Warning,
            JobMonitorSignal.For(JobTypes.Snapshot, Skipped, failure: null)!.Severity);
    }

    // --- NothingDoneSince, the rule the schedule UI and the escalation share --

    private static readonly DateTime Created = Now.AddDays(-40);

    [Fact]
    public void A_run_that_did_nothing_after_an_earlier_success_reads_as_nothing_since_that_success()
    {
        Assert.Equal(Now.AddDays(-2), ScheduleHealthRules.NothingDoneSince(
            "skipped", 0, lastRunAt: Now, lastSuccessAt: Now.AddDays(-2), Created));
    }

    [Fact]
    public void A_partial_run_moves_both_timestamps_together_and_is_not_nothing()
    {
        Assert.Null(ScheduleHealthRules.NothingDoneSince(
            "one provider failed", 0, lastRunAt: Now, lastSuccessAt: Now, Created));
    }

    [Fact]
    public void A_failing_job_is_a_failure_not_nothing()
    {
        Assert.Null(ScheduleHealthRules.NothingDoneSince("boom", 2, Now, Now.AddDays(-3), Created));
    }

    [Fact]
    public void A_job_that_has_never_run_has_not_done_nothing_yet()
    {
        Assert.Null(ScheduleHealthRules.NothingDoneSince(null, 0, lastRunAt: null, lastSuccessAt: null, Created));
    }

    [Fact]
    public void A_job_that_did_nothing_and_never_succeeded_reads_from_when_it_was_set_up()
    {
        Assert.Equal(Created, ScheduleHealthRules.NothingDoneSince("skipped", 0, Now, null, Created));
    }
}
