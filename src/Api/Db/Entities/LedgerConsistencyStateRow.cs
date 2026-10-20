namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>ledger_consistency_state</c> (mig 242, FU-012). The latest
/// consistency verdict for one ledger — one row per ledger, overwritten.
/// </summary>
/// <remarks>
/// <para>Before this, the check was computed and thrown away: the monitor ran it,
/// published a notification if something drifted, and kept nothing. A repair left
/// no trace either, because repairing makes the stored rows agree with the
/// transactions — indistinguishable from their having always agreed. So "done"
/// was invisible, and an operator with several ledgers could not tell one that had
/// been repaired from one nobody had opened.</para>
///
/// <para>Four states that were previously one:</para>
/// <list type="bullet">
///   <item>no row at all — nobody has ever checked this ledger;</item>
///   <item><see cref="Healthy"/> null — there is something to remember here, but
///     no current verdict: repaired before any check ran, or invalidated because
///     a restore replaced the data the last verdict was about;</item>
///   <item>true — the projections agreed at <see cref="LastCheckedAt"/>;</item>
///   <item>false — they did not, and <see cref="FailingProjections"/> says which.</item>
/// </list>
///
/// <para>"Unknown" is nullable rather than a sentinel on purpose. The first draft
/// spelled it <c>Healthy = false</c> with an empty failing list and an epoch
/// timestamp, which is wrong in the direction that matters: anything rendering the
/// verdict reads false as UNHEALTHY, so a ledger nobody had examined would show as
/// broken. A database CHECK ties the pair together, so neither half can be written
/// without the other.</para>
///
/// <para>NOT a log. The append-only history of repairs is <c>ledger_events</c>
/// with key <c>consistency.repaired</c>, which already has retention — keeping a
/// second copy here would be two sources of truth for one fact, and the copy is
/// always the one that drifts.</para>
///
/// <para><see cref="LastCheckedAt"/> also gates the monitor's cadence, which is
/// impossible while nothing is remembered. Recording the verdict is separately
/// what let the ANNOUNCEMENT throttle go: with no memory the monitor could not
/// tell "still drifted" from "drifted again", so a timer was the only rate control
/// available, and it swallowed a second projection starting to drift inside the
/// window. The cadence is now the only one.</para>
/// </remarks>
internal sealed class LedgerConsistencyStateRow
{
    public Guid LedgerId { get; init; }

    /// <summary>
    /// When the projections were last compared against the transactions —
    /// written on every check, automatic or on demand. A verdict without a date
    /// cannot tell "fine ten minutes ago" from "fine last August".
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
}
