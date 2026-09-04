namespace Coffer.Api.Notifications;

/// <summary>
/// How much a notification wants a human. Orthogonal to
/// <see cref="NotificationTopics"/> — ADR-0096 D3.
/// </summary>
/// <remarks>
/// Kept separate from topic because collapsing them into one enum forces every
/// subscriber to re-derive the distinction: "backup succeeded" and "quotes updated"
/// share a severity and want different routing, while "backup failed" and "quote
/// provider unreachable" share a severity and want the same routing.
/// </remarks>
public static class NotificationSeverity
{
    /// <summary>Routine and expected — a backup succeeded, a sync finished.</summary>
    public const string Info = "info";

    /// <summary>Working but degraded — a partial sync, drift found.</summary>
    public const string Warning = "warning";

    /// <summary>A human is needed — no successful backup in 48 hours.</summary>
    public const string Critical = "critical";

    /// <summary>Ordered floor-to-ceiling, so a subscriber's minimum is a slice.</summary>
    public static readonly string[] Ascending = [Info, Warning, Critical];

    /// <summary>Does <paramref name="severity"/> clear the <paramref name="floor"/>?</summary>
    public static bool MeetsFloor(string severity, string floor)
    {
        var s = Array.IndexOf(Ascending, severity);
        var f = Array.IndexOf(Ascending, floor);
        // An unknown severity is treated as critical rather than dropped: losing a
        // notification because someone typo'd its level is the failure mode this
        // whole subsystem exists to remove.
        return s < 0 || f < 0 || s >= f;
    }
}

/// <summary>What a notification is about. Orthogonal to severity — ADR-0096 D3.</summary>
public static class NotificationTopics
{
    public const string Backup = "backup";
    public const string Snapshot = "snapshot";
    public const string Sync = "sync";
    public const string Quotes = "quotes";
    public const string Consistency = "consistency";
    public const string Scheduler = "scheduler";

    public static readonly IReadOnlyList<string> All =
        [Backup, Snapshot, Sync, Quotes, Consistency, Scheduler];
}

/// <summary>
/// One deployment-scope notification: something happened to the INSTALLATION.
/// </summary>
/// <param name="Severity">See <see cref="NotificationSeverity"/>.</param>
/// <param name="Topic">See <see cref="NotificationTopics"/>.</param>
/// <param name="EventKey">
/// Machine-readable discriminator within the topic, e.g. <c>backup.succeeded</c>.
/// A heartbeat subscriber keys its monitor on this, so it is part of the contract
/// rather than a label.
/// </param>
/// <param name="Summary">One line, for a human reading a list.</param>
/// <param name="Detail">Structured extras; kept small — this is not a log.</param>
/// <param name="Monitor">
/// The recurring thing this event reports on — a CHECK identity, not a topic. Null for
/// events that are nobody's liveness signal, which is most of them. Paired with
/// <paramref name="Signal"/> it is what lets a dead-man subscriber be bound to one
/// specific job, because that is the only shape its provider actually has: a
/// healthchecks.io check is one URL, its request body does not affect alerting, and it
/// has no concept of severity. See <see cref="NotificationMonitors"/>.
/// </param>
/// <param name="Signal">
/// Whether this event says the monitored thing SUCCEEDED or FAILED. Deliberately not
/// derived from <paramref name="Severity"/>: severity is how loudly a human wants to
/// hear about something, and the two come apart immediately. `backup.succeeded` is a
/// success signal at Info; `consistency.drift` could be raised to Critical tomorrow and
/// still not be any check's failure.
/// </param>
/// <remarks>
/// <para>
/// Used by BOTH scopes. This remark previously said ledger-scope events were not this
/// type and belonged to <c>ledger_operations</c>; migration 208 created
/// <c>ledger_events</c> and <c>PublishLedgerAsync</c> publishes exactly this record to
/// it, so the note described the design before the split rather than after it.
/// </para>
/// <para>
/// What IS still true is that the two scopes never share a TABLE (ADR-0096 D1): a
/// deployment event lands in <c>system_events</c>, a ledger event in
/// <c>ledger_events</c>, gated by grant and dying with its ledger. The record carries no
/// ledger field — the publisher adds the ledger's name to <see cref="Detail"/> on the way
/// out, so a shared destination can tell the ledgers apart without deployment-scope
/// events carrying a permanently-null column.
/// </para>
/// </remarks>
public sealed record NotificationEvent(
    string Severity,
    string Topic,
    string EventKey,
    string Summary,
    IReadOnlyDictionary<string, string>? Detail = null,
    string? Monitor = null,
    MonitorSignal Signal = MonitorSignal.None);

/// <summary>What an event says about the thing it monitors.</summary>
/// <remarks>
/// Separate from severity on purpose. A dead-man's-switch provider can express exactly
/// two things about one check — it is alive, or it is down — and nothing about how much
/// anyone cares. Collapsing this into the severity scale would mean an unrelated
/// Critical event marking a backup check as failed, which is precisely the bug this
/// type exists to make unrepresentable.
/// </remarks>
public enum MonitorSignal
{
    /// <summary>Not a check signal. The default, and the common case.</summary>
    None = 0,

    /// <summary>The monitored thing happened. A heartbeat subscriber pings.</summary>
    Success,

    /// <summary>The monitored thing failed. A heartbeat subscriber reports it down.</summary>
    Failure,
}

/// <summary>
/// The recurring jobs a dead-man's switch can be bound to.
/// </summary>
/// <remarks>
/// A closed list rather than free text, because coverage is a QUESTION ASKED OF IT:
/// "which of these has somebody watching for its absence, and which is silently
/// unmonitored?" Free-text keys would make an unmonitored job indistinguishable from a
/// typo, and the old code asked something weaker and wrong — whether ANY heartbeat
/// subscriber existed at all, then reported absence detection as covered. One URL bound
/// to backups says nothing about snapshots.
/// </remarks>
public static class NotificationMonitors
{
    /// <summary>The scheduled whole-database backup. Deployment scope.</summary>
    public const string Backup = "backup";

    /// <summary>The per-ledger daily quote refresh (<c>scheduled_jobs.job_type</c>).</summary>
    public const string QuoteRefresh = "quote-refresh";

    /// <summary>The per-ledger daily automatic snapshot (<c>scheduled_jobs.job_type</c>).</summary>
    public const string Snapshot = "snapshot";

    /// <summary>The per-ledger daily bank feed sync (<c>scheduled_jobs.job_type</c>).</summary>
    public const string FeedSync = "feed-sync";

    /// <summary>
    /// The per-ledger reminder auto-post job (<c>scheduled_jobs.job_type</c>, mig 219).
    /// </summary>
    /// <remarks>
    /// The only ledger monitor watching a job that WRITES FINANCIAL TRANSACTIONS, which
    /// is the whole argument in ADR-0097 for making auto-post a job type rather than a
    /// tick-riding monitor: a writer that stops silently is worse than one that stops
    /// loudly, and only a job type has a failure counter, an auto-disable and a switch.
    /// </remarks>
    public const string ReminderAutoPost = "reminder-auto-post";

    /// <summary>
    /// The per-ledger projection consistency check.
    /// </summary>
    /// <remarks>
    /// The one ledger monitor that is NOT a <c>scheduled_jobs.job_type</c>. It runs on
    /// every scheduler tick and is deliberately not configurable — nobody should be able
    /// to switch off the thing whose purpose is noticing that the configurable things
    /// stopped. That makes it the most valuable ledger switch to bind, because it is the
    /// only one that cannot be disabled out from under its own alarm.
    /// </remarks>
    public const string Consistency = "consistency";

    /// <summary>
    /// Monitors that belong to the installation as a whole.
    /// </summary>
    public static readonly string[] Deployment = [Backup];

    /// <summary>
    /// Monitors that belong to a single ledger, one per per-ledger scheduled job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Job monitors take their <c>scheduled_jobs.job_type</c> name on purpose: the thing
    /// being watched IS the job, and a monitor whose name did not match the job it
    /// watches would need a mapping table nobody would keep in step.
    /// </para>
    /// <para>
    /// <see cref="Consistency"/> is the exception, and the exception is the point. An
    /// earlier version of this list was asserted to equal <c>JobTypes.All</c> exactly,
    /// which was right while every monitor was a job and wrong as soon as something worth
    /// watching was not one. The invariant is now one-directional — every job type has a
    /// monitor — plus this explicitly declared non-job monitor, which is weaker and
    /// honest rather than tidy and false.
    /// </para>
    /// <para>
    /// Declared as an ordered array rather than derived from <c>JobTypes.All</c>, which
    /// is an unordered <c>HashSet</c> — deriving it would give a nondeterministically
    /// ordered dropdown and coverage map. <c>MonitorScopeTests</c> pins the invariant the
    /// paragraph above describes: every job type appears here, and the monitors that are
    /// NOT jobs are an explicit allow-list. (This sentence used to claim the test asserted
    /// set EQUALITY, which flatly contradicted the paragraph above it once the consistency
    /// monitor arrived.) So adding a job type without a monitor fails loudly instead of
    /// shipping a job nothing can watch.
    /// </para>
    /// </remarks>
    public static readonly string[] Ledger =
        [QuoteRefresh, Snapshot, FeedSync, ReminderAutoPost, Consistency];

    /// <summary>
    /// Is <paramref name="monitor"/> a monitor that exists at <paramref name="scope"/>?
    /// </summary>
    /// <remarks>
    /// Scope is required rather than optional. A single flat list let ledger scope
    /// validate against deployment monitors and vice versa — so a ledger could bind a
    /// switch to the deployment's backup job, which no ledger runs and which would
    /// therefore never fire. The whole point of splitting the list is that the compiler
    /// makes every call site say which scope it means.
    /// </remarks>
    public static bool IsKnown(NotificationScope scope, string? monitor) =>
        monitor is not null && Array.IndexOf(For(scope), monitor) >= 0;

    /// <summary>Every monitor that exists at <paramref name="scope"/>.</summary>
    public static string[] For(NotificationScope scope) => scope switch
    {
        NotificationScope.Deployment => Deployment,
        NotificationScope.Ledger => Ledger,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}

/// <summary>Which of the two event scopes something belongs to (ADR-0096 D1).</summary>
public enum NotificationScope
{
    /// <summary>The installation as a whole; events land in <c>system_events</c>.</summary>
    Deployment,

    /// <summary>One ledger; events land in <c>ledger_events</c> and are gated by grant.</summary>
    Ledger,
}

