namespace Coffer.Api.Contracts;

/// <summary>One projection row that disagrees with what the legs imply.</summary>
/// <param name="Scope">Human-readable location, for display.</param>
/// <param name="Field">Which figure disagrees.</param>
/// <param name="Stored">What the projection holds.</param>
/// <param name="Expected">What a fresh derivation produces.</param>
/// <param name="AccountId">Set where the projection is per-account.</param>
/// <param name="SecurityId">Set where it is per-(account, security).</param>
/// <param name="HeaderId">Set where it is per-header.</param>
/// <remarks>
/// The ids are carried separately from <paramref name="Scope"/> so a repair can
/// target exactly what was reported. The first version had only the display
/// string, and the posting-count repair parsed a Guid back out of
/// <c>"header {guid}"</c> — which works right up until someone rewords the label.
/// </remarks>
public sealed record ConsistencyMismatch(
    string Scope,
    string Field,
    decimal Stored,
    decimal Expected,
    Guid? AccountId = null,
    Guid? SecurityId = null,
    Guid? HeaderId = null)
{
    public decimal Diff => Expected - Stored;
}

/// <summary>The projections a consistency check knows how to examine and repair.</summary>
/// <remarks>
/// Every projection the report names has a repair, so a reader is never told about
/// a problem the product cannot fix. String constants rather than an enum: they are
/// route segments and JSON values, and a rename would be a breaking API change
/// worth seeing in a diff.
/// </remarks>
public static class ConsistencyProjections
{
    public const string Balances = "balances";
    public const string Holdings = "holdings";
    public const string RealizedGains = "realized_gains";
    public const string PostingCounts = "posting_counts";

    public static readonly IReadOnlyList<string> All =
        [Balances, Holdings, RealizedGains, PostingCounts];

    public static bool IsKnown(string projection) => All.Contains(projection);
}

/// <summary>The state of one derived projection.</summary>
public sealed record ProjectionConsistency(
    string Projection,
    bool Healthy,
    int Checked,
    int MismatchedCount,
    IReadOnlyList<ConsistencyMismatch> Mismatches);

/// <summary>
/// Whether every derived projection still agrees with the transactions.
/// </summary>
/// <remarks>
/// Four interceptors keep denormalised state in step on every EF save. A write
/// that bypasses the ChangeTracker — raw SQL, Dapper, <c>ExecuteUpdateAsync</c>,
/// a hand-run scrub — skips all of them, and the projections drift silently.
/// That is not hypothetical: a one-off scrub reshaped in-kind transfers on three
/// accounts, correctly recomputed the FIFO side, and never touched balances. The
/// register showed wrong figures for months because nothing ever asked whether
/// the projections still agreed.
/// <para>
/// This asks. It writes nothing, so it is safe to run on a schedule or on a
/// whim, and repairing is a separate deliberate act.
/// </para>
/// <para>
/// <b>Not covered:</b> trade-derived <c>security_prices</c>. A trade leg seeds a
/// price row, but the per-day source-priority rule means a MISSING row is
/// legitimate whenever a manual or fetched price already owns that day — so a
/// naive check reports drift that is not there. Left out deliberately rather
/// than shipped wrong.
/// </para>
/// </remarks>
public sealed record LedgerConsistencyReport(
    bool Healthy,
    IReadOnlyList<ProjectionConsistency> Projections);
