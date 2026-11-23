namespace Coffer.Domain.Annotations;

/// <summary>
/// The text rules every annotation write shares: how blanks normalise, how memos
/// join and how two tag sets merge. The web editor's pre-join follows the
/// same vectors (<c>src/Web/src/lib/annotations/annotation-vectors.json</c>).
/// </summary>
public static class AnnotationText
{
    /// <summary>Between the transaction memo and a line's memo when a collapse to one line joins them.</summary>
    public const string CollapseSeparator = " · ";

    /// <summary>
    /// The one normalisation of stored free text: leading and trailing
    /// <see cref="char.IsWhiteSpace(char)"/> characters are trimmed, and what is
    /// left empty is no value at all (null). Null in, null out. Header payee, memo
    /// and check number go through it as well as every memo the write primitive
    /// stores, so the same keystrokes store the same bytes wherever they land.
    /// </summary>
    public static string? NormalizeText(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// A memo as stored: trimmed, and a blank one (empty, or only whitespace) is no
    /// memo, because blank always means inherit. Imported text is trimmed too.
    /// </summary>
    public static string? NormalizeMemo(string? memo) => NormalizeText(memo);

    /// <summary>
    /// Two memos as one: <see cref="JoinDistinctMemos"/> of the pair, so equal parts are
    /// kept once and different parts join first-then-second.
    /// </summary>
    public static string? JoinMemos(string? first, string? second) => JoinDistinctMemos([first, second]);

    /// <summary>
    /// Several memos as one: each part normalised (<see cref="NormalizeMemo"/>), a blank
    /// part dropped, a part equal to one already kept dropped, and the rest joined with
    /// <see cref="CollapseSeparator"/> in order; null when nothing is left. The comparison
    /// is ordinal over the trimmed text, so it is case-sensitive ("Trip" and "trip" are
    /// both kept), and a part is compared whole: one that already holds the separator is
    /// not split. Migration 264's <c>mig264_join_distinct</c> is the same rule.
    /// </summary>
    public static string? JoinDistinctMemos(IEnumerable<string?> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
            if (NormalizeMemo(part) is { } memo && seen.Add(memo))
                kept.Add(memo);
        return kept.Count == 0 ? null : string.Join(CollapseSeparator, kept);
    }

    /// <summary>
    /// Both sets in order, each tag once where it first appears. Tags are compared
    /// by equality only (ids on the server, resolved names in the editor): this
    /// never folds a name.
    /// </summary>
    public static IReadOnlyList<TTag> UnionTags<TTag>(IEnumerable<TTag> first, IEnumerable<TTag> second)
        where TTag : notnull
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return NormalizeTags(first.Concat(second));
    }

    /// <summary>A tag set as stored: each tag once, in order; a blank name is no tag.</summary>
    public static IReadOnlyList<TTag> NormalizeTags<TTag>(IEnumerable<TTag> tags)
        where TTag : notnull
    {
        ArgumentNullException.ThrowIfNull(tags);
        var seen = new HashSet<TTag>();
        var result = new List<TTag>();
        foreach (var tag in tags)
        {
            if (tag is string name && string.IsNullOrWhiteSpace(name)) continue;
            if (seen.Add(tag)) result.Add(tag);
        }
        return result;
    }
}
