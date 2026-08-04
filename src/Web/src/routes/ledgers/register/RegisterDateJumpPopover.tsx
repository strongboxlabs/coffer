import { useEffect, useId, useRef, useState } from 'react';

import type { IndexBucketDto } from '@/lib/types/register';

/**
 * Cmd/Ctrl+J date-jump popover (ADR-0024 follow-up companion to
 * RegisterScrollTrack). Small floating dialog that accepts a date,
 * resolves it to the nearest month-with-activity bucket, and seeks
 * the windowed register there.
 *
 * Mounted always; visible only while open. Owns its own state +
 * keyboard handling so the parent page passes only `buckets` and
 * `onSeek`.
 */
export interface RegisterDateJumpPopoverProps {
    /** Same bucket list the scroll-track consumes. Empty / single-
     *  bucket histories suppress the shortcut (no UX value). */
    buckets: IndexBucketDto[];
    /** Same callback the scroll-track uses — receives the resolved
     *  bucket's `sampleHeaderId`. */
    onSeek: (sampleHeaderId: string) => void;
}

export function RegisterDateJumpPopover({
    buckets,
    onSeek,
}: RegisterDateJumpPopoverProps) {
    const [isOpen, setIsOpen] = useState(false);
    const [value, setValue] = useState('');
    const [error, setError] = useState<string | null>(null);
    const inputRef = useRef<HTMLInputElement | null>(null);
    const titleId = useId();

    // Global Cmd/Ctrl+J → toggle. Listening on `window` so the
    // shortcut works from any focused element in the page. Skips
    // when an input/textarea is focused so typing 'j' in the
    // editor doesn't pop the dialog.
    useEffect(() => {
        if (buckets.length < 2) return; // suppress shortcut when no nav value

        function onKey(e: KeyboardEvent) {
            if (e.key !== 'j' && e.key !== 'J') return;
            if (!(e.metaKey || e.ctrlKey)) return;

            const target = e.target as HTMLElement | null;
            const tag = target?.tagName?.toLowerCase();
            const isEditableTarget =
                tag === 'input' ||
                tag === 'textarea' ||
                tag === 'select' ||
                target?.isContentEditable === true;
            // Don't hijack typing inside the popover's own input —
            // but DO trigger from anywhere else, including other inputs.
            // For now keep the simple rule: never hijack when inside
            // an editable. The shortcut works when focus is on the
            // table / scroll track / page chrome.
            if (isEditableTarget && !isOpen) return;

            e.preventDefault();
            setIsOpen((prior) => !prior);
        }

        window.addEventListener('keydown', onKey);
        return () => window.removeEventListener('keydown', onKey);
    }, [buckets.length, isOpen]);

    // Focus the input when the dialog opens; clear value + error on close.
    useEffect(() => {
        if (isOpen) {
            inputRef.current?.focus();
            inputRef.current?.select();
        } else {
            setValue('');
            setError(null);
        }
    }, [isOpen]);

    // Escape closes; click outside closes (handled below via backdrop).
    useEffect(() => {
        if (!isOpen) return;
        function onKey(e: KeyboardEvent) {
            if (e.key === 'Escape') {
                e.preventDefault();
                setIsOpen(false);
            }
        }
        window.addEventListener('keydown', onKey);
        return () => window.removeEventListener('keydown', onKey);
    }, [isOpen]);

    function handleSubmit(e: React.FormEvent<HTMLFormElement>) {
        e.preventDefault();
        if (!value) {
            setError('Pick a date.');
            return;
        }
        // value is YYYY-MM-DD from <input type="date">. We only care
        // about year+month; the day is dropped.
        const yearMonth = value.slice(0, 7);
        const target = findNearestBucket(buckets, yearMonth);
        if (!target) {
            setError('No transactions near that date.');
            return;
        }
        onSeek(target.sampleHeaderId);
        setIsOpen(false);
    }

    if (!isOpen) return null;

    return (
        <>
            {/* Backdrop catches outside-clicks. Transparent — we don't
                dim the page since the popover is a transient inline
                navigation aid, not a modal. */}
            <div
                aria-hidden
                className="fixed inset-0 z-40"
                onClick={() => setIsOpen(false)}
            />
            <div
                role="dialog"
                aria-modal="true"
                aria-labelledby={titleId}
                className="
                    fixed top-16 right-6 z-50
                    rounded-md border border-border bg-surface
                    shadow-md p-3
                    flex flex-col gap-2
                    min-w-[14rem]
                "
                onClick={(e) => e.stopPropagation()}
            >
                <div id={titleId} className="text-xs font-semibold text-text-muted">
                    Jump to date
                </div>
                <form onSubmit={handleSubmit} className="flex flex-col gap-2">
                    <input
                        ref={inputRef}
                        type="date"
                        value={value}
                        onChange={(e) => {
                            setValue(e.target.value);
                            setError(null);
                        }}
                        className="
                            rounded border border-border bg-surface
                            px-2 py-1 text-sm
                            focus:outline-none focus:ring-1 focus:ring-accent
                        "
                        aria-invalid={error != null}
                        aria-describedby={error != null ? `${titleId}-err` : undefined}
                    />
                    {error != null ? (
                        <span
                            id={`${titleId}-err`}
                            role="alert"
                            className="text-[0.6875rem] text-state-danger"
                        >
                            {error}
                        </span>
                    ) : null}
                    <div className="flex justify-end gap-2 text-[0.6875rem]">
                        <button
                            type="button"
                            onClick={() => setIsOpen(false)}
                            className="rounded px-2 py-0.5 text-text-muted hover:text-text"
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            className="rounded bg-accent px-2 py-0.5 text-accent-foreground hover:opacity-90"
                        >
                            Jump
                        </button>
                    </div>
                </form>
                <div className="text-[0.6875rem] text-text-muted">
                    <kbd className="rounded border border-border px-1">Esc</kbd>{' '}
                    to close ·{' '}
                    <kbd className="rounded border border-border px-1">Enter</kbd>{' '}
                    to jump
                </div>
            </div>
        </>
    );
}

/**
 * Find the bucket closest to the target `yearMonth` (yyyy-MM string).
 * Exact match wins; otherwise picks the nearest by month delta. Returns
 * null only when the bucket list is empty.
 */
function findNearestBucket(
    buckets: IndexBucketDto[],
    targetYearMonth: string,
): IndexBucketDto | null {
    if (buckets.length === 0) return null;
    const exact = buckets.find((b) => b.yearMonth === targetYearMonth);
    if (exact) return exact;

    const targetMonths = yearMonthToAbsoluteMonths(targetYearMonth);
    if (targetMonths == null) return buckets[0]!;

    let best = buckets[0]!;
    let bestDelta = Math.abs(
        yearMonthToAbsoluteMonths(best.yearMonth)! - targetMonths,
    );
    for (let i = 1; i < buckets.length; i++) {
        const m = yearMonthToAbsoluteMonths(buckets[i].yearMonth);
        if (m == null) continue;
        const delta = Math.abs(m - targetMonths);
        if (delta < bestDelta) {
            bestDelta = delta;
            best = buckets[i]!;
        }
    }
    return best;
}

function yearMonthToAbsoluteMonths(yearMonth: string): number | null {
    const [y, m] = yearMonth.split('-');
    const year = Number.parseInt(y, 10);
    const month = Number.parseInt(m, 10);
    if (!Number.isFinite(year) || !Number.isFinite(month)) return null;
    return year * 12 + (month - 1);
}
