using Coffer.Api.Notifications;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Tests.Unit.Notifications;

/// <summary>
/// The monitor registry is scope-aware, and its ledger half must track the per-ledger
/// scheduled jobs exactly.
/// </summary>
public sealed class MonitorScopeTests
{
    [Fact]
    public void Every_per_ledger_job_type_has_a_monitor()
    {
        // The invariant that makes the split safe to extend. A job type with no monitor is
        // a scheduled job nothing can watch — the silent failure this subsystem exists to
        // remove, added by the very act of adding a job. A monitor with no job type is a
        // dropdown entry that can never go green, which teaches its owner the panel lies.
        //
        // Asserted as SET equality because NotificationMonitors.Ledger is an ordered array
        // (the dropdown and the coverage map need a stable order) while JobTypes.All is an
        // unordered HashSet. Deriving one from the other would have given a
        // nondeterministically ordered UI, so they are declared separately and pinned here.
        // ONE-DIRECTIONAL now, and the weakening is deliberate. This asserted set EQUALITY
        // while every monitor was a job, which was true and became wrong the moment
        // something worth watching was not a job: the consistency check runs on every tick
        // and is deliberately not configurable, which makes it the most valuable ledger
        // switch to bind and the one an equality assertion forbade.
        //
        // The half that still matters is this one. A job type with no monitor is a
        // scheduled job nothing can watch — the silent failure this subsystem exists to
        // remove, added by the act of adding a job.
        Assert.All(JobTypes.All, jobType =>
            Assert.Contains(jobType, NotificationMonitors.Ledger));
    }

    [Fact]
    public void A_monitor_that_is_not_a_job_is_declared_on_purpose()
    {
        // The other half, kept as an explicit allow-list rather than an unchecked gap. A
        // monitor with no job type behind it is a dropdown entry that can never go green
        // unless something publishes for it, so each one has to be a deliberate decision
        // someone made and can find.
        var nonJobMonitors = NotificationMonitors.Ledger
            .Where(m => !JobTypes.All.Contains(m))
            .ToList();

        Assert.Equal(new[] { NotificationMonitors.Consistency }, nonJobMonitors);
    }

    [Fact]
    public void The_ledger_monitor_list_has_no_duplicates_and_a_stable_order()
    {
        Assert.Equal(
            NotificationMonitors.Ledger.Length,
            NotificationMonitors.Ledger.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_ledger_cannot_bind_a_switch_to_a_deployment_monitor()
    {
        // The reason IsKnown takes a scope instead of defaulting to one flat list. The
        // backup is a deployment job; no ledger runs one, so a ledger-scope switch bound
        // to it could never fire — it would sit there enabled, reporting no error, and
        // stay silent forever. That is exactly the state a dead-man's switch exists to
        // detect, manufactured by the screen that offers it.
        Assert.False(NotificationMonitors.IsKnown(
            NotificationScope.Ledger, NotificationMonitors.Backup));
        Assert.True(NotificationMonitors.IsKnown(
            NotificationScope.Deployment, NotificationMonitors.Backup));
    }

    [Fact]
    public void The_deployment_scope_does_not_offer_per_ledger_monitors()
    {
        // The mirror image, and it matters for the same reason: quote-refresh is
        // per-ledger, so a deployment-scope switch bound to it would be watching "some
        // ledger, somewhere" — a check that any one ledger's job could hold green while
        // every other ledger's was dead.
        foreach (var ledgerMonitor in NotificationMonitors.Ledger)
        {
            Assert.False(
                NotificationMonitors.IsKnown(NotificationScope.Deployment, ledgerMonitor),
                $"'{ledgerMonitor}' is per-ledger and must not be bindable at deployment scope");
        }
    }

    [Fact]
    public void An_unknown_monitor_is_unknown_at_both_scopes()
    {
        Assert.False(NotificationMonitors.IsKnown(NotificationScope.Ledger, "not-a-job"));
        Assert.False(NotificationMonitors.IsKnown(NotificationScope.Deployment, "not-a-job"));
        Assert.False(NotificationMonitors.IsKnown(NotificationScope.Ledger, null));
    }
}
