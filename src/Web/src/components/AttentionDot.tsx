import { cn } from '@/lib/cn';
import type { AttentionSeverity } from '@/lib/useLedgerAttention';

/**
 * The problem indicator threaded through the navigation (FU-049).
 *
 * A problem used to be visible only if you were already looking at the screen that
 * shows it — the consistency verdict on Settings → General, notifications on Settings →
 * Notifications, both inside a ledger you had to have chosen first. So "which of my
 * ledgers needs me" had no answer anywhere, and "does this one need me" cost three
 * clicks and knowing where to look.
 *
 * Four surfaces show the same dot, so a problem can be followed DOWN rather than
 * hunted: the Coffer wordmark (any ledger), the Manage ledgers row (that ledger), the
 * Settings nav item (that ledger), and the Notifications tab (that ledger).
 *
 * The data comes from `useLedgerAttention`, which lives in its own module so this file
 * stays components-only.
 */

/** Sentence naming what is wrong, for readers who cannot use the colour. */
function describe(severity: AttentionSeverity, count: number | undefined, context: string) {
    const kind = severity === 'critical' ? 'critical problem' : 'problem';
    if (count === undefined) return `${context} needs attention: ${kind}s outstanding`;
    return `${context} needs attention: ${count} ${kind}${count === 1 ? '' : 's'} outstanding`;
}

export interface AttentionDotProps {
    /** Null renders nothing — absence of a problem is absence of an indicator. */
    severity: AttentionSeverity | null;
    /**
     * How many distinct subjects are outstanding. Omitted on the aggregate: a number
     * there would be a sum across ledgers, which answers a question nobody asks.
     */
    count?: number;
    /** What the dot is about, for the accessible label — a ledger name, or "Coffer". */
    context: string;
    className?: string;
}

/**
 * One dot. Never two — a ledger with drift AND a failed backup shows critical, because
 * two dots side by side is a puzzle rather than information.
 *
 * Colour is never the only channel, and here it is not even the main one. Warning is
 * an OUTLINE and critical is FILLED — a weight difference, which is what survives at
 * 10px and does not depend on separating two warm hues. That was settled on screen:
 * the first build used `state-warning` and `state-danger` as solid fills, both passed
 * their contrast floor, and they read as the same dot (ADR-0102 D4). `mark-*` are the
 * tokens for a shape rather than a word.
 *
 * The sentence rides along twice on top of that: a `title` for anyone who hovers, and
 * screen-reader-only text for anyone who cannot see it at all. Both name the severity
 * and the count.
 */
export function AttentionDot({ severity, count, context, className }: AttentionDotProps) {
    if (severity === null) return null;

    const label = describe(severity, count, context);

    return (
        <span className={cn('inline-flex shrink-0 items-center', className)}>
            <span
                aria-hidden
                title={label}
                className={cn(
                    'size-dot-md rounded-full',
                    severity === 'critical'
                        ? 'bg-mark-danger'
                        : 'border-2 border-mark-warning bg-transparent',
                )}
            />
            <span className="sr-only">{label}</span>
        </span>
    );
}

/**
 * The dot with its count beside it, for the surfaces where the number is actionable.
 *
 * The count is of distinct SUBJECTS, not of rows: the consistency monitor publishes on
 * every pass, so a ledger left drifting for six months has hundreds of rows and exactly
 * one problem. A number that climbs while nothing changes is how a reader learns to
 * stop reading it.
 */
export function AttentionCount({ severity, count, context, className }: AttentionDotProps) {
    if (severity === null) return null;

    return (
        <span className={cn('inline-flex shrink-0 items-center gap-1', className)}>
            <AttentionDot severity={severity} count={count} context={context} />
            {count === undefined ? null : (
                <span
                    aria-hidden
                    className={cn(
                        // The NUMBER keeps the state-* tokens: it is a word on a
                        // surface, which is what those are tuned for.
                        'text-[0.6875rem] font-medium leading-none',
                        severity === 'critical' ? 'text-state-danger' : 'text-state-warning',
                    )}
                >
                    {count}
                </span>
            )}
        </span>
    );
}
