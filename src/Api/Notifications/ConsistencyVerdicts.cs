using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Notifications;

/// <summary>Who ran a consistency check.</summary>
public enum ConsistencyCheckOrigin
{
    /// <summary>The scheduled monitor (<see cref="ConsistencyMonitor"/>).</summary>
    Monitor,

    /// <summary>A person, from Settings → General → Maintenance.</summary>
    Person,
}

/// <summary>
/// Records a consistency verdict and announces it: the one path through which the
/// monitor's pass, a check a person runs and a repair write a ledger's stored verdict
/// (<c>ledger_consistency_state</c>, mig 242) and its consistency notices.
/// </summary>
/// <remarks>
/// <para>The Overview reads both. Its Problems widget lists the ledger's outstanding
/// <c>consistency.</c> subject from <c>ledger_events</c> (<see cref="OutstandingSubjects"/>)
/// and says when the verdict was taken from the stored row. A check that reached neither
/// would leave the Overview saying what an older check found, so every check and every
/// repair records here, whoever ran it.</para>
/// <para>Writes through the SERVICE context: the table has a read policy for the app role
/// and no write policy, so an app-role write is refused by RLS rather than by nobody
/// trying. What it writes is derived from the check alone, with nothing a caller
/// supplies, which is why a reader may run the check that records it.</para>
/// </remarks>
public sealed class ConsistencyVerdicts
{
    /// <summary>A verdict that every projection agrees.</summary>
    public const string OkKey = "consistency.ok";

    /// <summary>A verdict that something disagrees.</summary>
    public const string DriftKey = "consistency.drift";

    /// <summary>A repair, with the verdict after it.</summary>
    public const string RepairedKey = "consistency.repaired";

    /// <summary>The name oversold positions are recorded and announced under (FU-097).</summary>
    public const string OversoldFinding = "oversold";

    /// <summary>
    /// The keys that announce a check's verdict, each with the monitor's signal: what
    /// <see cref="ConsistencyMonitor"/>'s cadence counts as the ledger's last announcement.
    /// </summary>
    public static readonly string[] VerdictKeys = [OkKey, DriftKey];

    /// <summary>The one subject every consistency event shares (<see cref="EventSubjects"/>).</summary>
    private static readonly string Subject = EventSubjects.Of(DriftKey);

    private readonly ServiceDbContextFactory _dbFactory;
    private readonly NotificationPublisher _publisher;
    private readonly TimeProvider _clock;

    public ConsistencyVerdicts(
        ServiceDbContextFactory dbFactory,
        NotificationPublisher publisher,
        TimeProvider clock)
    {
        _dbFactory = dbFactory;
        _publisher = publisher;
        _clock = clock;
    }

    /// <summary>
    /// Record a full check's verdict, and announce it when its origin calls for it.
    /// </summary>
    /// <returns>Whether the verdict was announced.</returns>
    /// <remarks>
    /// <para>Every check records, whoever ran it: when it ran, the verdict, and which
    /// projections disagreed (FU-012). <c>LastRepairedAt</c> is left as it is: that a ledger
    /// was once repaired is history, and a green check does not retract it.</para>
    /// <para>The MONITOR announces on every pass, in both states. One publish serves two
    /// audiences: a dead-man's switch takes its signal and needs it on a cadence, and a person
    /// is filtered by topic and severity. The monitor's cadence is its only rate control
    /// (<see cref="ConsistencyMonitor.CheckEvery"/>).</para>
    /// <para>A PERSON's check has no cadence: it runs whenever it is pressed. So it announces
    /// only what the ledger does not already say: a verdict other than the stored one (the
    /// other state, or another set of failing projections, or none stored), or one the
    /// ledger's consistency notice contradicts (an outstanding problem on a healthy ledger,
    /// or none on an unhealthy one). The announcement is the monitor's own event, signal
    /// included, so a dead-man's switch hears the change at once. Pressing Check again with
    /// nothing changed records the time and sends nothing, and the monitor keeps the ledger's
    /// heartbeat: its cadence counts announcements as well as checks.</para>
    /// <para>A check takes a while, and others can record while it runs: a repair, a person's
    /// check, a restore's invalidation. A verdict is written only when its check began no
    /// earlier than the stored verdict's (<see cref="IsNoOlder"/>): an older one describes
    /// data a newer check has already seen past, and is neither written nor announced. So a
    /// monitor pass that began before a repair committed does not overwrite the repair's
    /// verdict, or announce drift the repair fixed. The row is saved against the version it
    /// was read at, and read again and decided again when another writer saved first,
    /// creating it included (<see cref="SaveStateAsync"/>). A person's check decided again
    /// after losing that race announces only a verdict other than the one now stored: the
    /// writer that won held the same verdict and announced what it needed to.</para>
    /// <para>The announcement commits with the row it follows, in one transaction, so no
    /// other writer saves between the two (<see cref="SaveStateAsync"/>).</para>
    /// </remarks>
    /// <param name="ledgerId">The ledger checked.</param>
    /// <param name="report">The check's report.</param>
    /// <param name="origin">Who ran the check.</param>
    /// <param name="checkedAt">When the check began to read the ledger: the verdict's time.</param>
    /// <param name="cancellationToken">Cancels the record.</param>
    public async Task<bool> RecordCheckAsync(
        Guid ledgerId,
        LedgerConsistencyReport report,
        ConsistencyCheckOrigin origin,
        DateTime checkedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var failing = Failing(report);

        var announced = await SaveStateAsync(ledgerId, async (db, row, raced, ct) =>
        {
            if (!IsNoOlder(row, checkedAt)) return Write.Nothing;
            var announce = origin == ConsistencyCheckOrigin.Monitor
                           || !SaysTheSame(row, report.Healthy, failing)
                           || (!raced
                               && await NoticeOutstandingAsync(db, ledgerId, ct).ConfigureAwait(false) == report.Healthy);
            row.LastCheckedAt = checkedAt;
            row.Healthy = report.Healthy;
            row.FailingProjections = failing;
            row.UpdatedAt = Now;
            return new Write(true, announce ? Announcement(report) : null);
        }, cancellationToken).ConfigureAwait(false);

        return announced is not null;
    }

    /// <summary>
    /// Record a repair: the verdict of the check after it, when it ran, and the repair
    /// itself, in the stored state and in one <c>consistency.repaired</c> event.
    /// </summary>
    /// <remarks>
    /// <para>A repair leaves no trace in the data it repairs: it makes the stored rows agree
    /// with what they are derived from, which is indistinguishable from their having always
    /// agreed. So the stored <c>LastRepairedAt</c> and the event are the only record that it
    /// happened (FU-012). The verdict is the full check
    /// <see cref="ProjectionRepairResult.After"/> took once the repair had committed, so it
    /// claims no more than was checked.</para>
    /// <para>The event says what the repair rewrote and what still disagrees. It is info only
    /// when nothing does, by the verdict that stands (below): an info event clears the
    /// ledger's <c>consistency.</c> subject, which every consistency event shares, so a
    /// repair that left another projection out of step is a warning and the subject stays
    /// outstanding, its summary naming what is left.</para>
    /// <para>A repair whose check did not finish (<see cref="ProjectionRepairResult.After"/>
    /// null) is recorded all the same, since the rewrite stands: <c>LastRepairedAt</c>, and an
    /// event saying what it rewrote and that what still disagrees is not known. The stored
    /// verdict was taken before the rewrite and no longer describes the data, so it is
    /// cleared, as a restore clears it: the state reads "not checked yet" and the ledger is
    /// due on the monitor's next tick. The event is a warning, so the subject stays
    /// outstanding until a check says otherwise.</para>
    /// <para>NOT suppressed, unlike a person's repeated check: a repair is a deliberate act
    /// that changed the data, and collapsing two repairs into one line would lose exactly the
    /// history this exists to keep. No signal: a repair is nobody's liveness ping.</para>
    /// <para>The repair is always recorded, but its verdict, or the clearing of one, only
    /// when no check that began later has recorded its own (<see cref="IsNoOlder"/>), as
    /// for a check; <c>LastRepairedAt</c> keeps the later of two repairs. A clearing is
    /// as of the rewrite, which the verdict it clears was taken before, so a check or a
    /// repair that began after the rewrite is no older than it. A repair whose verdict is
    /// not written changes nothing a later verdict is compared with: it stamps
    /// <c>LastRepairedAt</c> alone.</para>
    /// <para>The event follows the verdict that stands once that is decided, never one the
    /// row does not hold: every consistency event shares the ledger's one subject, so an
    /// event read off this repair's own check, when a later check's verdict stands, would
    /// clear the subject on a ledger stored as unhealthy, or reopen it on one stored as
    /// healthy. So when a later check's verdict stands, the event is info only when that
    /// verdict is healthy, its summary says what that check found (by name: the row keeps
    /// no counts), and its detail says <c>superseded</c>; when a restore or a later repair
    /// has cleared the verdict since, the event is a warning saying what still disagrees is
    /// not known. The event commits with the row, in one transaction, so a writer that
    /// saves the row after this repair does so, and records its own event, only once both
    /// have committed (<see cref="SaveStateAsync"/>).</para>
    /// </remarks>
    /// <param name="ledgerId">The ledger repaired.</param>
    /// <param name="repair">What the repair rewrote, and the check after it if one finished.</param>
    /// <param name="repairedAt">When the rewrite had committed, which is when the check after it
    /// began: the repair's time, and its verdict's.</param>
    /// <param name="cancellationToken">Cancels the record.</param>
    public async Task RecordRepairAsync(
        Guid ledgerId,
        ProjectionRepairResult repair,
        DateTime repairedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repair);
        var after = repair.After;

        await SaveStateAsync(ledgerId, (_, row, _, _) =>
        {
            var own = IsNoOlder(row, repairedAt);
            if (own)
            {
                if (after is null)
                {
                    Clear(row, repairedAt);
                }
                else
                {
                    row.LastCheckedAt = repairedAt;
                    row.Healthy = after.Healthy;
                    row.FailingProjections = Failing(after);
                    row.UpdatedAt = Now;
                }
            }
            // Otherwise the verdict, or the clearing, that stands keeps its time, which a
            // later verdict is compared with: only LastRepairedAt moves.
            if (row.LastRepairedAt is null || row.LastRepairedAt < repairedAt) row.LastRepairedAt = repairedAt;
            // The verdict that stands once the row is decided: the repair's own when it is
            // written, and otherwise the one a check that began later recorded first (or
            // none, when a restore or a later repair has cleared it since).
            return Task.FromResult(new Write(true, RepairEvent(repair, own, row.Healthy, row.FailingProjections)));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Record a repair whose rewrite threw partway: clear the stored verdict as of when the
    /// rewrite stopped.
    /// </summary>
    /// <remarks>
    /// <para>Several rewrites commit in pieces (balances an account at a time, holdings and
    /// realized gains a position at a time, posting counts a header at a time), so one that
    /// throws partway leaves what it committed standing. The stored verdict was taken before
    /// the rewrite and may no longer describe the data, so it is cleared as for a repair
    /// whose check did not finish: the state reads "not checked yet" and the ledger is due on
    /// the monitor's next tick. The clearing is as of when the rewrite stopped, after the
    /// last piece it committed, so a check that began later is no older than it; and, as for
    /// a repair, it is made only when no check that began later has recorded its own
    /// (<see cref="IsNoOlder"/>). A ledger with no row gets one holding no verdict, as of
    /// the same time, so a check that began before the rewrite stopped and records after it
    /// is not written.</para>
    /// <para><c>LastRepairedAt</c> is left as it is, since no repair completed, and no event
    /// is recorded: nothing says the ledger is healthy, and a consistency notice that is
    /// outstanding stays so until a check says otherwise. Logging the failure and answering
    /// it are the caller's.</para>
    /// </remarks>
    /// <param name="ledgerId">The ledger whose repair failed.</param>
    /// <param name="stoppedAt">When the rewrite stopped, after the last piece it committed:
    /// the clearing's time.</param>
    /// <param name="cancellationToken">Cancels the record.</param>
    public async Task RecordFailedRewriteAsync(
        Guid ledgerId,
        DateTime stoppedAt,
        CancellationToken cancellationToken = default)
    {
        await SaveStateAsync(ledgerId, (_, row, _, _) =>
        {
            if (!IsNoOlder(row, stoppedAt)) return Task.FromResult(Write.Nothing);
            Clear(row, stoppedAt);
            return Task.FromResult(new Write(true, null));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A repair's <c>consistency.repaired</c> event, by the verdict that stands: the
    /// repair's own when <paramref name="own"/>, else <paramref name="standingHealthy"/>
    /// and <paramref name="standingFailing"/>, the one the row holds (null: none).
    /// </summary>
    private static NotificationEvent RepairEvent(
        ProjectionRepairResult repair, bool own, bool? standingHealthy, string[] standingFailing)
    {
        var after = repair.After;
        var detail = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projection"] = repair.Projection,
            ["rewritten"] = Count(repair.Repaired.MismatchedCount),
        };

        var rewrote = repair.Repaired.MismatchedCount == 0
            ? $"Repaired {repair.Projection}: nothing needed changing."
            : $"Repaired {repair.Projection}: {Rewrote(repair.Projection, repair.Repaired.MismatchedCount)}.";

        string summary;
        if (!own)
        {
            // A check that began after the rewrite committed saw everything this repair's
            // check saw, and recorded first: its verdict stands, and the event says that one.
            // The stored row keeps the names of what disagrees, not the counts.
            detail["superseded"] = "true";
            if (standingHealthy is { } healthy) detail["healthy"] = healthy ? "true" : "false";
            if (standingHealthy == false) detail["still_to_fix"] = string.Join(", ", standingFailing);
            summary = standingHealthy switch
            {
                null => rewrote + " The verdict has been cleared since, so what still disagrees is not known.",
                true => rewrote + " A check that began after it found every projection agrees.",
                false => rewrote + " A check that began after it found what still disagrees: "
                         + string.Join(", ", standingFailing) + ".",
            };
        }
        else if (after is null)
        {
            detail["checked"] = "false";
            summary = rewrote + " The check after it did not finish, so what still disagrees is not known.";
        }
        else
        {
            detail["remaining"] = Count(repair.ProjectionAfter().MismatchedCount);
            detail["healthy"] = after.Healthy ? "true" : "false";
            if (!after.Healthy)
                detail["still_to_fix"] = string.Join(", ", DriftDetail(after).Select(kv => kv.Key + ": " + kv.Value));
            summary = after.Healthy
                ? rewrote + " Every projection agrees."
                : rewrote + " Still to fix: " + string.Join(". ", DriftParts(after)) + ".";
        }

        return new NotificationEvent(
            // The verdict that stands decides the subject: info, the all-clear, only when it is
            // healthy. An event that followed this repair's own check when a later one had
            // recorded would reopen, or clear, the subject against the stored verdict.
            Severity: standingHealthy == true ? NotificationSeverity.Info : NotificationSeverity.Warning,
            Topic: NotificationTopics.Consistency,
            EventKey: RepairedKey,
            Summary: summary,
            Detail: detail,
            Monitor: NotificationMonitors.Consistency,
            Signal: MonitorSignal.None);
    }

    /// <summary>
    /// The verdict's announcement: <c>consistency.ok</c> while healthy, <c>consistency.drift</c>
    /// naming what disagrees otherwise, each with the monitor's signal.
    /// </summary>
    /// <remarks>
    /// Drift is a warning, not critical: it is real and wants fixing, but it is slow damage
    /// with a repair button, unlike a missing backup, which is data that does not come back.
    /// Without the monitor and signal a switch bound to consistency would be pinged healthy
    /// every day and never told about drift.
    /// </remarks>
    private static NotificationEvent Announcement(LedgerConsistencyReport report) =>
        report.Healthy
            ? new NotificationEvent(
                Severity: NotificationSeverity.Info,
                Topic: NotificationTopics.Consistency,
                EventKey: OkKey,
                // True of every projection, holdings_currency included, which compares
                // holdings accounts with their brokerages rather than with the transactions.
                Summary: "Every projection agrees.",
                Detail: null,
                Monitor: NotificationMonitors.Consistency,
                Signal: MonitorSignal.Success)
            : new NotificationEvent(
                Severity: NotificationSeverity.Warning,
                Topic: NotificationTopics.Consistency,
                EventKey: DriftKey,
                Summary: string.Join(". ", DriftParts(report)) + ".",
                Detail: DriftDetail(report),
                Monitor: NotificationMonitors.Consistency,
                Signal: MonitorSignal.Failure);

    /// <summary>
    /// What disagrees, one sentence each: every unhealthy projection with its count, then
    /// the oversold positions, named because their fix is by hand and per position.
    /// </summary>
    private static List<string> DriftParts(LedgerConsistencyReport report)
    {
        var parts = report.Projections
            .Where(p => !p.Healthy)
            .Select(p => $"{p.Projection}: {OutOfStep(p)}")
            .ToList();
        if (report.Oversold.Count > 0)
        {
            parts.Add($"{report.Oversold.Count} position(s) sold more shares than they held, "
                      + "so their gain is overstated ("
                      + string.Join("; ", report.Oversold.Take(3).Select(o => $"{o.Account} / {o.Security}"))
                      + (report.Oversold.Count > 3 ? "; ..." : "")
                      + "). Correct the sale or enter the missing purchase");
        }
        return parts;
    }

    /// <summary>What disagrees, by name, with its count.</summary>
    private static Dictionary<string, string> DriftDetail(LedgerConsistencyReport report)
    {
        var detail = report.Projections
            .Where(p => !p.Healthy)
            .ToDictionary(p => p.Projection, p => Count(p.MismatchedCount), StringComparer.Ordinal);
        if (report.Oversold.Count > 0) detail[OversoldFinding] = Count(report.Oversold.Count);
        return detail;
    }

    /// <summary>
    /// How many of a projection's rows are out of step, as a summary says it. The
    /// holdings_currency projection compares each brokerage's holdings with the brokerage,
    /// and says so in the Maintenance panel's words ("Holdings currency — N of M brokerages
    /// out of step", "brokerage" when M is 1). The category_amounts projection says it as
    /// "N of M category lines out of step", "category line" when M is 1. The
    /// reminder_auto_post projection examines the ledger's one job and says it is not set
    /// up, as the panel does. Every other projection compares stored figures with what the
    /// transactions imply. The minor-units advisories
    /// (<see cref="LedgerConsistencyReport.UnfitAmounts"/>,
    /// <see cref="LedgerConsistencyReport.RefusedCurrencies"/>) are not findings, so no
    /// summary names them.
    /// </summary>
    private static string OutOfStep(ProjectionConsistency p) => p.Projection switch
    {
        ConsistencyProjections.HoldingsCurrency =>
            $"{Count(p.MismatchedCount)} of {Count(p.Checked)} {Brokerages(p.Checked)} out of step",
        // Compares fetched prices with the currency Yahoo names (ADR-0114 D5, amendment).
        ConsistencyProjections.QuoteCurrency =>
            $"{Count(p.MismatchedCount)} of {Count(p.Checked)} {Securities(p.Checked)} with fetched prices "
            + "in a currency Yahoo has not confirmed",
        // Compares each category line with the real line it pays against (ADR-0114 D13), in
        // the Maintenance panel's words for the finding labelled "Category amounts".
        ConsistencyProjections.CategoryAmounts =>
            $"{Count(p.MismatchedCount)} of {Count(p.Checked)} {CategoryLines(p.Checked)} out of step",
        ConsistencyProjections.ReminderAutoPost =>
            "the auto-post job is not set up, so reminders set to auto-post are never posted",
        _ => $"{Count(p.MismatchedCount)} of {Count(p.Checked)} disagree with the transactions",
    };

    /// <summary>
    /// What a repair of <paramref name="rewritten"/> rows did, as its summary says it: the
    /// holdings_currency repair records each brokerage's holdings in its own currency, as
    /// the Maintenance panel says; the quote_currency repair asks Yahoo again; the
    /// category_amounts repair sets each category line out of step to the paying account's
    /// amount, negated; the reminder_auto_post repair turns the ledger's auto-post job on;
    /// every other repair rewrites what the transactions imply.
    /// </summary>
    private static string Rewrote(string projection, int rewritten) => projection switch
    {
        ConsistencyProjections.HoldingsCurrency =>
            $"recorded the holdings of the {Count(rewritten)} {Brokerages(rewritten)} out of step in "
            + (rewritten == 1 ? "its" : "their") + " own currency",
        ConsistencyProjections.QuoteCurrency =>
            $"asked Yahoo for the currency of the {Count(rewritten)} {Securities(rewritten)} reported and "
            + "re-stamped their fetched prices to each currency it named",
        // Sets each category line to its real line negated (ADR-0114 D13, D15; decided by
        // the owner, 2026-11-18).
        ConsistencyProjections.CategoryAmounts =>
            $"set the {Count(rewritten)} {CategoryLines(rewritten)} out of step to the paying account's amount, negated",
        ConsistencyProjections.ReminderAutoPost => "turned the auto-post job on",
        _ => $"rewrote the {Count(rewritten)} that disagreed with the transactions",
    };

    /// <summary>"brokerage" for one, "brokerages" for any other count.</summary>
    private static string Brokerages(int n) => n == 1 ? "brokerage" : "brokerages";

    /// <summary>"category line" for one, "category lines" for any other count.</summary>
    private static string CategoryLines(int n) => n == 1 ? "category line" : "category lines";

    /// <summary>"security" for one, "securities" for any other count.</summary>
    private static string Securities(int n) => n == 1 ? "security" : "securities";

    /// <summary>
    /// The names the stored row keeps for what disagrees: each unhealthy projection, and
    /// <see cref="OversoldFinding"/> for oversold positions, which are findings without a
    /// projection (FU-097). Ordinal order, so two verdicts compare by their lists.
    /// </summary>
    private static string[] Failing(LedgerConsistencyReport report) =>
        report.Projections.Where(p => !p.Healthy).Select(p => p.Projection)
            .Concat(report.Oversold.Count > 0 ? [OversoldFinding] : [])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Whether the stored row already holds this verdict.</summary>
    private static bool SaysTheSame(LedgerConsistencyStateRow row, bool healthy, string[] failing) =>
        row.Healthy == healthy
        && row.FailingProjections.Order(StringComparer.Ordinal).SequenceEqual(failing, StringComparer.Ordinal);

    /// <summary>
    /// Whether a verdict taken at <paramref name="at"/> is no older than what the row holds:
    /// its check began no earlier than the stored verdict's, or, with no verdict stored, no
    /// earlier than the clearing, whose time is the row's <c>UpdatedAt</c>: a restore's; for
    /// a repair whose check did not finish, when its rewrite committed
    /// (<see cref="RecordRepairAsync"/>); or, for a rewrite that threw partway, when it
    /// stopped, after the last piece it committed (<see cref="RecordFailedRewriteAsync"/>).
    /// In each case the verdict cleared was taken before the rewrite or the restore. A save
    /// that leaves the verdict as it stands does not move it. A row not yet saved holds
    /// nothing.
    /// </summary>
    private static bool IsNoOlder(LedgerConsistencyStateRow row, DateTime at) =>
        row.LastCheckedAt is { } stored ? stored <= at : row.UpdatedAt <= at;

    /// <summary>
    /// Clear the row's verdict as of <paramref name="at"/>: no verdict, so the state reads
    /// "not checked yet" and the monitor's next tick checks the ledger, and <c>UpdatedAt</c>
    /// at the clearing's time, which a later verdict is compared with (<see cref="IsNoOlder"/>),
    /// not at this save's. <c>LastRepairedAt</c> is the caller's.
    /// </summary>
    private static void Clear(LedgerConsistencyStateRow row, DateTime at)
    {
        row.LastCheckedAt = null;
        row.Healthy = null;
        row.FailingProjections = [];
        row.UpdatedAt = at;
    }

    /// <summary>The clock's time, for a row written now.</summary>
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// What a record decided: whether to save the row at all, and the event, if any, to
    /// record with it.
    /// </summary>
    private readonly record struct Write(bool Save, NotificationEvent? Event)
    {
        /// <summary>Save nothing, and record nothing.</summary>
        public static Write Nothing => new(false, null);
    }

    /// <summary>How many times a record reads the row again after another writer saved it first.</summary>
    private const int SaveAttempts = 5;

    /// <summary>
    /// Read the ledger's row (a new one when it has none), let <paramref name="decide"/> set
    /// what to write and name the event that follows it, and save the row and the event
    /// together, in one transaction, against the version read; then deliver the event. When
    /// another writer saved the row first, its version moved, or it created the row first,
    /// and the save fails with nothing written; the row is then read again and
    /// <paramref name="decide"/> decides again, told that it lost a race.
    /// </summary>
    /// <remarks>
    /// One transaction, so no writer saves between a row and its event. Every consistency
    /// event is recorded here with a save of the row, and a writer whose save follows
    /// another's has read the row only after that one committed, so its event, stamped with
    /// its own transaction's start, is the later: the ledger's latest consistency event is
    /// always the one that followed the row as last saved.
    /// </remarks>
    /// <param name="decide">Given the context, the row, whether an earlier attempt lost a race,
    /// and the token; sets the row's fields and answers whether to save it and what event to
    /// record with it.</param>
    /// <returns>The event recorded, or null when nothing was recorded.</returns>
    private async Task<NotificationEvent?> SaveStateAsync(
        Guid ledgerId,
        Func<AppDbContext, LedgerConsistencyStateRow, bool, CancellationToken, Task<Write>> decide,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            NotificationEvent? recorded;
            await using (var db = _dbFactory.Create())
            {
                var row = await db.LedgerConsistencyStates
                    .FirstOrDefaultAsync(r => r.LedgerId == ledgerId, cancellationToken)
                    .ConfigureAwait(false);
                var created = row is null;
                row ??= new LedgerConsistencyStateRow { LedgerId = ledgerId };
                var write = await decide(db, row, attempt > 1, cancellationToken).ConfigureAwait(false);
                if (!write.Save) return null;
                if (created) db.LedgerConsistencyStates.Add(row);
                // Written even when no field changed (a repair whose verdict is not written,
                // stamping no later LastRepairedAt), so the save is still checked against the
                // version read and holds the row until it commits.
                else db.Entry(row).Property(r => r.UpdatedAt).IsModified = true;
                if (write.Event is not null) NotificationPublisher.RecordLedgerEvent(db, ledgerId, write.Event);
                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    recorded = write.Event;
                }
                catch (DbUpdateException ex) when (attempt < SaveAttempts
                                                    && (ex is DbUpdateConcurrencyException || IsDuplicate(ex)))
                {
                    // Another writer saved the row first: read it again, and decide again.
                    continue;
                }
            }

            if (recorded is not null)
                await _publisher.DeliverLedgerAsync(ledgerId, recorded, cancellationToken).ConfigureAwait(false);
            return recorded;
        }
    }

    /// <summary>Whether a save failed because another writer created the ledger's row first.</summary>
    private static bool IsDuplicate(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>Whether the ledger's consistency subject is outstanding: its latest warning
    /// has no info event after it (<see cref="OutstandingSubjects"/>, the Overview's own
    /// read).</summary>
    private static async Task<bool> NoticeOutstandingAsync(
        AppDbContext db, Guid ledgerId, CancellationToken cancellationToken) =>
        (await OutstandingSubjects.ListAsync(db, [ledgerId], cancellationToken).ConfigureAwait(false))
            .Any(s => string.Equals(s.Subject, Subject, StringComparison.Ordinal));

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
}
