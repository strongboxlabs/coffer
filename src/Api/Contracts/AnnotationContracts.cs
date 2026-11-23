using System.Text.Json.Serialization;

using Coffer.Domain.Annotations;

namespace Coffer.Api.Contracts;

// =============================================================================
// Memo and tags per side: the frozen wire contract.
//
// The readers, the writers and the SPA are built against these, so the names and
// meanings are fixed here before any of them is wired. Each interface lists the
// fields a DTO gains when it is wired; the request types are used as they are.
// AnnotationContractTests pins every JSON name.
// =============================================================================

/// <summary>
/// The wire spelling of each <see cref="LegKind"/> (<c>notesKind</c>). The SPA
/// reads it, and <c>isReadOnly</c>, to decide which rows offer Edit and Duplicate,
/// instead of comparing posting counts.
/// </summary>
public static class NotesKinds
{
    public const string OnePosting = "onePosting";
    public const string EventCounterpartOne = "eventCounterpartOne";
    public const string SplitSide = "splitSide";
    public const string SplitOpposite = "splitOpposite";
    public const string EventSide = "eventSide";
    public const string EventOpposite = "eventOpposite";

    public static string Of(LegKind kind) => kind switch
    {
        LegKind.OnePosting => OnePosting,
        LegKind.EventCounterpartOne => EventCounterpartOne,
        LegKind.SplitSide => SplitSide,
        LegKind.SplitOpposite => SplitOpposite,
        LegKind.EventSide => EventSide,
        LegKind.EventOpposite => EventOpposite,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Every kind has a wire name."),
    };
}

/// <summary>
/// The memo-and-tags fields of a register row (<c>RegisterRowDto</c>) and of each
/// leg the <c>/legs</c> endpoint returns. Everything a row shows, filters on and
/// edits comes from <see cref="MemoTagRules"/>; the raw <c>legMemo</c> and
/// <c>legTags</c> leave the row, so the browser cannot bypass the rule.
/// </summary>
public interface IRegisterRowNotes
{
    /// <summary>What this register shows for the row.</summary>
    string? Memo { get; }

    /// <summary>What this register shows for the row; never null.</summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>The shown memo is the other side's; the row draws the "comes from the other side" marker.</summary>
    bool MemoInherited { get; }

    /// <inheritdoc cref="MemoInherited"/>
    bool TagsInherited { get; }

    /// <summary>What the row would show with its own memo blank, as the row is stored. The editors show it as the placeholder while the field is empty and the save keeps the leg it is on.</summary>
    string? InheritedMemo { get; }

    /// <inheritdoc cref="InheritedMemo"/>
    IReadOnlyList<string> InheritedTags { get; }

    /// <summary>The account an inherited value comes from ("from X").</summary>
    Guid? InheritFromAccountId { get; }

    /// <summary>The transaction's memo, only where the header is in play: the split-side transaction row and a multi-posting event.</summary>
    string? HeaderMemo { get; }

    /// <inheritdoc cref="HeaderMemo"/>
    IReadOnlyList<string>? HeaderTags { get; }

    /// <summary>One of <see cref="NotesKinds"/>.</summary>
    string NotesKind { get; }

    /// <summary>
    /// The row only displays here: Edit and Duplicate are not offered on it, and Delete is
    /// not either, as before this release (the row menus offer Show other side and Show raw
    /// data). A bulk delete, unhide or move whose explicit selection names it is refused.
    /// </summary>
    bool IsReadOnly { get; }
}

/// <summary>
/// The <c>notes</c> member of the bank <c>POST</c> and <c>PATCH /transactions</c>
/// bodies and of the reminder bank create, edit and fire bodies, replacing every
/// top-level and per-posting memo and tag field (no compatibility shim).
/// </summary>
/// <remarks>
/// <para><see cref="Memo"/> and <see cref="Tags"/> mean "this side's" when the
/// transaction ends with one posting and "the whole transaction's" when it ends with
/// several. <see cref="Lines"/> is refused on a one-posting result
/// (<c>line-notes-on-one-posting</c>), and a line that names no line of the
/// originating account, names one twice, or addresses an investment event is refused
/// with <c>line-notes-invalid</c>.</para>
/// <para>PRESENCE decides, as on <c>PatchTransactionRequest</c>: an omitted key
/// leaves the value alone; <c>null</c>, <c>""</c> or <c>[]</c> clear it, which on
/// a one-split means "inherit the other side's" (blank always means inherit). That
/// makes the old omitted-memo-becomes-null loss impossible to express.</para>
/// </remarks>
public sealed class TransactionNotesRequest
{
    private readonly string? _memo;
    private readonly IReadOnlyList<string>? _tags;

    /// <summary>
    /// The register the write is made from. Must equal <c>postings.sourceAccountId</c>
    /// when postings are sent; on a split it becomes the originating account.
    /// Required: a body without it is malformed (400), not a write from the empty
    /// account.
    /// </summary>
    public required Guid Side { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Memo
    {
        get => _memo;
        init { _memo = value; HasMemo = true; }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Tags
    {
        get => _tags;
        init { _tags = value; HasTags = true; }
    }

    /// <summary>Per-line notes of a multi-posting result, by posting index.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TransactionLineNotesRequest>? Lines { get; init; }

    /// <summary>True when the request CARRIED <c>memo</c>, whatever its value.</summary>
    [JsonIgnore] public bool HasMemo { get; private init; }

    /// <inheritdoc cref="HasMemo"/>
    [JsonIgnore] public bool HasTags { get; private init; }

    /// <summary>The memo as the planner reads it.</summary>
    public FieldEdit<string> MemoEdit() => NotesEdits.Memo(HasMemo, Memo);

    /// <summary>The tag names as the planner reads them, before they are resolved to tags.</summary>
    public FieldEdit<IReadOnlyList<string>> TagNamesEdit() => NotesEdits.Tags(HasTags, Tags);

    /// <summary>The whole body as the planner reads it, tag names not yet resolved.</summary>
    public AnnotationIntent<string> Intent() => new(
        Side, MemoEdit(), TagNamesEdit(),
        Lines?.Select(l => new LineIntent<string>(l.Index, l.MemoEdit(), l.TagNamesEdit())).ToList());

    /// <summary>Every tag name the body sets, the transaction's and the lines', for validation.</summary>
    public IEnumerable<string> AllTagNames() =>
        (Tags ?? []).Concat((Lines ?? []).SelectMany(l => l.Tags ?? []));
}

/// <summary>One line's notes in <see cref="TransactionNotesRequest.Lines"/>; same presence rules.</summary>
public sealed class TransactionLineNotesRequest
{
    private readonly string? _memo;
    private readonly IReadOnlyList<string>? _tags;

    /// <summary>The line's position in the postings sent (its posting index after the write).</summary>
    public int Index { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Memo
    {
        get => _memo;
        init { _memo = value; HasMemo = true; }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Tags
    {
        get => _tags;
        init { _tags = value; HasTags = true; }
    }

    [JsonIgnore] public bool HasMemo { get; private init; }
    [JsonIgnore] public bool HasTags { get; private init; }

    public FieldEdit<string> MemoEdit() => NotesEdits.Memo(HasMemo, Memo);
    public FieldEdit<IReadOnlyList<string>> TagNamesEdit() => NotesEdits.Tags(HasTags, Tags);
}

/// <summary>
/// The fields <c>MergeTagResponse</c> carries once the merge repoints saved
/// reports.
/// </summary>
public interface IMergeTagResponseNotes
{
    int TransactionsRepointed { get; }

    /// <summary>
    /// Saved reports repointed that the merging user can see: their own and the
    /// ledger-shared ones. Another member's private report is repointed but not
    /// counted, so the response never reveals that it exists.
    /// </summary>
    int SavedReportsRepointed { get; }
}

/// <summary>
/// The memo-and-tags fields of an MCP <c>list_transactions</c> row (and a budget
/// drill-down line), each line read as the register of the leg in scope shows it: the
/// scoped account's; with a category and no account, that category's; with neither,
/// the line's own account's, or for a line between two real accounts the posting's
/// outflow leg's (for a zero amount, the originating side's leg).
/// </summary>
public interface ITransactionLineNotes
{
    /// <summary>The memo the line's register shows; a split-side line's own memo.</summary>
    string? Memo { get; }

    /// <summary>The transaction's memo, on split-side lines only. Text search matches either memo.</summary>
    string? TransactionMemo { get; }

    /// <summary>The tags in play for the line: a split-side line counts the transaction's and its own.</summary>
    IReadOnlyList<string> Tags { get; }
}

/// <summary>
/// The memo-and-tags fields of the investment create and PATCH bodies
/// (<c>CreateInvestmentTransactionRequest</c>, <c>PatchInvestmentTransactionRequest</c>)
/// once the write primitive places them. The writing side is the request's
/// <c>brokerageAccountId</c>, the source brokerage of an in-kind transfer; the server
/// routes the value to the brokerage leg of a one-posting event or to the header of a
/// multi-posting one.
/// </summary>
/// <remarks>
/// <para><see cref="Memo"/> becomes PRESENCE-tracked, a breaking change for the PATCH:
/// today an omitted memo clears it. Afterwards an omitted memo leaves it, and
/// <c>null</c> or blank clears it, as on the bank <c>notes</c> body.</para>
/// <para><see cref="Tags"/> keeps its current meaning: <c>null</c> or omitted leaves
/// the tags, <c>[]</c> clears them, a list replaces them.</para>
/// <para><see cref="InvestmentNotesEdits"/> is the one reading of both.</para>
/// </remarks>
public interface IInvestmentWriteNotes
{
    string? Memo { get; }

    /// <summary>True when the body carried <c>memo</c>, whatever its value. Not on the wire.</summary>
    bool HasMemo { get; }

    IReadOnlyList<string>? Tags { get; }
}

/// <summary>How the planner reads <see cref="IInvestmentWriteNotes"/>.</summary>
public static class InvestmentNotesEdits
{
    public static FieldEdit<string> Memo(bool hasMemo, string? memo) => NotesEdits.Memo(hasMemo, memo);

    public static FieldEdit<IReadOnlyList<string>> Tags(IReadOnlyList<string>? tags) =>
        tags is null ? FieldEdit<IReadOnlyList<string>>.Leave
        : tags.Count == 0 ? FieldEdit<IReadOnlyList<string>>.Inherit
        : FieldEdit<IReadOnlyList<string>>.Set(tags);
}

/// <summary>
/// The MCP <c>set_transaction_tags</c> tool once it writes through the write
/// primitive (a breaking change to the tool).
/// </summary>
/// <remarks>
/// <para>It gains a REQUIRED <see cref="AccountIdParameter"/>: the register the tags
/// are written from, because the same transaction carries different tags on each
/// side. A transaction that register shows read-only is refused with
/// <see cref="ReadOnlyRowReason"/>, and one the account is not on with
/// <see cref="SideNotOnTransactionReason"/>. As with an id outside the ledger today,
/// one refusal rejects the whole call and nothing is tagged.</para>
/// <para>The dry run reports, per transaction, where the tags would land or why
/// they would not (<see cref="ISetTransactionTagsPlacement"/>).</para>
/// </remarks>
public static class SetTransactionTagsContract
{
    public const string AccountIdParameter = "accountId";
    public const string ReadOnlyRowReason = AnnotationViolationCodes.ReadOnlyRow;
    public const string SideNotOnTransactionReason = AnnotationViolationCodes.SideNotOnTransaction;

    /// <summary>The account's own side: its leg of a one-posting transaction, or the brokerage leg of a one-posting event.</summary>
    public const string LandsOnSide = "side";

    /// <summary>The whole transaction: the header of a multi-posting transaction or event.</summary>
    public const string LandsOnTransaction = "transaction";

    /// <summary>Where a write's tags land on the shape the transaction has.</summary>
    public static string LandsOn(TxnShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return shape.HeaderHeld ? LandsOnTransaction : LandsOnSide;
    }
}

/// <summary>One transaction of a <c>set_transaction_tags</c> dry run.</summary>
public interface ISetTransactionTagsPlacement
{
    Guid TransactionId { get; }

    /// <summary><see cref="SetTransactionTagsContract.LandsOnSide"/> or <see cref="SetTransactionTagsContract.LandsOnTransaction"/>; null when refused.</summary>
    string? LandsOn { get; }

    /// <summary>Null when the tags land; otherwise the reason, one of the contract's reason codes.</summary>
    string? Refused { get; }
}

/// <summary>The one implementation of <see cref="ISetTransactionTagsPlacement"/>.</summary>
public sealed record SetTransactionTagsPlacement(Guid TransactionId, string? LandsOn, string? Refused)
    : ISetTransactionTagsPlacement;

/// <summary>The presence rule shared by both request types.</summary>
internal static class NotesEdits
{
    public static FieldEdit<string> Memo(bool present, string? value) =>
        !present ? FieldEdit<string>.Leave
        : AnnotationText.NormalizeMemo(value) is { } memo ? FieldEdit<string>.Set(memo)
        : FieldEdit<string>.Inherit;

    public static FieldEdit<IReadOnlyList<string>> Tags(bool present, IReadOnlyList<string>? value) =>
        !present ? FieldEdit<IReadOnlyList<string>>.Leave
        : value is { Count: > 0 } ? FieldEdit<IReadOnlyList<string>>.Set(value)
        : FieldEdit<IReadOnlyList<string>>.Inherit;
}
