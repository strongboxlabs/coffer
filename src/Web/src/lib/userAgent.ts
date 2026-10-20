/**
 * A session's browser and system, as a person names them: "Firefox on Windows" (FU-087).
 *
 * Read from the user-agent string the session was signed in with. It is only a label for
 * telling one's own sessions apart, never a security signal: the browser chooses what it
 * sends. Order matters, because most strings claim to be several browsers at once: Edge
 * says Chrome and Safari, Chrome says Safari.
 */
export function describeUserAgent(userAgent: string | null): string {
    if (!userAgent) return 'Unknown device';

    const browser = /\bEdg(?:e|A|iOS)?\//.test(userAgent)
        ? 'Edge'
        : /\bOPR\//.test(userAgent)
          ? 'Opera'
          : /\bFirefox\/|\bFxiOS\//.test(userAgent)
            ? 'Firefox'
            : /\bChrome\/|\bCriOS\//.test(userAgent)
              ? 'Chrome'
              : /\bSafari\//.test(userAgent)
                ? 'Safari'
                : null;

    const system = /\bAndroid\b/.test(userAgent)
        ? 'Android'
        : /\biPhone\b|\biPad\b|\biPod\b/.test(userAgent)
          ? 'iOS'
          : /\bWindows\b/.test(userAgent)
            ? 'Windows'
            : /\bMac OS X\b|\bMacintosh\b/.test(userAgent)
              ? 'macOS'
              : /\bCrOS\b/.test(userAgent)
                ? 'ChromeOS'
                : /\bLinux\b/.test(userAgent)
                  ? 'Linux'
                  : null;

    if (browser && system) return `${browser} on ${system}`;
    return browser ?? system ?? 'Unknown device';
}
