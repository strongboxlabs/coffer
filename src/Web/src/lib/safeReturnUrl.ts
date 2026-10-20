/**
 * The address to send the browser to after signing in, when the server asked for one
 * — or null when it isn't safe to follow.
 *
 * The server sets `returnUrl` only when /oauth/authorize bounces an anonymous user to
 * sign in, so the only target ever needed is that endpoint on this origin. The check
 * used to be string prefixes ("starts with /, not // or /\"), which `/%09/evil.com`
 * passed: the browser drops the tab when it parses the URL and lands on `//evil.com`
 * (2026-10-12 security audit, S6). Parsing it the way the browser will, then requiring
 * this origin and that one path, leaves nothing to drop.
 */
export function safeReturnUrl(raw: string, origin: string = window.location.origin): string | null {
    let target: URL;
    try {
        target = new URL(raw, origin);
    } catch {
        return null;
    }
    if (target.origin !== origin || target.pathname !== '/oauth/authorize') return null;
    return target.pathname + target.search;
}
