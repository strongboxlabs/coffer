import { useEffect, useMemo, useRef, useState } from 'react';

import type { IndexBucketDto } from '@/lib/types/register';

/**
 * Date-aware scroll-track for the register (ADR-0024 follow-up).
 *
 * The windowed register loads a sliding ~1000-row window out of a
 * 40K+ entry timeline. The native browser scrollbar's thumb only
 * reflects the loaded window — it lies about the user's actual
 * position in history. This component replaces the native scrollbar
 * with a custom track that shows every month-with-activity as a
 * bucket, so the user can scan the full timeline visually and jump
 * to any month with one click. Google Photos / Apple Photos pattern.
 *
 * The parent (BankRegisterPage / InvestmentRegisterPage) is
 * responsible for:
 *  - hiding the native scrollbar on the scroll container
 *    (`scrollbar-width: none` + `::-webkit-scrollbar { display: none }`)
 *  - mounting this component as a positioned overlay on the scroll
 *    container's right edge
 *  - passing the currently-topmost entry's `yearMonth` (so this
 *    component can highlight the active bucket) and an `onSeek`
 *    callback that ultimately calls `register.refresh(headerId)`.
 *
 * Self-hides when fewer than 2 buckets exist — a single-month
 * register has no inter-month nav value.
 */
export interface RegisterScrollTrackProps {
    /**
     * Months-with-activity for the current account's register,
     * most-recent first. Comes from
     * `GET /api/ledgers/{id}/transactions/index-buckets`.
     */
    buckets: IndexBucketDto[];
    /**
     * `yearMonth` (yyyy-MM) of the topmost currently-loaded entry.
     * Drives the active-bucket highlight + the floating "current
     * month" pill. Null on first render / empty register.
     */
    currentYearMonth: string | null;
    /**
     * Called when the user clicks/drag-releases on a bucket. Argument
     * is that bucket's `sampleHeaderId` — the parent's job is to wire
     * this to `register.refresh(sampleHeaderId)` so the windowed
     * register re-seeds anchored on that entry.
     */
    onSeek: (sampleHeaderId: string) => void;
}

export function RegisterScrollTrack({
    buckets,
    currentYearMonth,
    onSeek,
}: RegisterScrollTrackProps) {
    const trackRef = useRef<HTMLDivElement | null>(null);
    const [hoverIndex, setHoverIndex] = useState<number | null>(null);
    const [isDragging, setIsDragging] = useState(false);
    const [trackHeight, setTrackHeight] = useState(0);

    // Measure the track container height so we can compute pointer-Y
    // → bucket-index during drag without reading layout in the hot
    // pointermove path. ResizeObserver covers window resizes; the
    // initial measurement comes from the ref callback below.
    useEffect(() => {
        const el = trackRef.current;
        if (!el) return;
        setTrackHeight(el.clientHeight);
        const ro = new ResizeObserver((entries) => {
            for (const entry of entries) {
                setTrackHeight(entry.contentRect.height);
            }
        });
        ro.observe(el);
        return () => ro.disconnect();
    }, [buckets.length]);

    // Lookup: yearMonth → bucket index in `buckets`. Used by the
    // active-bucket highlight + the pill label.
    const indexByYearMonth = useMemo(() => {
        const m = new Map<string, number>();
        buckets.forEach((b, i) => m.set(b.yearMonth, i));
        return m;
    }, [buckets]);

    const activeIndex = currentYearMonth != null
        ? indexByYearMonth.get(currentYearMonth) ?? null
        : null;

    // The floating pill is transient — visible only while the user
    // is actively interacting (hoverIndex is set during hover AND
    // during drag; handlePointerLeave keeps it set while dragging
    // so the pill follows even if the pointer briefly exits the
    // track bounds). When idle, the active-bucket tick alone marks
    // the current position; the pill would otherwise overlap the
    // rightmost data column. Google Photos pattern: pill on
    // scroll/drag, subtle marker at rest.
    const pillIndex = hoverIndex;

    // Year-label positions: each year's label sits at the OLDEST
    // month of that year (bottom of the year's cluster in our
    // DESC-ordered timeline). Clicking the label area lands the user
    // at the *start* of the calendar year — matches the mental model
    // "I clicked 2022, take me to early 2022" rather than the
    // counterintuitive "land at December 2022" that a top-of-cluster
    // label position produced. Computed BEFORE the early-return
    // guard so React's rules-of-hooks invariant holds.
    const yearLabels = useMemo(() => {
        // Anchor each year's label at that year's OLDEST month (the START of the
        // calendar year), regardless of whether `buckets` runs newest-first
        // (date desc) or oldest-first (date asc). Track the min-yearMonth index
        // per year rather than relying on iteration order, so the label sits
        // correctly for both sort directions.
        const byYear = new Map<string, number>();
        for (let i = 0; i < buckets.length; i++) {
            const year = buckets[i].yearMonth.slice(0, 4);
            const cur = byYear.get(year);
            if (cur === undefined || buckets[i].yearMonth < buckets[cur].yearMonth) {
                byYear.set(year, i);
            }
        }
        return Array.from(byYear, ([year, bucketIndex]) => ({ year, bucketIndex }));
    }, [buckets]);

    // Self-hide for trivially-small histories: a single-month register
    // has no inter-month nav value.
    if (buckets.length < 2) return null;

    const bucketPixelHeight = trackHeight > 0
        ? trackHeight / buckets.length
        : 0;

    function pointerYToBucketIndex(clientY: number): number {
        const el = trackRef.current;
        if (!el || bucketPixelHeight === 0) return 0;
        const rect = el.getBoundingClientRect();
        const y = clientY - rect.top;
        const idx = Math.floor(y / bucketPixelHeight);
        return Math.max(0, Math.min(buckets.length - 1, idx));
    }

    function handlePointerDown(e: React.PointerEvent<HTMLDivElement>) {
        // Only respond to primary button + keep keyboard a11y alive
        // (Tab + arrow keys handled separately below).
        if (e.button !== 0) return;
        e.currentTarget.setPointerCapture(e.pointerId);
        setIsDragging(true);
        setHoverIndex(pointerYToBucketIndex(e.clientY));
    }

    function handlePointerMove(e: React.PointerEvent<HTMLDivElement>) {
        if (!isDragging && e.buttons === 0) {
            // Hover (no button held) — update the pill so the user
            // can preview the date before clicking.
            setHoverIndex(pointerYToBucketIndex(e.clientY));
            return;
        }
        if (isDragging) {
            setHoverIndex(pointerYToBucketIndex(e.clientY));
        }
    }

    function handlePointerUp(e: React.PointerEvent<HTMLDivElement>) {
        if (!isDragging) return;
        e.currentTarget.releasePointerCapture(e.pointerId);
        setIsDragging(false);
        const idx = pointerYToBucketIndex(e.clientY);
        onSeek(buckets[idx].sampleHeaderId);
        // Keep the pill visible at the seek target until the next
        // pointermove — instant visual confirmation of the jump.
        setHoverIndex(idx);
    }

    function handlePointerLeave() {
        if (!isDragging) setHoverIndex(null);
    }

    // Keyboard navigation on the track is intentionally NOT wired:
    // the host page has a document-level keydown listener for
    // ArrowUp/Down that drives row-by-row focus movement, and
    // racing the two handlers produced wrestle-jank (both
    // seek-by-bucket and move-row-focus firing on the same press,
    // since React's stopPropagation doesn't suppress
    // document-level listeners). Keyboard-driven date navigation
    // lives in the Cmd/Ctrl+J popover (RegisterDateJumpPopover);
    // the track is click + drag only.

    // The year that contains the active bucket — used to make that
    // year's label visually prominent (bold + accent color) so it
    // reads as the "you are here, this is the year" anchor.
    const activeYear = activeIndex != null
        ? buckets[activeIndex].yearMonth.slice(0, 4)
        : null;

    return (
        <div
            ref={trackRef}
            role="scrollbar"
            aria-orientation="vertical"
            aria-controls="register-scroll-region"
            aria-valuemin={0}
            aria-valuemax={buckets.length - 1}
            aria-valuenow={activeIndex ?? 0}
            aria-valuetext={
                activeIndex != null
                    ? formatYearMonthLong(buckets[activeIndex].yearMonth)
                    : undefined
            }
            onPointerDown={handlePointerDown}
            onPointerMove={handlePointerMove}
            onPointerUp={handlePointerUp}
            onPointerLeave={handlePointerLeave}
            className="
                absolute top-0 right-0 bottom-0
                w-12 select-none
                cursor-pointer
            "
            style={{ touchAction: 'none' }}
        >
            {/* Vertical spine. 2px wide; positioned at right-7 so
                the year labels live in the gutter to its RIGHT
                (close to the screen edge), matching the Google
                Photos pattern where year markers sit at the
                far-right against the page edge. */}
            <div
                className="absolute top-1 bottom-1 right-7 w-0.5 bg-text-muted/70"
                aria-hidden
            />

            {/* Year labels — short `'25` form. Positioned to the
                RIGHT of the spine, flush near the screen edge, so
                they read as right-rail annotations rather than
                competing with the body content. Anchored at each
                year's OLDEST month (bottom of the cluster when
                newest-first, top when oldest-first) so clicking near a
                year lands the user at the START of that calendar year.
                The label for the CURRENT year renders bold in the
                accent colour. Clamped from edges so first/last
                don't clip. */}
            {yearLabels.map((label) => {
                const centre = bucketPixelHeight * (label.bucketIndex + 0.5);
                const clamped = Math.max(
                    8,
                    Math.min(trackHeight - 8, centre),
                );
                const isActive = label.year === activeYear;
                return (
                    <span
                        key={label.year}
                        aria-hidden
                        className={
                            'absolute right-1 -translate-y-1/2 text-xs leading-none tabular-nums ' +
                            (isActive
                                ? 'font-semibold text-accent'
                                : 'text-text-muted')
                        }
                        style={{ top: clamped }}
                    >
                        {formatYearShort(label.year)}
                    </span>
                );
            })}

            {/* Month tick markers on the spine. Tiny by default; the
                active bucket gets a much wider + brighter horizontal
                bar so the user can spot the "you are here" position
                without reading text. Positioned relative to the new
                spine at right-7. */}
            {buckets.map((bucket, i) => {
                const isActive = i === activeIndex;
                return (
                    <span
                        key={bucket.yearMonth}
                        aria-hidden
                        className={
                            'absolute -translate-y-1/2 ' +
                            (isActive
                                ? 'right-6 h-0.5 w-3 bg-accent rounded-full'
                                : 'right-[26px] h-px w-1.5 bg-text-muted/40')
                        }
                        style={{ top: bucketPixelHeight * (i + 0.5) }}
                    />
                );
            })}

            {/* Transient position pill. Shown only while the user is
                hovering or dragging the track — the active-bucket
                accent bar above marks the current position when idle.
                Pill floats to the LEFT of the track so the user can
                preview a date before clicking. Opaque background
                since it can overlap the rightmost data column. */}
            {pillIndex != null && bucketPixelHeight > 0 ? (
                <div
                    aria-hidden
                    className="
                        absolute right-9 -translate-y-1/2
                        whitespace-nowrap rounded
                        bg-surface border border-border
                        px-1.5 py-0.5
                        text-[0.6875rem] font-medium tabular-nums text-text
                        shadow-md pointer-events-none
                    "
                    style={{ top: bucketPixelHeight * (pillIndex + 0.5) }}
                >
                    {formatYearMonthLong(buckets[pillIndex].yearMonth)}
                </div>
            ) : null}
        </div>
    );
}

/**
 * `2024` → `'24`. The two-digit-with-apostrophe form fits in the
 * narrow right-rail gutter without crowding. Reading-direction
 * implication: `'24` is unambiguously a year ('twenty-four), not
 * confusable with a month number.
 */
function formatYearShort(year: string): string {
    return "’" + year.slice(-2);
}

/**
 * `2024-03` → `Mar 2024`. Renders in the SPA-default locale so it
 * follows the OS / browser preference (e.g. `mars 2024` on a
 * French system). Used for the pill label + the scrollbar
 * `aria-valuetext`.
 */
function formatYearMonthLong(yearMonth: string): string {
    const [yearStr, monthStr] = yearMonth.split('-');
    const year = Number.parseInt(yearStr, 10);
    const month = Number.parseInt(monthStr, 10);
    if (!Number.isFinite(year) || !Number.isFinite(month)) return yearMonth;
    // Day=1 is a placeholder — we render { year, month } only.
    const d = new Date(year, month - 1, 1);
    return d.toLocaleString(undefined, { month: 'short', year: 'numeric' });
}
