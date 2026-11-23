namespace Coffer.Api.Contracts;

/// <summary>
/// Spending against what is TYPICAL for the category — the read-only half of
/// budgeting, and deliberately the whole of v1.
///
/// <para>There is no stored target behind any of this. The "typical" figure is
/// the mean of the category's spend over the trailing complete months (for a ledger
/// younger than the window, over only the months since its first transaction:
/// <see cref="BudgetProgressDto.HistoryFrom"/>), derived on every read. That is not a shortcut around a missing table: most people
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
    decimal? Actual,
    /// <summary>
    /// Mean spend over the trailing window, over only the months since the ledger's
    /// first transaction when it is younger than the window. Null only when the
    /// category has no history in the window at all — a new category shows no
    /// comparison rather than a fabricated zero, which would read as "100% over".
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
    /// The currency the stored target was typed in (ADR-0114 D12), or null when no target
    /// is stored for this category and month. It is what says a target is STORED:
    /// <see cref="Target"/> is null both when none is stored and when one is stored but
    /// needs a missing rate (ADR-0114 D9), and only this tells the two apart. A target
    /// typed in another than the reporting currency still blocks its ancestors and
    /// descendants (ADR-0099 D1a), and clearing it is still a clear.
    /// </summary>
    string? TargetCurrencyCode,
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
    decimal? Mark,
    /// <summary>
    /// Every missing rate a figure of this row needed (ADR-0114 D9): the figure is then
    /// null, and this says why, which tells it apart from a Typical that is null for
    /// having no history. Every figure is in the reporting currency
    /// (<see cref="BudgetProgressDto.CurrencyCode"/>): spending converts at each
    /// posting's date, a target typed in another currency at its month's last day, or
    /// for the current month at the latest rate (ADR-0114 D7, D12). Absent from the
    /// JSON when nothing is missing.
    /// </summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? RateUnavailable = null);

/// <summary>
/// One day's spend in one category. Sparse — a day with no spend has no cell.
/// </summary>
/// <param name="Day">Day of the month, 1-based.</param>
/// <param name="Amount">In the reporting currency; null, with
/// <paramref name="RateUnavailable"/>, when a posting of the day needs a missing rate.</param>
public sealed record BudgetDailyCell(
    Guid CategoryId, int Day, decimal? Amount,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? RateUnavailable = null);

/// <summary>
/// One month of spend-versus-typical for a ledger.
/// </summary>
/// <param name="Month">The month being reported, <c>yyyy-MM</c>.</param>
/// <param name="WindowMonths">
/// The trailing window requested, in complete months, and always that length: it does
/// not shrink for a ledger younger than the window. <see cref="BudgetCategoryRow.Typical"/>
/// averages all of it, or, when <see cref="BudgetProgressDto.HistoryFrom"/> is set,
/// only the days since then, each day over the window's months in which the ledger
/// already existed on it. Surfaced so the screen can say "last 3 months" — a derived
/// number has to show its provenance or a reader cannot judge it — so a screen that
/// names what was averaged reads both fields.
/// </param>
/// <param name="CurrencyCode">
/// The ledger's reporting currency (ADR-0114 D7), the overview's. Every figure here is
/// in it, converted.
/// </param>
/// <param name="DaysInMonth">Length of the reported month.</param>
/// <param name="ElapsedDays">
/// How much of the month has happened, on the caller's local date: today's
/// day-of-month for the current month, the whole month for a past one, zero for a
/// future one. The chart stops its solid line here — a cumulative line drawn to
/// the month's end when only half the month has happened reads as a collapse in
/// spending.
/// </param>
/// <param name="DailyActual">
/// Per-category daily amounts for the reported month, sparse. The client
/// accumulates; the server does not, because the same cells serve both the
/// all-categories hero and any single category the user focuses, and
/// pre-accumulating would mean sending one series per category.
/// </param>
/// <param name="DailyNormal">
/// Per-category mean daily amounts across the trailing window, keyed by
/// day-of-month and divided by the WINDOW LENGTH, or for a ledger younger than the
/// window by the window's months in which it already existed on that day
/// (<see cref="BudgetProgressDto.HistoryFrom"/>). The day is each window month's
/// own, so it can be past the reported month's last day; the client counts such a
/// day on that last day. Accumulated by the client
/// into the shaded band the actual line runs inside — the reference that knows
/// rent lands on the 1st, which no straight pace line does — and read by the
/// projection for what is usually spent over the rest of the month.
/// </param>
public sealed record BudgetProgressDto(
    string Month,
    int WindowMonths,
    IReadOnlyList<BudgetCategoryRow> Rows,
    decimal? ActualTotal,
    decimal? TypicalTotal,
    string CurrencyCode,
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
    string? LatestMonthWithSpending,
    /// <summary>
    /// The part of <see cref="ActualTotal"/> dated after the caller's today: the
    /// rows the register shows as Scheduled. The month's spend counts them
    /// (ADR-0109 D10), so a screen that shows it says "incl. this much scheduled",
    /// and a projection never falls below them, each counted on its own date.
    /// Zero for a past month; the whole total for a future one.
    /// </summary>
    decimal? ScheduledTotal = 0m,
    /// <summary>
    /// The day of the ledger's first transaction, when it falls after the window's
    /// first day; null when the whole window is averaged (or the ledger has no
    /// transactions).
    ///
    /// <para>The window's months before it are no history at all, so they are not
    /// averaged: each day of the month divides by the window's months in which the
    /// ledger already existed on that day, at least one. A screen that names the
    /// window says what was averaged instead — fewer whole months when this is a
    /// month's first day, the months since this day when it is not, and no history
    /// when it is on or after the reported month's first day.</para>
    /// </summary>
    DateOnly? HistoryFrom = null,
    /// <summary>Every missing rate a total here needed (ADR-0114 D9): that total is
    /// null. Absent from the JSON when nothing is missing.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? RateUnavailable = null);

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
/// <para>Two shapes (ADR-0114 D12). Fill from average sends <c>Entries</c>, each stored in
/// the ledger's reporting currency. Copy last month sends <c>CopyFromMonth</c>
/// (<c>yyyy-MM</c>) and no entries: the server copies that month's stored targets, each
/// with the amount and currency it was typed in. Sending both is refused.</para>
/// </remarks>
public sealed record FillBudgetTargetsRequest(
    string Month, IReadOnlyList<BudgetTargetEntry>? Entries, string? CopyFromMonth = null);

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
