namespace Coffer.Api.Contracts;

/// <summary>
/// One category in the management tree (Slice A). Categories are accounts
/// (<c>account_type='category'</c>); this read carries the hierarchy pointer plus
/// the usage counts the manage-categories UI needs to render the tree, show a
/// usage hint, and gate Delete. <see cref="TransactionCount"/> and
/// <see cref="ChildCount"/> mirror the <c>DeleteCategoryAsync</c> gate (any
/// reference blocks a hard delete), so the UI can pre-disable Delete; the server
/// remains authoritative. <see cref="Total"/> is the raw signed sum of the
/// category's own leg amounts (expense categories net positive, income net
/// negative, per the double-entry sign convention) — the UI normalizes the sign
/// per kind for display.
/// </summary>
public sealed record CategoryNode(
    Guid Id,
    string Name,
    string CategoryKind,
    Guid? ParentId,
    bool IsActive,
    bool IsSystem,
    int TransactionCount,
    int ChildCount,
    decimal? Total,
    /// <summary>Every missing rate <see cref="Total"/> needed, which is then null
    /// (ADR-0114 D9); absent from the JSON when none is.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? RateUnavailable = null);

/// <summary>
/// The management tree's read: every category, with the currency their figures are
/// in. A category has no currency of its own, since its lines carry the paying
/// account's (ADR-0109 D9), so each <see cref="CategoryNode.Total"/> is converted into
/// the ledger's reporting currency, <see cref="CurrencyCode"/>, each leg at its
/// transaction's date (ADR-0114 D7).
/// </summary>
public sealed record CategoryListDto(
    IReadOnlyList<CategoryNode> Categories,
    string CurrencyCode);

/// <summary>Reparent a category under a new parent; <c>null</c> moves it to the
/// top level. The parent must be a category of the same kind in the ledger; the
/// server rejects cycles.</summary>
public sealed record ReparentCategoryRequest(Guid? ParentId);

/// <summary>Merge one category into another, of any kind: the target must not sit beneath
/// the source. <see cref="DryRun"/> returns the counts that would move without writing
/// (preview).</summary>
public sealed record MergeCategoryRequest(Guid TargetId, bool DryRun = false);

/// <summary>Echo of a category merge — counts moved (or that would move, when
/// <see cref="DryRun"/>).</summary>
/// <param name="BudgetTargetsMoved">Budget targets of the source the destination kept, summed
/// into its own where both held one for the same month (ADR-0099). Zero on a dry run, which does
/// not read them.</param>
/// <param name="BudgetTargetsDropped">Targets dropped because a category above them already held
/// one for that month: anything beneath the destination (the source's sub-categories and its own)
/// against the destination's, and what arrived against the destination's ancestors'. Zero on a
/// dry run.</param>
public sealed record MergeCategoryResponse(
    int TransactionsMoved, int ChildrenReparented, bool DryRun,
    int BudgetTargetsMoved = 0, int BudgetTargetsDropped = 0);
