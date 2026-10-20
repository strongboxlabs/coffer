using Coffer.Api.Notifications;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Tests.Unit.Scheduling;

/// <summary>
/// A finished run becomes its monitor's signal — and a run that did not do its work must
/// not hold the check green.
/// </summary>
public sealed class JobMonitorSignalTests
{
    [Fact]
    public void A_clean_run_is_a_success_signal_bound_to_its_own_job()
    {
        var e = JobMonitorSignal.For(
            NotificationMonitors.QuoteRefresh, JobRunOutcome.Ok(), failure: null);

        Assert.NotNull(e);
        Assert.Equal(MonitorSignal.Success, e!.Signal);
        Assert.Equal(NotificationMonitors.QuoteRefresh, e.Monitor);
        Assert.Equal(NotificationSeverity.Info, e.Severity);

        // Bound to the job it is about, not to "some scheduled job": one healthchecks URL
        // is one check, so a signal that did not name its job could keep another job's
        // check alive.
        Assert.Equal(NotificationTopics.Quotes, e.Topic);
    }

    [Fact]
    public void A_thrown_run_is_a_failure_signal_that_does_not_quote_the_exception()
    {
        // The published summary goes to whatever URL a ledger holder configured and is
        // readable by every grant holder including a viewer. A raw Npgsql or
        // HttpRequestException message routinely names a host, a port, a database or a
        // full URL, so the message never travels — only the class of failure does.
        var e = JobMonitorSignal.For(
            NotificationMonitors.Snapshot, JobRunOutcome.Ok(),
            failure: new InvalidOperationException("Host=10.0.0.7;Database=coffer;Password=hunter2"));

        Assert.NotNull(e);
        Assert.Equal(MonitorSignal.Failure, e!.Signal);
        Assert.Equal(NotificationSeverity.Warning, e.Severity);
        Assert.EndsWith(".failed", e.EventKey, StringComparison.Ordinal);

        // Nothing from the message, not even a fragment.
        Assert.DoesNotContain("10.0.0.7", e.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", e.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Database=", e.Summary, StringComparison.Ordinal);

        // ...but it still says what KIND of failure, and where the detail lives.
        Assert.Contains("unexpected error", e.Summary, StringComparison.Ordinal);
        Assert.Contains("server log", e.Summary, StringComparison.Ordinal);

        // The TYPE is safe and is the first thing a triager wants.
        Assert.Equal("InvalidOperationException", e.Detail!["error_type"]);
    }

    [Fact]
    public void A_recognised_failure_is_described_by_its_kind()
    {
        // Coarse on purpose: enough to tell a reader whether this is theirs to fix — a
        // provider refusing a request is acted on differently from a database error —
        // without naming the host it happened against.
        var db = JobMonitorSignal.For(
            NotificationMonitors.Snapshot, JobRunOutcome.Ok(),
            failure: new Microsoft.EntityFrameworkCore.DbUpdateException("relation x does not exist"));
        Assert.Contains("database rejected", db!.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("relation x", db.Summary, StringComparison.Ordinal);

        var net = JobMonitorSignal.For(
            NotificationMonitors.QuoteRefresh, JobRunOutcome.Ok(),
            failure: new HttpRequestException("No such host is known (bank.example.com:443)"));
        Assert.Contains("network request", net!.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("bank.example.com", net.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_degraded_run_is_a_FAILURE_signal_not_a_success()
    {
        // The whole reason the outcome channel exists. A healthchecks check has two
        // states; a run that completed without doing its work must not hold it green.
        // Safe to be this strict because Degraded is already scoped to a genuine provider
        // outage — a delisted ticker never reaches here.
        var e = JobMonitorSignal.For(
            NotificationMonitors.QuoteRefresh,
            JobRunOutcome.Degraded("No quote provider succeeded: yahoo timed out"),
            failure: null);

        Assert.NotNull(e);
        Assert.Equal(MonitorSignal.Failure, e!.Signal);
        Assert.Contains("yahoo timed out", e.Summary);
    }

    [Fact]
    public void An_unbounded_failure_message_is_capped_before_it_is_published()
    {
        // The same message is capped at 500 on its way into scheduled_jobs.last_error,
        // whose comment explains why: "A message, never a stack trace — the column is
        // surfaced in the SPA." The published summary had no cap, which made the stricter
        // of the two paths the one nobody looks at.
        //
        // And this one travels further: it is DELIVERED to whatever URL a ledger holder
        // configured, and ledger_events_read grants SELECT to every grant holder with no
        // role filter — a viewer included. A 40KB Npgsql message is the wrong thing to
        // hand either of them.
        var e = JobMonitorSignal.For(
            NotificationMonitors.Snapshot, JobRunOutcome.Ok(),
            failure: new InvalidOperationException(new string('x', 40_000)));

        Assert.NotNull(e);
        Assert.True(e!.Summary.Length < 700,
            $"summary should be bounded, was {e.Summary.Length} chars");

        // Not merely capped — the message is not there at all now.
        Assert.DoesNotContain("xxxx", e.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_degraded_reason_is_capped_too_not_just_a_thrown_one()
    {
        // The three unbounded sources are different shapes: a thrown exception's Message,
        // the quote handler joining every provider failure, and the feed handler joining
        // one entry per faulted connection. Capping only the exception path would leave
        // the other two, which is why the cap lives where the summary is built.
        var e = JobMonitorSignal.For(
            NotificationMonitors.QuoteRefresh,
            JobRunOutcome.Degraded(new string('y', 40_000)),
            failure: null);

        Assert.NotNull(e);
        Assert.True(e!.Summary.Length < 700,
            $"degraded summary should be capped, was {e.Summary.Length} chars");
    }

    [Fact]
    public void A_short_handler_authored_reason_is_left_exactly_as_it_is()
    {
        // Degraded messages are handler-authored prose, not exception text, so they pass
        // through — and the cap must not touch ordinary ones, or every alert grows a
        // trailing ellipsis and the marker stops meaning anything.
        var e = JobMonitorSignal.For(
            NotificationMonitors.Snapshot,
            JobRunOutcome.Degraded("Automatic snapshot skipped: all five slots hold manual snapshots."),
            failure: null);

        Assert.NotNull(e);
        Assert.Contains("all five slots", e!.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("…", e.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_type_with_no_monitor_announces_nothing()
    {
        // The runner dispatches whatever job_type rows exist, including ones this build
        // has no handler for. Answering null beats throwing inside a scheduler tick.
        Assert.Null(JobMonitorSignal.For("not-a-job", JobRunOutcome.Ok(), failure: null));
    }

    [Fact]
    public void Every_ledger_monitor_produces_a_topic_the_event_tables_accept()
    {
        // A topic outside the CHECK constraints (migrations 207 and 208) would make the
        // ledger_events INSERT raise 23514 BEFORE delivery — taking down the scheduler
        // tick that runs the real jobs, over a notification.
        foreach (var monitor in NotificationMonitors.Ledger)
        {
            var e = JobMonitorSignal.For(monitor, JobRunOutcome.Ok(), failure: null);
            Assert.NotNull(e);
            Assert.Contains(e!.Topic, NotificationTopics.All);
        }
    }
}
