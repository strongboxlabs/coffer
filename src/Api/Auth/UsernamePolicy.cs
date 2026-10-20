using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Coffer.Api.Auth;

/// <summary>
/// The one place that decides what a username may be (ADR-0089).
/// </summary>
/// <remarks>
/// <para>Before this existed the rule lived only in the SPA's setup form
/// (<c>^[a-z0-9_-]{3,32}$</c>) and nothing enforced it: <c>/setup/begin</c>
/// checked only non-empty + uniqueness, the invite form had no pattern at all,
/// and the DB had no CHECK. So an API caller or any invited user could already
/// create <c>someone@example.com</c> while the first user was refused it — with
/// a disabled button and no stated reason.</para>
///
/// <para><b>Permissive by intent.</b> A username may be an email address, a
/// handle, or a name in any script. We reject only what actually causes harm:
/// whitespace (indistinguishable copy/paste variants, invisible leading and
/// trailing padding), Unicode control and format characters (bidi overrides such
/// as U+202E let one username render as another), and absurd lengths.</para>
///
/// <para>Case-insensitivity is NOT enforced here — it belongs to the storage
/// layer, where it cannot be bypassed. <c>users.username</c> carries the ICU
/// <c>username_ci</c> collation (migration 187), which makes <c>=</c> and the
/// unique index fold case for every caller. Doing it in C# instead would leave
/// the invariant one forgotten <c>ToLower()</c> away from breaking, and would
/// depend on the process culture.</para>
/// </remarks>
public static class UsernamePolicy
{
    /// <summary>Shortest allowed username, in text elements.</summary>
    public const int MinLength = 3;

    /// <summary>
    /// Longest allowed username. Comfortably fits an email address (RFC 5321
    /// caps those at 254) without letting someone store an essay.
    /// </summary>
    public const int MaxLength = 254;

    /// <summary>
    /// Rejects any whitespace (<c>\s</c>) or Unicode control/format character
    /// (<c>\p{C}</c> — Cc, Cf, Co, Cs, Cn, which covers bidi overrides and
    /// zero-width joiners). Everything else printable is allowed.
    /// </summary>
    private static readonly Regex Disallowed =
        new(@"[\s\p{C}]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Canonical storage form: NFC-normalised and trimmed. Normalising matters
    /// for identity — "é" can arrive as U+00E9 or as "e" + U+0301, which are
    /// different byte sequences that must not become two accounts. Deliberately
    /// does NOT change case: folding is the collation's job.
    /// </summary>
    public static string Normalize(string username) =>
        (username ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);

    /// <summary>
    /// Validate an already-<see cref="Normalize"/>d username.
    /// </summary>
    /// <param name="username">The normalised candidate.</param>
    /// <param name="error">Human-readable reason, or null when valid.</param>
    /// <returns>True when the username is acceptable.</returns>
    public static bool IsValid(string username, out string? error)
    {
        if (string.IsNullOrEmpty(username))
        {
            error = "username is required.";
            return false;
        }

        // Count text elements, not UTF-16 code units, so an emoji or a combining
        // sequence isn't charged double against the limit.
        var length = new StringInfo(username).LengthInTextElements;
        if (length < MinLength)
        {
            error = $"username must be at least {MinLength} characters.";
            return false;
        }
        if (length > MaxLength)
        {
            error = $"username must be at most {MaxLength} characters.";
            return false;
        }

        if (Disallowed.IsMatch(username))
        {
            error = "username cannot contain spaces or invisible control characters.";
            return false;
        }

        error = null;
        return true;
    }
}
