namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// The one place that says how long a tag name may be, and what to do with one
/// that is longer.
/// </summary>
/// <remarks>
/// <para>It lives in the IMPORTER, which is an odd-looking home for a rule the
/// API enforces, and it is deliberate: <c>Api</c> references
/// <c>Importer.Moneydance</c> and not the other way round, so this is the
/// lowest assembly both sides can see. The alternative was a second
/// <c>const int 64</c> in <c>TagsEndpoints</c>, which is what was here before
/// and is exactly the thing that drifts.</para>
///
/// <para><b>Why the limit needed enforcing at all.</b> <c>tags.name</c> is bare
/// <c>TEXT</c> with no CHECK, and the importer only ever trimmed — so a
/// Moneydance export could create tag names the application's own tag
/// management would refuse to create, and every later rename of one would be
/// judged against a limit the existing value already broke.</para>
///
/// <para><b>Truncate, not reject</b> — a maintainer decision, taken over the
/// alternative of failing the import. An import that refuses a file over a tag
/// name is a worse outcome than a shortened label, and the name is a label: no
/// identity, no reference, nothing downstream keys on it.</para>
///
/// <para><b>The cost, stated plainly:</b> two distinct names sharing their
/// first 64 characters collapse into ONE tag, and the rows carrying either end
/// up on the same tag. Nothing here prevents that, by design — see
/// <c>TagNameTruncationTests</c>, which pins it so it is a known consequence
/// rather than a surprise. Rejecting would have avoided it at the cost of
/// refusing the whole import.</para>
/// </remarks>
public static class TagName
{
    /// <summary>Longest tag name the product accepts, anywhere.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Trim, cap at <see cref="MaxLength"/>, then trim again; null when
    /// nothing is left.
    /// </summary>
    /// <remarks>
    /// The SECOND trim is the part worth noticing. Cutting at a fixed offset
    /// lands mid-word as often as not, and a name ending in a space would be
    /// stored with it — making two names that differ only by where the cut
    /// fell look identical in every UI while remaining distinct rows.
    /// </remarks>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length > MaxLength) trimmed = trimmed[..MaxLength].TrimEnd();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
