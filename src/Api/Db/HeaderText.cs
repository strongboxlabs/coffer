namespace Coffer.Api.Db;

/// <summary>
/// Normalize a header's free-text fields — payee, memo, check number — on the
/// way into storage.
/// </summary>
/// <remarks>
/// <para>Trim, and treat whitespace-only as absent. The rule is not interesting;
/// where it lives is. The bank EDITOR did this six times inline
/// (<c>payee.trim().length === 0 ? null : payee.trim()</c>) and the investment
/// editor did not (<c>draft.payee || null</c>, where <c>"  "</c> is truthy), so
/// the same keystrokes stored different bytes depending on which register you
/// happened to be on — and a padded payee is a DIFFERENT payee to every
/// grouping key in the app: the payee vocabulary, similar-payees recall (which
/// matches feed payees exactly), and the register's payee search.</para>
///
/// <para>Fixing it in the investment editor would have made the two clients
/// agree and left the contract untouched — MCP, a direct PATCH or a script
/// could still store <c>"Acme  "</c>, and the next client would be the third
/// place to get it right. Normalizing at the point of PERSISTENCE means there is
/// one place, it cannot be bypassed, and a client that forgets is merely
/// redundant rather than wrong.</para>
///
/// <para>Deliberately NOT applied to leg memos on the bank path: those are
/// trimmed by the editor today, and moving them here is a behaviour change to a
/// field this audit did not cover. Header text is what diverged.</para>
/// </remarks>
internal static class HeaderText
{
    /// <summary>
    /// The value to store for a free-text header field: trimmed, or null when
    /// it is empty or whitespace-only. Null in, null out — an absent field
    /// stays absent rather than becoming an empty string.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
