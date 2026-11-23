namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>ledger_consistency_state</c> (mig 242, FU-012). The latest
/// consistency verdict for one ledger — one row per ledger, overwritten.
/// </summary>
/// <remarks>
/// <para>A check computed and thrown away — run, a notification published if
/// something drifted, nothing kept — leaves no trace, and nor does a repair, because
/// repairing makes the stored rows agree with what they are derived from — indistinguishable
/// from their having always agreed. "Done" would be invisible, and an operator with
/// several ledgers could not tell one that has been repaired from one nobody has
/// opened.</para>
///
/// <para>Four distinct states:</para>
/// <list type="bullet">
///   <item>no row at all — nobody has ever checked this ledger;</item>
///   <item><see cref="Healthy"/> null — there is something to remember here, but
///     no current verdict: repaired before any check ran, or invalidated because
///     a restore replaced the data the last verdict was about;</item>
///   <item>true — the projections agreed at <see cref="LastCheckedAt"/>;</item>
///   <item>false — they did not, and <see cref="FailingProjections"/> says which.</item>
/// </list>
///
/// <para>"Unknown" is nullable rather than a sentinel on purpose. Spelling it
/// <c>Healthy = false</c> with an empty failing list and an epoch timestamp is wrong
/// in the direction that matters: anything rendering the verdict reads false as
/// UNHEALTHY, so a ledger nobody had examined would show as broken. A database CHECK ties the pair together, so neither half can be written
/// without the other.</para>
///
/// <para>NOT a log. The append-only history of repairs is <c>ledger_events</c>
/// with key <c>consistency.repaired</c>, which already has retention — keeping a
/// second copy here would be two sources of truth for one fact, and the copy is
/// always the one that drifts.</para>
///
/// <para><see cref="LastCheckedAt"/> also gates the monitor's cadence, which is
/// impossible while nothing is remembered. Recording the verdict is separately
/// what makes an ANNOUNCEMENT throttle unnecessary: with no memory the monitor
/// cannot tell "still drifted" from "drifted again", so a timer would be the only
/// rate control available, and it would swallow a second projection starting to
/// drift inside the window. The cadence is the only rate control.</para>
/// </remarks>
internal sealed class LedgerConsistencyStateRow
{
    public Guid LedgerId { get; init; }

    /// <summary>
    /// When the projections were last compared with what they are derived from (the
    /// transactions, or, for holdings_currency, each holdings account's brokerage, or,
    /// for reminder_auto_post, the ledger's reminders) —
    /// written on every check, automatic or on demand. A verdict without a date
    /// cannot tell "fine ten minutes ago" from "fine last August". It is when the
    /// check began to read the ledger, and a check that began earlier does not
    /// overwrite it (<c>ConsistencyVerdicts</c>).
    /// <para>Null means there is no current verdict, and it is also what makes a
    /// ledger due: the monitor's gate is <c>LastCheckedAt &gt; cutoff</c>, which
    /// is null — not true — for an unknown. So invalidating a verdict is a single
    /// null and needs no knowledge of the cadence.</para>
    /// </summary>
    public DateTime? LastCheckedAt { get; set; }

    /// <summary>
    /// The verdict at <see cref="LastCheckedAt"/>, or null when there is none.
    /// False means CHECKED AND WRONG — never "not known".
    /// </summary>
    public bool? Healthy { get; set; }

    /// <summary>
    /// Which projections disagreed, by their <c>ConsistencyProjections</c> name;
    /// empty when healthy or unknown. Stored rather than derived so the list
    /// survives without re-running the check that produced it — and so a NEW
    /// projection drifting is detectable by comparison rather than invisible.
    /// </summary>
    public string[] FailingProjections { get; set; } = [];

    /// <summary>
    /// The last time anyone repaired anything on this ledger, or null.
    /// Deliberately NOT cleared by a later healthy check: that a ledger was once
    /// repaired is a fact about its history, and a green check does not retract it.
    /// </summary>
    public DateTime? LastRepairedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// The row's version, Postgres's <c>xmin</c>, which every write of the row changes; a
    /// system column, not one of the table's. Every recorder saves the row against the
    /// version it read (<c>ConsistencyVerdicts</c>), so a write that lands in between makes
    /// the save fail and the recorder read the row again and decide again.
    /// </summary>
    public uint Version { get; set; }
}
