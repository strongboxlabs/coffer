namespace Coffer.Api.Db.Entities;

/// <summary>
/// One budget target: the number a person typed for one category in one month
/// (mig 225, ADR-0099 D1).
/// </summary>
/// <remarks>
/// <para>PRESENCE OF THE ROW IS THE STATE. There is no "budgeting enabled" flag and no
/// per-ledger mode. A category with a row for this month is judged against
/// <see cref="Amount"/>; a category without one is judged against its derived normal,
/// computed from the trailing months and stored nowhere. Deleting a row therefore does
/// not clear a budget — it returns that category to its normal, which is why the screen
/// still works for someone who sets three categories and forgets the rest.</para>
///
/// <para>TWO RULES THIS TYPE CANNOT ENFORCE, both of which live in
/// <c>BudgetTargetsRepository</c> rather than here or in the schema:</para>
///
/// <list type="number">
///   <item><description><see cref="CategoryId"/> must name a CATEGORY. The composite FK
///   points at <c>accounts(id, ledger_id)</c> and categories are rows in
///   <c>accounts</c>, so nothing in the database stops a target on a bank
///   account.</description></item>
///   <item><description>A target and its ancestors are MUTUALLY EXCLUSIVE (ADR-0099
///   D1a) — a target may sit on a node or on its descendants, never both, so each
///   subtree has exactly one authoritative mark. That is a recursive tree predicate,
///   which no CHECK or unique index can express, and ADR-0032 makes a trigger a last
///   resort.</description></item>
/// </list>
/// </remarks>
public sealed class BudgetTargetRow
{
    public Guid Id { get; set; }

    public Guid LedgerId { get; set; }

    /// <summary>The category this target is for. A category, not any account — see the
    /// type's remarks for why the database cannot say so.</summary>
    public Guid CategoryId { get; set; }

    /// <summary>
    /// The month this target applies to, always the FIRST day of it. A database CHECK
    /// enforces the truncation, because a mid-month date would produce two rows that are
    /// the same month to a reader and different to the unique index.
    /// </summary>
    public DateOnly TargetMonth { get; set; }

    /// <summary>
    /// The typed amount, in <see cref="CurrencyCode"/>, <c>NUMERIC(20,3)</c> (migration
    /// 301). Zero is meaningful ("I intend to spend nothing here"); negatives are refused
    /// by a CHECK.
    ///
    /// <para>The column is no longer the rounding point (ADR-0114 D15). It holds up to 3
    /// places for every currency, and no CHECK holds a row to its currency's places, because
    /// a stored target that does not fit is kept. The API holds the rule instead: a typed
    /// target that carries more places than its currency allows is refused
    /// (<c>BudgetTargetsRepository</c>), and a figure the API computes, a category merge's
    /// converted share, is rounded once at its currency's places, half away from zero,
    /// before it is stored. Round nowhere else: mig 209 exists only to undo a double-round
    /// introduced by a well-meaning <c>round(x, 4)</c> upstream of a 2dp column three
    /// migrations earlier.</para>
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// The currency <see cref="Amount"/> was typed in: the ledger's reporting currency at
    /// the time (migration 280, ADR-0114 D12). Stamped on every insert and every retype,
    /// and never rewritten when the reporting currency changes, so a target typed in the
    /// old currency converts at read time and changing the currency back reads it as
    /// typed. <c>required</c>, and the column has no default: a writer cannot leave it out.
    /// </summary>
    public required string CurrencyCode { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
