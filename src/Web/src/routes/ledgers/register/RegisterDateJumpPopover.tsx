import { useEffect, useId, useLayoutEffect, useRef, useState } from 'react';

import {
    dismissOnEscape,
    happenedInside,
    useDismissOnPageEscape,
    useReturnFocusOnClose,
} from '@/components/ui/popoverDismissal';
import type { IndexBucketDto } from '@/lib/types/register';

import { registerFocusHome } from './shell/useRegisterFocusHome';
import { POPUP } from './shell/useRegisterKeyboardNav';

/**
 * Cmd/Ctrl+J date-jump popover (ADR-0024 follow-up companion to
 * RegisterScrollTrack). Small floating popover that accepts a date,
 * resolves it to the nearest month-with-activity bucket, and seeks
 * the windowed register there.
 *
 * A popover like the register header's Filter, Show and Sort (ADR-0023 §B.4,
 * §L), not a modal: nothing behind it is blocked, Tab may leave it and it
 * stays open, and a press outside closes it. It is a non-modal dialog, named
 * "Jump to date", so a key pressed in it stays its own: the register's
 * shortcuts leave a key from a dialog to the dialog (§B.2). Its own shortcut
 * does the same: Ctrl/⌘+J pressed in a dialog, a menu or a list does not open
 * it. It sits on the header popovers' tier (z-30), so a dialog opened while it
 * is open covers it as it covers the rest of the page.
 *
 * It sits in the Tab order right after what it was opened from, as each header
 * popover sits right after the button that opens it (ADR-0023 §L): the control
 * that had focus when the shortcut was pressed. Tab from that control goes into
 * it, Tab from its last control (Jump) goes on to the control that follows that
 * one, Shift+Tab from its date field goes back to it, and Shift+Tab from the
 * control that follows it comes back to Jump. Opened from the register's row
 * list, which is no Tab stop of its own, Shift+Tab goes back to the list itself,
 * not to a row's control inside it, and Tab from the list goes into the rows as
 * it does with the popover closed; from the last row it goes on into the popover.
 * Opened from the page itself, with nothing focused, or once what it was opened
 * from has left the page, it sits at the start of the page: Tab from a fresh
 * start, from the browser's own controls, or past the page's last control, goes
 * into its date field, Shift+Tab from the page's first control comes back to
 * Jump, Tab from Jump goes on to the page's first control, and Shift+Tab from
 * the date field to its last. A guard hands focus on in the direction of the Tab
 * the page heard moving it; a Tab pressed in the browser's own controls is not
 * heard, and enters the page at its first Tab stop going forward and at its last
 * going back, so a guard reached that way goes back when nothing follows it and
 * forward otherwise. The registers draw it after every control, so Shift+Tab
 * from there reaches its end guard; when no Tab stop follows its opener (the
 * page's last control, or the row list with nothing after it), it is the page's
 * last place in the Tab order, and focus goes to Jump.
 *
 * Its own markup stays where the page renders it, outside the register's list,
 * which unmounts rows as they scroll away and is swapped for a placeholder while
 * loading or empty: drawn inside either, it would be taken off the page with
 * them, and with it what was typed and the focus. What places it in the Tab
 * order is a focus guard of its own beside the opener, an empty element no wider
 * than a pixel that hands focus on as it arrives (into the popover, from either
 * side), and one at each end of the popover that hands focus on as it leaves.
 * Should the opener or its guard leave the page, the guard left behind goes too,
 * the popover stays open, and a guard at the start of the page leads into it
 * instead, as does one placed there at the open when there is no opener on the
 * page. The opener is never a field (the shortcut does nothing there, so never
 * the search box) nor a control in a dialog, a menu or a list. It leaves the page
 * when the register swaps the list area for a placeholder (on a filter, a sort or
 * a refresh, and while loading or empty), taking the row list, its rows'
 * controls, an inline edit's buttons and the bulk bar's buttons with it; when the
 * list unmounts a row's control as it scrolls away; and when the opener takes
 * itself away (a filter chip removed, the bulk bar's buttons when the selection
 * clears, an editor's buttons when the edit closes), or is removed from a parent
 * that stays. The toolbar's and the header's controls stay. Reaching the popover
 * where the page renders it, from the control before or after that place, passes
 * over it, so it is in the Tab order once: right after its opener, or at the
 * start of the page.
 *
 * Closed by Esc, Cancel, Jump or the shortcut, it gives focus back to where it
 * was opened from, unless focus has moved on to another control since, which
 * keeps it. With nothing to give it back to (it was opened with nothing focused,
 * or what it was opened from has left the page since), focus goes to the
 * register's focus home instead (the row list, else the add control, else the
 * search box), never to the page. The add control or the search box then stands
 * in for the list, which takes focus back when it comes onto the page if the
 * reader has pressed nothing since (useRegisterFocusHome). A press outside
 * closes it and leaves focus where the press puts it: on what it landed on, or
 * on the page when that is no control. It gives none to the register's focus
 * home, with or without an opener, so it records no stand-in.
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
    const jumpRef = useRef<HTMLButtonElement | null>(null);
    const rootRef = useRef<HTMLDivElement | null>(null);
    // Where focus was when the popover opened: where it sits in the Tab order,
    // and where focus goes back to on close.
    const openerRef = useRef<HTMLElement | null>(null);
    // The focus guard beside the opener, while the popover is open.
    const openerGuardRef = useRef<HTMLElement | null>(null);
    // Whether a press outside closed it: that press puts focus where it lands,
    // so the close gives none to the register's focus home. Cleared on open.
    const pressedRef = useRef(false);
    // Which way the Tab moving focus now went (true: Shift+Tab), or null when no
    // Tab pressed in the page is moving it: a guard hands focus on in that
    // direction (cameBackward).
    const tabRef = useRef<boolean | null>(null);
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
            // A key pressed in a dialog, a menu or a list is that popup's, as
            // the register's other shortcuts leave it (ADR-0023 §B.2). Closed,
            // the popover is not on the page, so its own Ctrl/⌘+J still
            // closes it.
            if (!isOpen && happenedInside(e, POPUP)) return;

            e.preventDefault();
            if (!isOpen) {
                const active = document.activeElement;
                openerRef.current = active instanceof HTMLElement && active !== document.body ? active : null;
                pressedRef.current = false;
            }
            setIsOpen((prior) => !prior);
        }

        window.addEventListener('keydown', onKey);
        return () => window.removeEventListener('keydown', onKey);
    }, [buckets.length, isOpen]);

    // Open, a focus guard goes into the page right after the opener, so Tab runs
    // through the popover from there as through a header popover from its button:
    // focus arriving on the guard goes on into the popover, at its first control
    // from before the guard and at its last from after it. Placed before anything
    // focuses the popover, and taken away when it closes.
    //
    // With no opener guard, the popover sits at the start of the page, and a guard
    // at the start of the body leads into it the same way. That is so from the open
    // when nothing on the page had focus, or what had it has already gone, and from
    // the moment the opener guard goes: the page is watched while the popover is
    // open, and once the opener or its guard has left it (a row unmounted, the
    // list's wrapper replaced, the opener removed from a parent that stays), the
    // guard left behind is taken away too and the one at the start goes in. An
    // opener moved within the page takes its guard along.
    useLayoutEffect(() => {
        if (!isOpen) return;
        // A Tab moves focus in the task its keydown is dispatched in, so its
        // direction is kept until that task is done, and no longer: a Tab pressed
        // in the browser's own controls later reaches the page with no keydown in
        // it, and must not be read as the last one the page heard.
        let expire: number | undefined;
        const onTab = (e: KeyboardEvent) => {
            if (e.key !== 'Tab') return;
            tabRef.current = e.shiftKey;
            window.clearTimeout(expire);
            expire = window.setTimeout(() => { tabRef.current = null; }, 0);
        };
        document.addEventListener('keydown', onTab, true);
        const onGuard = (guard: HTMLElement) =>
            (cameBackward(tabRef.current, guard) ? jumpRef.current : inputRef.current)?.focus();
        const startOfPage = focusGuard(onGuard);
        const opener = openerRef.current;
        let beside: HTMLElement | null = null;
        let watch: MutationObserver | null = null;
        const toStartOfPage = () => {
            watch?.disconnect();
            beside?.remove();
            openerGuardRef.current = null;
            document.body.prepend(startOfPage);
        };
        if (opener !== null && opener.isConnected) {
            const guard = focusGuard(onGuard);
            opener.after(guard);
            beside = guard;
            openerGuardRef.current = guard;
            watch = new MutationObserver(() => {
                if (!opener.isConnected || !guard.isConnected) toStartOfPage();
                else if (opener.nextSibling !== guard) opener.after(guard);
            });
            watch.observe(document.body, { childList: true, subtree: true });
        } else {
            toStartOfPage();
        }
        return () => {
            document.removeEventListener('keydown', onTab, true);
            window.clearTimeout(expire);
            tabRef.current = null;
            watch?.disconnect();
            beside?.remove();
            startOfPage.remove();
            openerGuardRef.current = null;
        };
    }, [isOpen]);

    /**
     * The guard beside the opener while it and the opener are both on the page,
     * else null: the popover then sits at the start of the page. Read from the page
     * itself as well as from the watch, which hears a removal only once the change
     * that made it is done.
     */
    function openerGuard(): HTMLElement | null {
        const guard = openerGuardRef.current;
        const opener = openerRef.current;
        return guard !== null && guard.isConnected && opener !== null && opener.isConnected ? guard : null;
    }

    // Focus arriving on the guard at the popover's start. Shift+Tab from its date
    // field leaves it for the opener, the list included, which is no Tab stop of
    // its own: so the guard focuses the opener itself rather than what the browser
    // would reach, a row's control inside it; with no opener guard it goes to the
    // page's last control. Tab from the control before the place the page renders
    // the popover passes over it, to the control after that place. When nothing
    // follows that place, the Tab order starts again at the start of the page: the
    // page's first control, or, with no opener guard, the popover itself, which
    // sits there.
    function onStartGuard(e: React.FocusEvent<HTMLElement>) {
        const popover = rootRef.current;
        if (popover === null) return;
        const guard = openerGuard();
        if (!cameBackward(tabRef.current, e.currentTarget)) {
            const after = stopAfter(popover, popover.lastElementChild);
            focusOr(after ?? (guard !== null ? firstStop(popover) : null), inputRef.current);
            return;
        }
        if (guard !== null) openerRef.current?.focus();
        else focusOr(lastStop(popover), inputRef.current);
    }

    // Focus arriving on the guard at the popover's end. Tab from Jump leaves it for
    // the control after the opener's guard, or, with no opener guard, for the
    // page's first control. Shift+Tab from the control after the place the page
    // renders the popover passes over it, to the control before that place. When
    // nothing comes before that place, the control Shift+Tab left is the page's
    // first: Shift+Tab goes on to the page's last control, or, with no opener
    // guard, into the popover, which sits before the page's first control.
    //
    // Shift+Tab from the browser's own controls (no Tab heard) enters the page at
    // its last Tab stop. The registers draw the popover after every control, so
    // that is this guard. When no Tab stop outside the popover follows the
    // opener's guard (the opener is the page's last control, or the row list with
    // nothing after it), the popover is the page's last place in the Tab order,
    // and focus goes to Jump rather than passing over it to the opener.
    function onEndGuard(e: React.FocusEvent<HTMLElement>) {
        const popover = rootRef.current;
        if (popover === null) return;
        const guard = openerGuard();
        if (cameBackward(tabRef.current, e.currentTarget)) {
            if (tabRef.current === null && guard !== null && stopAfter(popover, guard) === null) {
                jumpRef.current?.focus();
                return;
            }
            const before = stopBefore(popover, popover.firstElementChild);
            focusOr(before ?? (guard !== null ? lastStop(popover) : null), jumpRef.current);
            return;
        }
        focusOr((guard !== null ? stopAfter(popover, guard) : null) ?? firstStop(popover), inputRef.current);
    }

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

    // Closed by Esc, Cancel, Jump or the shortcut again, focus goes back to
    // where it was when the popover opened, or, when nothing had focus then or
    // that control has left the page by now, to the register's focus home: the
    // row list, else the add control, else the search box, as the list's
    // stand-in.
    //
    // A press outside puts focus where it lands, so its close gives none to the
    // focus home and records no stand-in. React renders that close as soon as
    // the press's mousedown listener returns, before the browser moves focus to
    // what the press landed on: focus is on the page for that moment, and a
    // stand-in recorded then (the press's pointerdown, which clears one, has
    // already gone by) would let the list take back the focus the press gave.
    useReturnFocusOnClose(isOpen, openerRef, () => (pressedRef.current ? null : registerFocusHome()));

    // A press outside closes it, as it does the header's popovers (ADR-0023
    // §L). The press itself goes on to whatever it landed on: nothing covers
    // the page. Inside is read from the press's path.
    useEffect(() => {
        if (!isOpen) return;
        const onDoc = (e: MouseEvent) => {
            if (rootRef.current && !happenedInside(e, rootRef.current)) {
                pressedRef.current = true;
                setIsOpen(false);
            }
        };
        document.addEventListener('mousedown', onDoc);
        return () => document.removeEventListener('mousedown', onDoc);
    }, [isOpen]);

    // Escape closes it, as it does a list (ADR-0023 §F, §L). An Escape pressed
    // inside is handled on the popover itself; one pressed with focus
    // elsewhere on the page (Tab leaves the popover) on the body.
    useDismissOnPageEscape(isOpen, () => setIsOpen(false));

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
        <div
            ref={rootRef}
            role="dialog"
            aria-labelledby={titleId}
            className="
                fixed top-16 right-6 z-30
                rounded-md border border-border bg-surface
                shadow-md p-3
                flex flex-col gap-2
                min-w-[14rem]
            "
            onKeyDown={(e) => dismissOnEscape(e, () => setIsOpen(false))}
        >
            <span tabIndex={0} aria-hidden="true" data-focus-guard="" className="sr-only" onFocus={onStartGuard} />
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
                        ref={jumpRef}
                        type="submit"
                        className="rounded bg-accent px-2 py-0.5 text-text-inverse hover:opacity-90"
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
            <span tabIndex={0} aria-hidden="true" data-focus-guard="" className="sr-only" onFocus={onEndGuard} />
        </div>
    );
}

/** What can hold focus, before the checks a selector cannot make (`isTabbable`). */
const FOCUSABLE = 'a[href], button, input, select, textarea, summary, iframe, [tabindex], [contenteditable]';

/**
 * A focus guard that hands focus on with `onFocus`, given the guard, as it
 * arrives: no wider than a pixel, out of the layout and of what is read aloud.
 */
function focusGuard(onFocus: (guard: HTMLElement) => void): HTMLElement {
    const guard = document.createElement('span');
    guard.tabIndex = 0;
    guard.className = 'sr-only';
    guard.setAttribute('aria-hidden', 'true');
    guard.setAttribute('data-focus-guard', '');
    guard.addEventListener('focus', () => onFocus(guard));
    return guard;
}

/** Whether Tab stops on `el`: in the sequential order, enabled, drawn, and no focus guard. */
function isTabbable(el: HTMLElement): boolean {
    if (el.tabIndex < 0 || el.hasAttribute('data-focus-guard')) return false;
    if ((el as HTMLButtonElement).disabled === true) return false;
    if (el instanceof HTMLInputElement && el.type === 'hidden') return false;
    // Not drawn (display: none, a closed details' contents); a DOM without layout
    // cannot tell, and takes it as drawn.
    const check = (el as { checkVisibility?: () => boolean }).checkVisibility;
    return check === undefined || check.call(el);
}

/**
 * The page's Tab stops outside `popover`, in document order, and where `anchor`
 * falls among them: the number of stops before it. Read from one list of what
 * can hold focus, the popover's own and the guards included, so a place in the
 * page is an index rather than a relation between nodes.
 */
function stopsAround(popover: HTMLElement, anchor: Element | null): { stops: HTMLElement[]; at: number } {
    const own = new Set(popover.querySelectorAll(FOCUSABLE));
    const stops: HTMLElement[] = [];
    let at = 0;
    for (const el of document.querySelectorAll<HTMLElement>(FOCUSABLE)) {
        if (el === anchor) at = stops.length;
        if (!own.has(el) && isTabbable(el)) stops.push(el);
    }
    return { stops, at };
}

/**
 * Whether the Tab that brought focus to `guard` went backward: `tab`, the Tab the
 * page heard, while it is moving focus. A Tab pressed in the browser's own
 * controls (the address bar, after F6 or a click there) reaches the page with no
 * keydown in it, and enters at the page's first Tab stop going forward and at its
 * last going back: so with no Tab heard (null), focus came backward when no Tab
 * stop follows the guard, and forward otherwise.
 */
function cameBackward(tab: boolean | null, guard: Element): boolean {
    return tab ?? !anyStopAfter(guard);
}

/** Whether any Tab stop, the popover's own included, follows `el` in the page. */
function anyStopAfter(el: Element): boolean {
    let after = false;
    for (const other of document.querySelectorAll<HTMLElement>(FOCUSABLE)) {
        if (other === el) after = true;
        else if (after && isTabbable(other)) return true;
    }
    return false;
}

/** The first Tab stop outside `popover` after `anchor`; none when nothing follows it. */
function stopAfter(popover: HTMLElement, anchor: Element | null): HTMLElement | null {
    const { stops, at } = stopsAround(popover, anchor);
    return anchor !== null ? stops[at] ?? null : null;
}

/** The last Tab stop outside `popover` before `anchor`; none when nothing comes before it. */
function stopBefore(popover: HTMLElement, anchor: Element | null): HTMLElement | null {
    const { stops, at } = stopsAround(popover, anchor);
    return anchor !== null ? stops[at - 1] ?? null : null;
}

/** The page's first Tab stop outside `popover`. */
function firstStop(popover: HTMLElement): HTMLElement | null {
    return stopsAround(popover, null).stops[0] ?? null;
}

/** The page's last Tab stop outside `popover`. */
function lastStop(popover: HTMLElement): HTMLElement | null {
    return stopsAround(popover, null).stops.at(-1) ?? null;
}

/** Focus `target`, or `fallback` when there is no target. */
function focusOr(target: HTMLElement | null, fallback: HTMLElement | null): void {
    (target ?? fallback)?.focus();
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
