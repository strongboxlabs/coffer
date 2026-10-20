namespace Coffer.Api.Contracts;

/// <summary>
/// Spending against what is TYPICAL for the category — the read-only half of
/// budgeting, and deliberately the whole of v1.
///
/// <para>There is no stored target behind any of this. The "typical" figure is
/// the mean of the category's spend over the trailing complete months, derived
/// on every read. That is not a shortcut around a missing table: most people
/// never set a budget, and of those who do, most want to notice a month that is
/// out of the ordinary rather than to manage spending against a plan. A number
/// nobody has to set is the only kind those users will ever have.</para>
///
/// <para><b>Derived numbers move; decided numbers do not.</b> Because
/// <see cref="BudgetCategoryRow.Typical"/> describes history, it changes when
/// history changes — a backdated receipt, a late feed import, a re-categorised
/// split, or a category merge (which repoints legs with no date predicate, so it
/// rewrites months that are long past). That is correct for a description, and
/// it is why the field is called <i>typical</i> rather than <i>budget</i>: the
/// word has to carry the expectation. When stored targets arrive they will be
/// frozen precisely because a person decided them.</para>
/// </summary>
public sealed record BudgetCategoryRow(
    Guid CategoryId,
    string Name,
    Guid? ParentId,
    /// <summary>Spend in the requested month. Positive magnitude.</summary>
    decimal Actual,
    /// <summary>
    /// Mean spend over the trailing window. Null only when the category has no
    /// history in the window at all — a new category shows no comparison rather
    /// than a fabricated zero, which would read as "100% over".
    /// </summary>
    decimal? Typical,
    /// <summary>
    /// The number a person TYPED for this category and month, or null when they
    /// have not. Null is the ordinary case: ADR-0099 D1 makes presence of a stored
    /// row the state, so most rows carry none and fall back to
    /// <see cref="Typical"/>.
    /// </summary>
    decimal? Target,
    /// <summary>
    /// The AUTHORITATIVE mark for this row — what the bar's tick sits at and what
    /// the hero's ceiling sums. Never compute this client-side from the two fields
    /// above; the composition is a tree walk the server has already done.
    ///
    /// <para>Per ADR-0099 D1a a target and its ancestors are mutually exclusive, so
    /// every subtree has exactly one authoritative level. This is
    /// <see cref="Target"/> when the row is targeted; otherwise it is
    /// <see cref="Typical"/> with each nearest-targeted DESCENDANT's derived normal
    /// swapped out for that descendant's typed number. On a row with no targeted
    /// descendant the two are identical, which is the common case.</para>
    ///
    /// <para>Null only when <see cref="Typical"/> is null and nothing below is
    /// targeted — there is genuinely nothing to compare against.</para>
    /// </summary>
    decimal? Mark);

/// <summary>
/// One day's spend in one category. Sparse — a day with no spend has no cell.
/// </summary>
/// <param name="Day">Day of the month, 1-based.</param>
public sealed record BudgetDailyCell(Guid CategoryId, int Day, decimal Amount);

/// <summary>
/// One month of spend-versus-typical for a ledger.
/// </summary>
/// <param name="Month">The month being reported, <c>yyyy-MM</c>.</param>
/// <param name="WindowMonths">
/// How many trailing complete months <see cref="BudgetCategoryRow.Typical"/>
/// averages over. Surfaced so the screen can say "last 3 months" — a derived
/// number has to show its provenance or a reader cannot judge it.
/// </param>
/// <param name="CurrencyCode">
/// Display currency, derived the same way the overview derives it. A budget
/// figure carries no currency of its own; it inherits the ledger's.
/// </param>
/// <param name="MixedCurrency">
/// True when the ledger's accounts do not agree on a currency, in which case the
/// totals are a sum across currencies and the screen must say so rather than
/// stamping them with one symbol.
/// </param>
/// <param name="DaysInMonth">Length of the reported month.</param>
/// <param name="ElapsedDays">
/// How much of the month has happened: today's day-of-month for the current
/// month, the whole month for a past one, zero for a future one. The chart
/// stops its solid line here — a cumulative line drawn to the month's end when
/// only half the month has happened reads as a collapse in spending.
/// </param>
/// <param name="DailyActual">
/// Per-category daily amounts for the reported month, sparse. The client
/// accumulates; the server does not, because the same cells serve both the
/// all-categories hero and any single category the user focuses, and
/// pre-accumulating would mean sending one series per category.
/// </param>
/// <param name="DailyNormal">
/// Per-category mean daily amounts across the trailing window, keyed by
/// day-of-month and divided by the WINDOW LENGTH. Accumulated by the client
/// into the shaded band the actual line runs inside — the reference that knows
/// rent lands on the 1st, which no straight pace line does.
/// </param>
public sealed record BudgetProgressDto(
    string Month,
    int WindowMonths,
    IReadOnlyList<BudgetCategoryRow> Rows,
    decimal ActualTotal,
    decimal? TypicalTotal,
    string CurrencyCode,
    bool MixedCurrency,
    int DaysInMonth,
    int ElapsedDays,
    IReadOnlyList<BudgetDailyCell> DailyActual,
    IReadOnlyList<BudgetDailyCell> DailyNormal,
    /// <summary>
    /// The most recent month that HAS spending, <c>yyyy-MM</c>, or null.
    ///
    /// <para>Populated only when the requested month has none, and only then:
    /// it costs an extra scan, and the answer is uninteresting on any month
    /// that has data. A ledger whose imports lag — or one being looked at in a
    /// month that has not happened yet — otherwise lands the reader on a wall
    /// of zeroes with no clue that the data is simply elsewhere.</para>
    /// </summary>
    string? LatestMonthWithSpending);

/// <summary>Set one category's target for one month. <c>PUT …/budget/targets</c>.</summary>
/// <param name="Month">
/// <c>yyyy-MM</c>. A month, not a date, so the wire format cannot smuggle in a day
/// that would silently land the row in a different bucket than the caller meant.
/// </param>
public sealed record SetBudgetTargetRequest(Guid CategoryId, string Month, decimal Amount);

/// <summary>
/// Set many targets for one month in a single transaction — "copy last month" and
/// "fill from the N-month average". <c>POST …/budget/targets/fill</c>.
/// </summary>
/// <remarks>
/// The caller proposes; the server decides. Entries that would break ADR-0099 D1a
/// (a target on a node AND on its descendants) are SKIPPED and reported rather
/// than written or silently dropped — one click standing in for thirty typed
/// decisions is exactly where a silent partial failure would be worst.
/// </remarks>
public sealed record FillBudgetTargetsRequest(string Month, IReadOnlyList<BudgetTargetEntry> Entries);

/// <param name="Amount">Non-negative. Zero is meaningful: "I intend to spend
/// nothing here".</param>
public sealed record BudgetTargetEntry(Guid CategoryId, decimal Amount);

/// <summary>What one entry of a fill did, echoed back so the UI can say what it
/// skipped and why.</summary>
/// <param name="Status">
/// <c>ok</c>, <c>ancestor-has-target</c>, <c>descendant-has-target</c>,
/// <c>not-a-category</c> or <c>category-not-in-ledger</c>.
/// </param>
/// <param name="ConflictingCategoryName">
/// Set when the entry was refused for a conflict — the category that already holds
/// the target. Naming it is the point: it may be several levels away and off screen.
/// </param>
public sealed record BudgetTargetEntryResult(
    Guid CategoryId, string Status, string? ConflictingCategoryName);

/// <summary>The outcome of a fill: how many landed, and every entry that did not.</summary>
public sealed record FillBudgetTargetsResponse(
    int Applied, IReadOnlyList<BudgetTargetEntryResult> Skipped);
