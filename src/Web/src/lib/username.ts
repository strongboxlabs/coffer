/**
 * Client-side mirror of the API's `UsernamePolicy` (ADR-0089).
 *
 * The SERVER is the source of truth — this exists only so the user gets feedback
 * while typing instead of after a round-trip. Keep the constants in step with
 * `src/Api/Auth/UsernamePolicy.cs`.
 *
 * Permissive by intent: an email address, a handle, or a name in any script is
 * fine. The old rule (`^[a-z0-9_-]{3,32}$`) lived only in the setup form and was
 * enforced by neither the API nor the invite form, so the first user was refused
 * an email address that any invited user could already register — and the form
 * just disabled its submit button without saying why.
 *
 * Rejects only what actually harms a login identifier:
 *   - whitespace: invisible leading/trailing padding, and copy/paste variants
 *     that are indistinguishable on screen
 *   - Unicode control/format characters (`\p{C}`): bidi overrides such as U+202E
 *     let one username render as another; zero-width characters make two
 *     distinct usernames look identical
 *
 * Case is NOT restricted here. Folding happens in Postgres via the `username_ci`
 * ICU collation (migration 187), so `Ada` and `ada` are the same account
 * regardless of what any client does.
 */
export const USERNAME_MIN_LENGTH = 3;
export const USERNAME_MAX_LENGTH = 254;

const DISALLOWED = /[\s\p{C}]/u;

/** Canonical form: trimmed and NFC-normalised. Deliberately preserves case. */
export function normalizeUsername(raw: string): string {
    return raw.trim().normalize('NFC');
}

/**
 * @returns a human-readable reason the username is unacceptable, or null when
 * it's fine. Callers render the string next to the field — a disabled submit
 * button with no stated reason is indistinguishable from a broken app.
 */
export function usernameProblem(raw: string): string | null {
    const value = normalizeUsername(raw);
    if (value.length === 0) return 'Username is required.';

    // Count code points, not UTF-16 units, so an emoji or a combining sequence
    // isn't charged double against the limit.
    const length = [...value].length;
    if (length < USERNAME_MIN_LENGTH)
        return `Username must be at least ${USERNAME_MIN_LENGTH} characters.`;
    if (length > USERNAME_MAX_LENGTH)
        return `Username must be at most ${USERNAME_MAX_LENGTH} characters.`;

    if (DISALLOWED.test(value))
        return 'Username cannot contain spaces or invisible control characters.';

    return null;
}
