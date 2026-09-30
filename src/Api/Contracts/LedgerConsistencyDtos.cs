namespace Coffer.Api.Contracts;

/// <summary>One projection row that disagrees with what the legs imply.</summary>
/// <param name="Scope">Human-readable location, for display.</param>
/// <param name="Field">Which figure disagrees.</param>
/// <param name="Stored">What the projection holds.</param>
/// <param name="Expected">What a fresh derivation produces.</param>
/// <param name="AccountId">Set where the projection is per-account.</param>
/// <param name="SecurityId">Set where it is per-(account, security).</param>
/// <param name="HeaderId">Set where it is per-header.</param>
/// <param name="PriceDate">Set where it is per-(security, day).</param>
/// <param name="Reason">What KIND of finding this is, where a projection has
/// more than one. Null where it has only one. Carried as data rather than left
/// for a reader to infer from <paramref name="Field"/>: the UI has to present
/// an unrepairable finding differently from a repairable one, and sniffing
/// prose for that is a bug waiting on a reworded label.</param>
/// <remarks>
/// The ids are carried separately from <paramref name="Scope"/> so a repair can
/// target exactly what was reported. The first version had only the display
/// string, and the posting-count repair parsed a Guid back out of
/// <c>"header {guid}"</c> — which works right up until someone rewords the label.
/// <paramref name="PriceDate"/> is here for the same reason: the trade-price
/// repair needs the day, and the day was otherwise only legible inside
/// <paramref name="Scope"/>.
/// </remarks>
public sealed record ConsistencyMismatch(
    string Scope,
    string Field,
    decimal Stored,
    decimal Expected,
    Guid? AccountId = null,
    Guid? SecurityId = null,
    Guid? HeaderId = null,
    DateOnly? PriceDate = null,
    string? Reason = null)
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

    /// <summary>
    /// Trade-derived <c>security_prices</c> (migration 232). Maintained by
    /// <c>TradePriceFromLegInterceptor</c> like the other four — what was
    /// singular about it is that it was the one derived figure this report did
    /// not examine, while feeding valuation, allocation and returns.
    /// </summary>
    public const string TradePrices = "trade_prices";

    public static readonly IReadOnlyList<string> All =
        [Balances, Holdings, RealizedGains, PostingCounts, TradePrices];

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
/// <b>Trade-derived <c>security_prices</c> is now covered</b> (migration 232),
/// the way this note said it would have to be. It was left out because a naive
/// row-presence check reports drift that is not there: the per-day
/// source-priority rule makes a MISSING trade price legitimate whenever a
/// <c>fetch</c> close or a <c>manual</c> gap-fill already owns that day. The
/// check asks what the rule IMPLIES instead — last trade of the day by
/// <c>h.seq</c>, comparable only against import/simplefin/trade rows — and it
/// lives in SQL beside the writer it checks, so the two cannot drift apart.
/// </para>
/// </remarks>
public sealed record LedgerConsistencyReport(
    bool Healthy,
    IReadOnlyList<ProjectionConsistency> Projections,
    IReadOnlyList<UnbackedPriceAdvisory> UnbackedPrices);

/// <summary>
/// One security holding prices that claim a trade produced them while no trade
/// currently does. ADVISORY — deliberately not a projection and not counted in
/// <see cref="LedgerConsistencyReport.Healthy"/>.
/// </summary>
/// <remarks>
/// <c>security_prices</c> records THAT a trade wrote a row and never WHICH one,
/// so this can only be inferred by re-deriving and finding nothing — which
/// cannot tell a deleted trade (ADR-0084 D4 keeps its price on purpose) from a
/// rule change from an edit. Reporting all three as defects marked a sound
/// ledger broken: 130 rows on one install, none of them repairable.
/// <para>
/// Grouped per security because that is the unit someone acts on, and ordered
/// by what the holding is worth now, so what still carries money comes first.
/// </para>
/// </remarks>
/// <param name="Count">How many of this security's prices are unbacked.</param>
/// <param name="HoldingValue">Quantity held now at the latest price; 0 for a
/// position since sold, which sorts it last without hiding it.</param>
public sealed record UnbackedPriceAdvisory(
    Guid SecurityId,
    string Security,
    int Count,
    DateOnly Earliest,
    DateOnly Latest,
    decimal HoldingValue);
