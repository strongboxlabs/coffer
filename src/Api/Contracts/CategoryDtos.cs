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
    decimal Total);

/// <summary>Reparent a category under a new parent; <c>null</c> moves it to the
/// top level. The parent must be a category of the same kind in the ledger; the
/// server rejects cycles.</summary>
public sealed record ReparentCategoryRequest(Guid? ParentId);

/// <summary>Merge one category into another of the same kind. <see cref="DryRun"/>
/// returns the counts that would move without writing (preview).</summary>
public sealed record MergeCategoryRequest(Guid TargetId, bool DryRun = false);

/// <summary>Echo of a category merge — counts moved (or that would move, when
/// <see cref="DryRun"/>).</summary>
public sealed record MergeCategoryResponse(
    int TransactionsMoved, int ChildrenReparented, bool DryRun);
