import { cn } from '@/lib/cn';

// What the three lists of Structure share that is a value rather than a component:
// how a row's name link is drawn and how a search box's text becomes a test. Components
// live beside it; a file that exports both would stop React refreshing on an edit.

/**
 * The class of a row's name link: quiet, and accent and underlined on hover. An inactive
 * row's is muted and struck through, so it reads as set aside without the colour carrying
 * the meaning alone (the Inactive badge says it in words).
 */
export function rowLinkClass(inactive = false): string {
    return cn(
        'truncate text-sm hover:text-accent hover:underline',
        inactive ? 'text-text-subtle line-through' : 'text-text',
    );
}

/** A search box's text as the lists compare it: trimmed, and with case ignored. */
export function normalizeQuery(raw: string): string {
    return raw.trim().toLowerCase();
}

/**
 * Whether any of the texts contains the (already normalised) query. An empty query matches
 * everything, so a list can filter unconditionally.
 */
export function matchesQuery(query: string, ...texts: ReadonlyArray<string | null | undefined>): boolean {
    if (query === '') return true;
    return texts.some((t) => t != null && t.toLowerCase().includes(query));
}

/** "1 transaction", "12 transactions": a count a reader can say aloud. */
export function transactionCountLabel(n: number): string {
    return `${n} transaction${n === 1 ? '' : 's'}`;
}
