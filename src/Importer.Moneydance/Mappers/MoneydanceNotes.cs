using Coffer.Domain.Annotations;
using Coffer.Importer.Moneydance.Json.Typed;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// What Moneydance's memo and tag fields mean on each side of a transaction, planned
/// through the write primitive (<see cref="AnnotationPlanner"/>), which decides where
/// each value may sit. Shared by the bank route, reminder templates and the investment
/// route, so a template and the transactions fired from it place their values alike.
/// </summary>
/// <remarks>
/// <para><b>Moneydance's model.</b> On a one-split, the side the transaction was entered
/// from holds <c>memo</c> and <c>0.tags</c>; the other side's memo is <c>0.desc</c>, which
/// Moneydance fills with a copy of <c>desc</c> (the payee) unless the user typed one, and
/// its tags are the transaction's <c>tags</c>. On a multi-split, <c>memo</c> is the whole
/// transaction's, <c>N.desc</c> and <c>N.tags</c> are line N's on the entered side, and
/// <c>tags</c> is ONE set shared by every line's other side; there are no transaction-level
/// tags on the entered side.</para>
/// <para><b>Placement.</b> One-split: the home leg gets the memo and <c>0.tags</c>, the other
/// leg <c>0.desc</c> only where it is the user's own (non-blank and different from both
/// <c>desc</c> and the memo written) and <c>tags</c>. Multi-split: the header gets the memo,
/// each line's home leg <c>N.desc</c> and <c>N.tags</c> with <c>tags</c> merged in, because
/// the other side of a split line is read-only and could never edit the shared set; the
/// other legs get nothing and show their line's value inherited. An event (a brokerage
/// cash move, any investment transaction) shows one memo and one tag set, so every value
/// that would sit on its read-only side is merged into it.</para>
/// <para><b>Text.</b> Every memo is trimmed and a blank one is no memo; comparisons are
/// ordinal over trimmed text. System merges are never capped and never drop a tag.</para>
/// </remarks>
public static class MoneydanceNotes
{
    /// <summary>One posting of a bank-route transaction: the split and the account on its other side.</summary>
    public sealed record BankPosting(MdSplit Split, Guid CounterpartAccountId);

    /// <summary>
    /// The memo the importer writes as the transaction's own: the user's <c>memo</c>, else
    /// the feed's original memo, trimmed.
    /// </summary>
    public static string? WrittenMemo(string? memo, string? feedMemo) =>
        AnnotationText.NormalizeMemo(memo) ?? AnnotationText.NormalizeMemo(feedMemo);

    /// <summary>
    /// A one-split's <c>0.desc</c> as the other side's own memo, or null where it is no
    /// value of the user's: blank, or equal to <c>desc</c> (Moneydance's default copy) or
    /// to the memo written on the entered side (same display either way, and the other
    /// side keeps following later edits).
    /// </summary>
    public static string? CounterpartMemo(string? splitDescription, string? description, string? writtenMemo)
    {
        var own = AnnotationText.NormalizeMemo(splitDescription);
        if (own is null) return null;
        if (string.Equals(own, AnnotationText.NormalizeText(description), StringComparison.Ordinal)) return null;
        if (string.Equals(own, writtenMemo, StringComparison.Ordinal)) return null;
        return own;
    }

    /// <summary>A comma-separated Moneydance tag list as names, in order, each once.</summary>
    /// <remarks>
    /// Each name goes through <see cref="TagName.Normalize"/> here rather than at the
    /// insert, because these names are the keys the tag links are later resolved by; a
    /// name shortened only at the insert would miss its own lookup. Names are kept as
    /// spelled: which of two spellings of one name a ledger keeps is decided by the
    /// database's one key, at the insert.
    /// </remarks>
    public static IReadOnlyList<string> TagNames(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in raw.Split(','))
            if (TagName.Normalize(piece) is { } name && seen.Add(name))
                names.Add(name);
        return names;
    }

    /// <summary>
    /// Plan the notes of a bank-route transaction or reminder template entered from
    /// <paramref name="home"/>.
    /// </summary>
    /// <param name="shape">The mapped shape (<see cref="Db.ImportAnnotations.ShapeOf"/>).</param>
    /// <param name="writtenMemo">The transaction's memo as written (<see cref="WrittenMemo"/>).</param>
    /// <param name="description">The transaction's raw <c>desc</c>.</param>
    /// <param name="transactionTags">The transaction's raw <c>tags</c>.</param>
    /// <param name="postings">The emitted postings, in posting order.</param>
    public static AnnotationPlan<string> PlanBankRoute(
        TxnShape shape,
        Guid home,
        string? writtenMemo,
        string? description,
        string? transactionTags,
        IReadOnlyList<BankPosting> postings)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(postings);

        var sharedTags = TagNames(transactionTags);

        if (postings.Count > 1)
        {
            var lines = postings
                .Select(p => new LineIntent<string>(
                    p.Split.Index,
                    Memo(p.Split.Description),
                    Tags(AnnotationText.UnionTags(TagNames(p.Split.Tags), sharedTags))))
                .ToList();
            return AnnotationPlanner.Seed(shape, new AnnotationIntent<string>(
                home, Memo(writtenMemo), FieldEdit<IReadOnlyList<string>>.Leave, lines), TagLimit.SystemMerge);
        }

        var posting = postings.Single();
        var counterpartMemo = CounterpartMemo(posting.Split.Description, description, writtenMemo);
        var entered = TagNames(posting.Split.Tags);

        if (shape.IsEvent)
        {
            // The other side of a one-posting event is read-only, so what Moneydance
            // keeps for it joins the event's own value.
            return AnnotationPlanner.Seed(shape, new AnnotationIntent<string>(
                home,
                Memo(AnnotationText.JoinMemos(writtenMemo, counterpartMemo)),
                Tags(AnnotationText.UnionTags(entered, sharedTags))), TagLimit.SystemMerge);
        }

        var seeded = AnnotationPlanner.Seed(shape, new AnnotationIntent<string>(
            home, Memo(writtenMemo), Tags(entered)), TagLimit.SystemMerge);
        if (!seeded.IsValid || (counterpartMemo is null && sharedTags.Count == 0)) return seeded;

        // The other side writes its own values, as it would from its own register.
        return AnnotationPlanner.Plan(
            ShapeChange<string>.Unchanged(shape.WithOriginating(seeded.OriginatingAccountId), seeded.Result),
            new AnnotationIntent<string>(
                posting.CounterpartAccountId,
                counterpartMemo is null ? FieldEdit<string>.Leave : Memo(counterpartMemo),
                sharedTags.Count == 0 ? FieldEdit<IReadOnlyList<string>>.Leave : Tags(sharedTags)),
            TagLimit.SystemMerge);
    }

    private static FieldEdit<string> Memo(string? memo) =>
        memo is null ? FieldEdit<string>.Inherit : FieldEdit<string>.Set(memo);

    private static FieldEdit<IReadOnlyList<string>> Tags(IReadOnlyList<string> tags) =>
        tags.Count == 0 ? FieldEdit<IReadOnlyList<string>>.Inherit : FieldEdit<IReadOnlyList<string>>.Set(tags);

    /// <summary>
    /// The one memo and tag set of a Moneydance investment transaction: the memo written,
    /// then each non-security split's <c>desc</c> that is the user's own (non-blank and
    /// different from <c>desc</c>, the payee), in split order, each distinct memo once
    /// (<see cref="AnnotationText.JoinDistinctMemos"/>); and every tag the transaction or
    /// any split carries.
    /// </summary>
    public static Notes<string> InvestmentEvent(MdTxn txn, string? writtenMemo)
    {
        ArgumentNullException.ThrowIfNull(txn);
        var payee = AnnotationText.NormalizeText(txn.Description);
        var parts = new List<string?> { writtenMemo };
        foreach (var split in txn.Splits)
        {
            if (split.InvestSplitType == "sec") continue;
            var own = AnnotationText.NormalizeMemo(split.Description);
            if (own is null || string.Equals(own, payee, StringComparison.Ordinal)) continue;
            parts.Add(own);
        }
        var memo = AnnotationText.JoinDistinctMemos(parts);

        var tags = TagNames(txn.Tags);
        foreach (var split in txn.Splits)
            tags = AnnotationText.UnionTags(tags, TagNames(split.Tags));
        return new Notes<string>(memo, tags);
    }

    /// <summary>Plan an investment event's one value onto <paramref name="shape"/>, entered from its brokerage.</summary>
    public static AnnotationPlan<string> PlanEvent(TxnShape shape, Guid brokerage, Notes<string> value)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(value);
        return AnnotationPlanner.Seed(shape, new AnnotationIntent<string>(
            brokerage, Memo(value.Memo), Tags(value.Tags)), TagLimit.SystemMerge);
    }
}
