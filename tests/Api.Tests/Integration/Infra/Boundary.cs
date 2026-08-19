namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// One source of truth for the magnitudes a financial path has to survive.
/// </summary>
/// <remarks>
/// Two prod failures came from tests seeded with kiddie-pool data — $100, 10 shares —
/// which never approach the magnitudes where money math breaks. The code was wrong at
/// scale and every test passed, because no test ever asked a question big enough to
/// get the wrong answer.
/// <para>
/// So a financial suite takes a <c>[Theory]</c> over <see cref="Positions"/> rather
/// than a single <c>[Fact]</c> at a comfortable size. Every value here is tied to a
/// COLUMN precision, verified against the migration that set it — not invented, and
/// not a round number chosen for looking large:
/// </para>
/// <list type="bullet">
///   <item><c>txn_legs.quantity</c>, <c>txn_legs.unit_price</c> and
///     <c>lots.unit_cost</c> are <c>NUMERIC(25,12)</c> (migration 180, finishing 043).</item>
///   <item>The <c>realized_gains</c> money columns are <c>NUMERIC(19,2)</c>
///     (migration 182).</item>
///   <item><c>security_prices.price</c> is <c>NUMERIC(19,4)</c> (migration 155).</item>
/// </list>
/// <para>
/// The interesting arithmetic is <c>quantity × unit_cost</c>: two 12dp operands
/// produce up to 24 decimal places, and at a seven-figure position that is ~30
/// significant digits — past <c>System.Decimal</c>'s 28–29. Postgres NUMERIC is
/// arbitrary-precision so it stores the value happily; Npgsql then throws
/// <c>OverflowException</c> on the way back rather than truncating. That is the
/// failure mode, and it is only reachable with a FRACTIONAL quantity: whole shares
/// divide evenly and never grow the scale.
/// </para>
/// </remarks>
public static class Boundary
{
    /// <summary>Scale of <c>quantity</c> / <c>unit_price</c> / <c>unit_cost</c> (mig 180).</summary>
    public const int QuantityScale = 12;

    /// <summary>Scale of the money columns (mig 182).</summary>
    public const int MoneyScale = 2;

    /// <summary>Scale of <c>security_prices.price</c> (mig 155).</summary>
    public const int PriceScale = 4;

    /// <summary>
    /// One seedable position, plus the totals it must produce. <see cref="Basis"/> and
    /// <see cref="Proceeds"/> are the money values at <see cref="MoneyScale"/>, so a
    /// test asserts against them instead of recomputing the arithmetic under test.
    /// </summary>
    /// <param name="Name">Appears in the test name, so a failure says WHICH magnitude broke.</param>
    /// <param name="Quantity">Shares bought, then sold in full.</param>
    /// <param name="BuyPrice">Per-share cost.</param>
    /// <param name="SellPrice">Per-share disposal price.</param>
    /// <param name="Basis">Expected cost basis = round(Quantity × BuyPrice, 2).</param>
    /// <param name="Proceeds">Expected proceeds = round(Quantity × SellPrice, 2).</param>
    public sealed record Position(
        string Name,
        decimal Quantity,
        decimal BuyPrice,
        decimal SellPrice,
        decimal Basis,
        decimal Proceeds)
    {
        /// <summary>Expected realized gain, straight from the two money totals.</summary>
        public decimal Gain => Proceeds - Basis;

        /// <summary>
        /// The price move, <see cref="SellPrice"/> / <see cref="BuyPrice"/>.
        /// </summary>
        /// <remarks>
        /// EVERY case shares this ratio, enforced by
        /// <c>BoundaryFixtureTests.Every_position_shares_the_same_price_ratio</c>. That
        /// is what makes cross-magnitude invariance usable: a percentage — a return, an
        /// allocation share, a gain percent — is scale-free, so it must come out
        /// IDENTICAL at 10 shares and at 123,456.789012. A test can then assert two
        /// magnitudes agree without modelling the code under test at all.
        /// <para>
        /// It is deliberately not a coincidence to be rediscovered: an earlier fixture
        /// had 1.25 here and 1.111 there, which made that invariance false and produced
        /// a failing test that looked like an engine bug and was not.
        /// </para>
        /// </remarks>
        public decimal Ratio => SellPrice / BuyPrice;

        /// <summary>
        /// Tolerance for a total derived through a 12dp <c>unit_cost</c> round-trip.
        /// The basis is stored to the cent and the per-share cost is re-derived from
        /// it, so the recomputed total can land a cent either side. Zero for the
        /// typical case, where everything divides evenly.
        /// </summary>
        public decimal Tolerance => Quantity == decimal.Truncate(Quantity) ? 0m : 0.01m;

        public override string ToString() => Name;
    }

    /// <summary>
    /// Whole shares, three-figure money — the size most existing fixtures use. Present
    /// so the matrix proves the boundary case is what breaks, rather than the seeding.
    /// </summary>
    /// <remarks>
    /// 180 -> 200 is 10/9, the SAME ratio as <see cref="LargeFractional"/>'s
    /// 8.10 -> 9.00. The prices are chosen to match that ratio rather than for being
    /// round, because the shared ratio is what <see cref="Position.Ratio"/> relies on.
    /// </remarks>
    public static readonly Position Typical = new(
        Name: "typical",
        Quantity: 10m,
        BuyPrice: 180m,
        SellPrice: 200m,
        Basis: 1_800m,
        Proceeds: 2_000m);

    /// <summary>
    /// 123,456.789012 shares — a full 12dp fractional quantity at a seven-figure
    /// position. The basis does not divide evenly, so <c>unit_cost</c> carries all 12
    /// decimals and <c>quantity × unit_cost</c> reaches 24dp / ~30 significant digits.
    /// This is the shape that took down <c>realized_gains</c> in production before
    /// migration 182 constrained the money columns.
    /// </summary>
    public static readonly Position LargeFractional = new(
        Name: "large-fractional",
        Quantity: 123_456.789012m,
        BuyPrice: 8.10m,
        SellPrice: 9.00m,
        Basis: 999_999.99m,
        Proceeds: 1_111_111.10m);

    /// <summary>
    /// The <c>[Theory]</c> matrix: every financial suite runs its assertions at both
    /// magnitudes. Adding a case here extends every suite that uses it at once, which
    /// is the point of a single source of truth.
    /// </summary>
    public static TheoryData<Position> Positions
    {
        get
        {
            var data = new TheoryData<Position>();
            foreach (var c in All) data.Add(c);
            return data;
        }
    }

    /// <summary>
    /// The same cases as a plain list, for code that inspects them rather than being
    /// parameterised by them — see <c>BoundaryFixtureTests</c>.
    /// </summary>
    public static IReadOnlyList<Position> All { get; } = [Typical, LargeFractional];

    // ---------------------------------------------------------------------------
    // Column limits. These are VALUES rather than seedable positions: a position at
    // MaxMoney has no room left to be multiplied by anything, so it belongs in a test
    // that writes a column directly (schema-drift / precision guards) rather than in
    // the matrix above. The 2026-07-27 decision names each of these, so they live here
    // in one place instead of being re-derived per suite.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The largest value <c>NUMERIC(25,12)</c> holds: 13 integer digits and 12
    /// decimals. A quantity column maximum (mig 180).
    /// </summary>
    public const decimal MaxScaleQuantity = 9_999_999_999_999.999999999999m;

    /// <summary>
    /// The largest value <c>NUMERIC(19,2)</c> holds: 17 integer digits and 2 decimals.
    /// A money column maximum (mig 182).
    /// </summary>
    public const decimal MaxMoney = 99_999_999_999_999_999.99m;

    /// <summary>
    /// A quantity whose product with a 4dp price lands near <see cref="decimal"/>'s
    /// 28-29 significant-digit ceiling without exceeding it — seven integer digits and
    /// a full 12 decimals, so <c>quantity × price</c> carries 16 decimal places on top
    /// of 11 integer digits.
    /// </summary>
    /// <remarks>
    /// This is the magnitude at which an UNROUNDED product stops fitting. It is the
    /// reason <see cref="Coffer.Api.Contracts.ReportingScale"/> bounds market value
    /// and the SQL feeders <c>ROUND</c> their output: Postgres NUMERIC is
    /// arbitrary-precision and stores the full product happily, and Npgsql then throws
    /// <c>OverflowException</c> on the way back rather than truncating.
    /// </remarks>
    public const decimal NearDecimalCeiling = 9_999_999.999999999999m;

    /// <summary>
    /// A money figure large enough to exercise seven-figure-plus aggregation without
    /// approaching <see cref="MaxMoney"/> — the magnitude a real portfolio reaches.
    /// </summary>
    public const decimal LargeMoney = 9_999_999_999.99m;
}
