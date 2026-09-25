using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Wire-level validation of a header's tag list — the one rule both the bank
/// and the investment write surfaces enforce.
/// </summary>
/// <remarks>
/// <para>Moved out of <see cref="TransactionsEndpoints"/> when the investment
/// contracts gained a <c>Tags</c> field. Duplicating it would have put the caps
/// in two places, and a cap that disagrees with itself is worse than no cap:
/// the editor enforces the same 64 / 20 limits client-side, so a divergence
/// would only ever show up through the API, on the surface nobody was
/// watching.</para>
///
/// <para>Persistence-side normalization (trim, case-insensitive dedupe,
/// create-on-first-use) lives in <c>HeaderTags</c>. This is the part that has
/// to REJECT rather than normalize.</para>
/// </remarks>
internal static class TagValidation
{
    private const int MaxTagNameLength = 64;
    private const int MaxTagsPerHeader = 20;

    /// <summary>
    /// Per-tag validation (slice 2c.6b). Empty list is legal — it means "clear
    /// all tags." Each name is trimmed; empty-after-trim is a hard 422 (no
    /// silent drops). Name length and total count are capped to keep payloads
    /// predictable. Returns null when the list is acceptable.
    /// </summary>
    public static IResult? ValidateTags(IReadOnlyList<string> tags)
    {
        if (tags.Count > MaxTagsPerHeader)
            return BusinessError.Problem(
                BusinessError.Codes.TransactionTagsTooMany,
                $"At most {MaxTagsPerHeader} tags may be applied to one transaction.");
        foreach (var raw in tags)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return BusinessError.Problem(
                    BusinessError.Codes.TransactionTagEmpty,
                    "Tag names cannot be empty or whitespace-only.");
            if (trimmed.Length > MaxTagNameLength)
                return BusinessError.Problem(
                    BusinessError.Codes.TransactionTagTooLong,
                    $"Tag names must be {MaxTagNameLength} characters or fewer.");
        }
        return null;
    }
}
