namespace Coffer.Domain.Annotations;

public enum FieldEditKind
{
    /// <summary>The request did not carry the field: keep what is there.</summary>
    Leave,

    /// <summary>The request carried a value.</summary>
    Set,

    /// <summary>The request carried null, blank or an empty list: this side has no value of its own.</summary>
    Inherit,
}

/// <summary>
/// One field of an annotation write, with presence semantics: omitted, set, or
/// cleared. Clearing is how a side goes back to inheriting: blank always means
/// inherit, there is no explicit "none".
/// </summary>
public readonly record struct FieldEdit<T>(FieldEditKind Kind, T? Value)
{
    public static FieldEdit<T> Leave => new(FieldEditKind.Leave, default);
    public static FieldEdit<T> Inherit => new(FieldEditKind.Inherit, default);
    public static FieldEdit<T> Set(T value) => new(FieldEditKind.Set, value);
}

/// <summary>An edit to one line of a multi-posting transaction.</summary>
/// <param name="PostingIndex">The line, by its posting index in the shape after the write.</param>
public sealed record LineIntent<TTag>(
    int PostingIndex,
    FieldEdit<string> Memo,
    FieldEdit<IReadOnlyList<TTag>> Tags)
    where TTag : notnull;

/// <summary>
/// What one side asks to write. <see cref="Memo"/> and <see cref="Tags"/> mean
/// "this side's" on a one-posting result and "the whole transaction's" on a
/// multi-posting one; <see cref="Lines"/> address the lines of a multi-posting one.
/// </summary>
/// <param name="Side">The account the write is made from. On a split it becomes the originating account:
/// a reshape hands ownership to the side that makes it.</param>
public sealed record AnnotationIntent<TTag>(
    Guid Side,
    FieldEdit<string> Memo,
    FieldEdit<IReadOnlyList<TTag>> Tags,
    IReadOnlyList<LineIntent<TTag>>? Lines = null)
    where TTag : notnull
{
    /// <summary>A write that changes no annotation: a reshape, an untouched save.</summary>
    public static AnnotationIntent<TTag> Untouched(Guid side) =>
        new(side, FieldEdit<string>.Leave, FieldEdit<IReadOnlyList<TTag>>.Leave);
}

/// <summary>
/// A transaction's structure before and after a write, its stored notes, and
/// which leg of the result each surviving leg was.
/// </summary>
/// <param name="Survivors">After-key to before-key, for each leg of
/// <paramref name="After"/> that is a leg of <paramref name="Before"/>. Values follow
/// their leg, so this map is what keeps an override through a reorder or a
/// recategorize, and what drops a removed line's values.</param>
public sealed record ShapeChange<TTag>(
    TxnShape Before,
    Annotations<TTag> Current,
    TxnShape After,
    IReadOnlyDictionary<LegKey, LegKey> Survivors)
    where TTag : notnull
{
    /// <summary>A write that does not touch the structure.</summary>
    public static ShapeChange<TTag> Unchanged(TxnShape shape, Annotations<TTag> current) =>
        new(shape, current, shape, shape.Legs.ToDictionary(l => l.Key, l => l.Key));

    /// <summary>Legs survive by stored id: the bank PATCH, which names the legs it keeps.</summary>
    public static IReadOnlyDictionary<LegKey, LegKey> ByLegId(TxnShape before, TxnShape after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var byId = before.Legs.Where(l => l.LegId is not null).ToDictionary(l => l.LegId!.Value, l => l.Key);
        return after.Legs
            .Where(l => l.LegId is { } id && byId.ContainsKey(id))
            .ToDictionary(l => l.Key, l => byId[l.LegId!.Value]);
    }

    /// <summary>
    /// Legs survive by (account, role): the investment edit, which rebuilds its legs.
    /// Several legs with the same account and role pair up in posting order. A
    /// bank-shape leg has no role yet, so when a transaction becomes an event its
    /// legs survive by account alone.
    /// </summary>
    public static IReadOnlyDictionary<LegKey, LegKey> ByAccountAndRole(TxnShape before, TxnShape after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var pool = before.Legs
            .GroupBy(l => (l.Key.AccountId, l.Role))
            .ToDictionary(g => g.Key, g => new Queue<LegKey>(g.OrderBy(l => l.Key.PostingIndex).Select(l => l.Key)));
        var survivors = new Dictionary<LegKey, LegKey>();
        foreach (var leg in after.Legs.OrderBy(l => l.Key.PostingIndex))
        {
            if (Take(leg.Key.AccountId, leg.Role) is { } exact) survivors[leg.Key] = exact;
            else if (leg.Role is not null && Take(leg.Key.AccountId, null) is { } roleless) survivors[leg.Key] = roleless;
        }
        return survivors;

        LegKey? Take(Guid account, string? role) =>
            pool.TryGetValue((account, role), out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
    }
}

/// <summary>Whether a write may grow a tag set past the limit.</summary>
public enum TagLimit
{
    /// <summary>A user's edit: growing a set past the limit is refused; an unchanged or smaller one saves.</summary>
    Enforce,

    /// <summary>A system merge (Moneydance union, convert in-kind, a clone): never capped, never dropped.</summary>
    SystemMerge,
}

/// <summary>A reason the write is refused; each is a 422 code at the API.</summary>
public sealed record AnnotationViolation(string Code, string Detail);

/// <summary>The 422 codes a plan can carry. The API's <c>BusinessError.Codes</c> spells the same strings.</summary>
public static class AnnotationViolationCodes
{
    public const string ReadOnlyRow = "read-only-row";
    public const string SideNotOnTransaction = "side-not-on-transaction";
    public const string LineNotesOnOnePosting = "line-notes-on-one-posting";
    public const string LineNotesInvalid = "line-notes-invalid";
    public const string TagsTooMany = "transaction-tags-too-many";
}

/// <summary>What a write should store, and who owns the transaction afterwards.</summary>
/// <param name="Result">The notes of the header and of every leg of the shape after the write.
/// Never write a plan with <see cref="Violations"/>.</param>
/// <param name="OriginatingAccountId">The originating account after the write: the account the
/// transaction was entered from, or the side that last reshaped it.</param>
public sealed record AnnotationPlan<TTag>(
    Annotations<TTag> Result,
    Guid? OriginatingAccountId,
    IReadOnlyList<AnnotationViolation> Violations)
    where TTag : notnull
{
    public bool IsValid => Violations.Count == 0;
}
