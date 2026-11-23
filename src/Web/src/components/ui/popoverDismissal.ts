import { useEffect, useLayoutEffect, useRef, type RefObject } from 'react';

/**
 * How a popover closes (ADR-0023 §F, §L): Esc dismisses it, focus goes back
 * where it came from, and a press closes it only when it lands outside.
 *
 * A popover that closes on an Escape marks it handled (preventDefault) and
 * stops it (stopPropagation) in the handler that closes it, so nothing further
 * out acts on it too: an editor's cancel, a dialog's close, the register
 * selection's clear. An Escape something nearer already marked (a list inside
 * the popover closing its suggestions) is not the popover's.
 */

/**
 * Whether `event` happened inside `element`: on it, or on anything in it.
 *
 * Read from the event's path, which is fixed when dispatch starts, and never
 * from where its target is now. A control can remove itself as the press goes
 * down: a pick in the category picker commits on mousedown, and React takes the
 * list away between its own listener and the document's. The pressed option is
 * then out of the page, so `element.contains(event.target)` would read that
 * press as outside and close the popover around the picker on its own pick. The
 * path still holds the ancestors the option had.
 *
 * Every check of this kind asks here: the popovers, lists, menus, the editor
 * and the dialog, and the register's shortcuts, which ask with a selector
 * whether a key came from any popup at all. Ask while the event is being
 * dispatched, in its listener: once dispatch ends the path is empty. An element
 * that is not there (null) holds no event.
 */
export function happenedInside(event: Event, element: Node | null): boolean;
/** Whether `event` happened inside any element that matches `selector`, read the same way. */
export function happenedInside(event: Event, selector: string): boolean;
export function happenedInside(event: Event, where: Node | string | null): boolean {
    const path = event.composedPath();
    if (typeof where === 'string') return path.some((node) => node instanceof Element && node.matches(where));
    return where !== null && path.includes(where);
}

type EscapeEvent = Pick<KeyboardEvent, 'key' | 'defaultPrevented' | 'preventDefault' | 'stopPropagation'>;

/** Close on an Escape nothing nearer used, marked handled and stopped. */
export function dismissOnEscape(e: EscapeEvent, close: () => void): void {
    if (e.key !== 'Escape' || e.defaultPrevented) return;
    e.preventDefault();
    e.stopPropagation();
    close();
}

/**
 * While `open`, close on an Escape pressed with focus outside the popover too:
 * focus on the page after a click that does not focus a button, or on a
 * control Tab moved to. Heard on the body, which is after every React handler,
 * since the app's root is inside it (so a list that closed on the Escape has
 * already stopped it), and before the document, where the register's
 * selection listens (so the popover's own never also clears the selection).
 */
export function useDismissOnPageEscape(open: boolean, close: () => void): void {
    const closeRef = useRef(close);
    useLayoutEffect(() => {
        closeRef.current = close;
    });
    useEffect(() => {
        if (!open) return;
        const onKey = (e: KeyboardEvent) => dismissOnEscape(e, () => closeRef.current());
        document.body.addEventListener('keydown', onKey);
        return () => document.body.removeEventListener('keydown', onKey);
    }, [open]);
}

/**
 * However the popover closes, give focus back to `opener`, as it is when the
 * popover opens (ADR-0023 §L).
 * Closing removes the control that held focus, and focus left on the page is
 * out of reach of everything around the opener: from an open inline editor,
 * the next Escape would clear the register's selection instead of cancelling
 * the edit. Only when nothing has claimed focus since, so a control the user
 * moved to keeps it without so much as a blur (a list closes when its control
 * loses focus).
 *
 * A popover opened with nothing focused has no opener, and one that has left
 * the page by the time the popover closes cannot take focus: focusing it does
 * nothing. Either way focus would stay on the page, so it goes to `fallback`,
 * read as the popover closes: the place the host names for focus that has
 * nowhere to go back to (the register's focus home, for the date jump). With no
 * fallback, or none on the page, or a fallback that returns null for this close,
 * nothing moves.
 */
export function useReturnFocusOnClose(
    open: boolean,
    opener: RefObject<HTMLElement | null>,
    fallback?: () => HTMLElement | null,
): void {
    // The latest fallback, so a host that passes a new function on each render
    // neither re-runs the effect below nor has a stale one read at close.
    const fallbackRef = useRef(fallback);
    useLayoutEffect(() => {
        fallbackRef.current = fallback;
    });
    useLayoutEffect(() => {
        if (!open) return;
        const target = opener.current;
        return () => {
            const holder = document.activeElement;
            if (holder !== null && holder !== document.body) return;
            const to = target !== null && target.isConnected ? target : fallbackRef.current?.() ?? null;
            to?.focus({ preventScroll: true });
        };
    }, [open, opener]);
}
