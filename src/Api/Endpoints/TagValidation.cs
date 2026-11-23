using Coffer.Api.Errors;

using Coffer.Importer.Moneydance.Mappers;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Wire-level validation of the tag names a write carries — the one rule every
/// write surface enforces before the names reach the dictionary.
/// </summary>
/// <remarks>
/// <para>Moved out of <see cref="TransactionsEndpoints"/> when the investment
/// contracts gained a <c>Tags</c> field. Duplicating it would have put the cap
/// in two places, and a cap that disagrees with itself is worse than no cap:
/// the editor enforces the same 64-character limit client-side, so a divergence
/// would only ever show up through the API, on the surface nobody was
/// watching.</para>
///
/// <para>The number of tags is not checked here. A set may already hold more than
/// the limit (a system merge never drops a tag), and only GROWING a set past it is
/// refused, which needs the stored set: the write primitive decides that.</para>
///
/// <para>Persistence-side normalization (trim, one tag per name key,
/// create-on-first-use) lives in <c>TagDictionary</c>. This is the part that has
/// to REJECT rather than normalize.</para>
/// </remarks>
internal static class TagValidation
{
    private const int MaxTagNameLength = TagName.MaxLength;

    /// <summary>
    /// Per-tag validation (slice 2c.6b). An empty list is legal. Each name is
    /// trimmed; empty-after-trim is a hard 422 (no silent drops), and a name's length
    /// is capped to keep payloads predictable. Returns null when every name is
    /// acceptable.
    /// </summary>
    public static IResult? ValidateTags(IEnumerable<string?> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
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
