import { useCallback, useEffect, useState } from 'react';

/**
 * A collapsed/expanded flag that survives a reload, kept per browser.
 *
 * Extracted from {@link LedgerHubSection}, which had the only copy, when the
 * Categories screen needed the same behaviour (FU-060). A second hand-rolled
 * `localStorage` boolean would have been the start of two of them drifting —
 * in particular over the try/catch, which is not optional: `localStorage`
 * throws rather than returning null in a private window and in some test
 * environments, and an uncaught throw here takes the whole panel down.
 *
 * @param storageKey Must be stable across renders. A key that changes between
 *   renders silently loses the stored preference and reads as "collapse does
 *   not stick", which is tedious to trace back to a template literal.
 * @param defaultExpanded Used on first mount, when nothing is stored, and
 *   whenever the read fails.
 */
export function usePersistedDisclosure(
    storageKey: string,
    defaultExpanded = true,
): readonly [boolean, () => void] {
    // Lazy initializer, so the first paint already matches the stored value
    // rather than flashing the default and correcting itself in an effect.
    const [expanded, setExpanded] = useState<boolean>(() => {
        if (typeof window === 'undefined') return defaultExpanded;
        try {
            const stored = window.localStorage.getItem(storageKey);
            if (stored === null) return defaultExpanded;
            return stored === 'true';
        } catch {
            return defaultExpanded;
        }
    });

    useEffect(() => {
        try {
            window.localStorage.setItem(storageKey, String(expanded));
        } catch {
            // Ignore — the preference just will not survive a reload.
        }
    }, [storageKey, expanded]);

    const toggle = useCallback(() => setExpanded((v) => !v), []);
    return [expanded, toggle] as const;
}
