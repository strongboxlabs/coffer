import type { Components } from 'react-virtuoso';

// Shared Virtuoso timeline sentinels (ADR-0030 reuse). The
// `─── Newest transaction ───` / `─── Oldest transaction · DATE ───`
// edge markers — honest cues for "yes, this really is the end" so the
// user isn't second-guessing whether the scrollbar means "absolute
// end" or "edge of the loaded window." Bank shipped these; investment
// had none. Extracting the builder means both registers render the
// identical sentinels off the same `atTimelineHead`/`atTimelineTail`
// flags from `useRegisterController`.

const SENTINEL_CLASS =
    'border-y border-border/40 bg-surface-muted/30 py-2 text-center text-[0.625rem] uppercase tracking-wider text-text-subtle';

export interface TimelineSentinelArgs {
    /** True when the window covers the absolute timeline head. */
    atTimelineHead: boolean;
    /** True when the window covers the absolute timeline tail. */
    atTimelineTail: boolean;
    /** Optional date label appended to the oldest sentinel (the tail
     *  entry's posted date) so the user has a date anchor at the end.
     *  Null omits the ` · DATE` suffix. */
    oldestLabel: string | null;
}

/**
 * Build the `{ Header, Footer }` object for Virtuoso's `components`
 * prop. Header renders the "Newest transaction" sentinel only when
 * `atTimelineHead`; Footer renders the "Oldest transaction" sentinel
 * only when `atTimelineTail`. Returns `undefined` for an edge that
 * isn't reached (Virtuoso treats an undefined Header/Footer as none).
 *
 * Callers memoize the result on the three args so Virtuoso doesn't
 * re-mount the Header/Footer on every parent render.
 */
export function buildTimelineSentinels<TData>({
    atTimelineHead,
    atTimelineTail,
    oldestLabel,
}: TimelineSentinelArgs): Pick<Components<TData>, 'Header' | 'Footer'> {
    return {
        Header: atTimelineHead
            ? () => (
                <div role="presentation" className={SENTINEL_CLASS}>
                    ─── Newest transaction ───
                </div>
            )
            : undefined,
        Footer: atTimelineTail
            ? () => (
                <div role="presentation" className={SENTINEL_CLASS}>
                    ─── Oldest transaction
                    {oldestLabel ? ` · ${oldestLabel}` : ''}{' '}
                    ───
                </div>
            )
            : undefined,
    };
}
