import { useCallback, useLayoutEffect, useRef, type RefObject } from 'react';

/**
 * Where focus goes when a list takes away the row whose control holds it
 * (ADR-0023 §L).
 *
 * THE RULE. A row deleted or removed from its own menu or button takes that
 * control with it. Focus goes to the same control on the next row, else on
 * the previous row, else to the list's add control (New tag, Add passkey,
 * + Create backup), never to the page. A list with no add control gives it to
 * its own region instead: the element that holds the rows and stays on the
 * page when none is left, focusable but no Tab stop (tabIndex -1), named, and
 * ringed for keyboard focus only. It follows the filter chips, where removing
 * a chip moves focus to the next chip, else to Clear all.
 *
 * HOW. A list calls useRowRemovalFocus with its rows' keys in the order they
 * are drawn and its add control, and gives each row's control the ref the
 * hook makes for its key. When a row goes, its control goes with it:
 *  - if the control still held focus (a confirm gave it back before the list
 *    was refetched, or a row button whose request just landed), focus moves on
 *    in the same render, once the page holds no other focus;
 *  - if a dialog it opened is still open (its confirm closes in the render
 *    that removes the row), the dialog gives focus back to the control's
 *    successor as it closes (Modal, through successorOf), and a menu does the
 *    same (ContextMenu).
 * The next and previous rows are read from the order the list was drawn in
 * when the row went, and a row's control counts only while it is on the page
 * and not disabled. Focus the user has put somewhere else stays where it is.
 */

/**
 * Where focus goes when a control that holds it replaces or removes itself
 * (ADR-0023 §L): to the control that takes its place, never to the page.
 *
 * Give the ref this returns to the control that goes (Map, which a landed
 * mapping replaces with Sync from…; Remove N unused, which goes once nothing
 * is unused). `next` names, once it has gone, the control that takes its
 * focus. When the control leaves the page while it holds focus, focus moves to
 * `next()` in the same render, once the page holds no other focus. Call the
 * hook in the component whose render takes the control away, so that its
 * layout effect runs in that render; a row the list takes away whole is
 * useRowRemovalFocus's.
 */
export function useFocusHandOff(next: () => HTMLElement | null): (el: HTMLElement | null) => void {
    return useHandOff(next, false);
}

/**
 * Where focus goes when a part of the page that holds controls of its own goes
 * while one of them holds focus (ADR-0023 §L): from whichever of its controls
 * held it to the control that takes its place, never to the page.
 *
 * Give the ref this returns to the element that goes with its controls (the
 * consistency report, whose Repair buttons and links go with it). When it
 * leaves the page while focus is on it or anywhere inside it, focus moves to
 * `next()` in the same render, once the page holds no other focus. Call the
 * hook in the component whose render takes the element away, as for
 * useFocusHandOff.
 */
export function useFocusWithinHandOff(next: () => HTMLElement | null): (el: HTMLElement | null) => void {
    return useHandOff(next, true);
}

/**
 * Whether `el` holds focus when `active` has it: `el` is `active`, or with
 * `within` contains it.
 */
function holdsFocus(el: HTMLElement, active: Element | null, within: boolean): boolean {
    return active !== null && (within ? el.contains(active) : el === active);
}

/** useFocusHandOff, and with `within` useFocusWithinHandOff. */
function useHandOff(next: () => HTMLElement | null, within: boolean): (el: HTMLElement | null) => void {
    const element = useRef<HTMLElement | null>(null);
    const owed = useRef<HTMLElement | null>(null);
    const nextRef = useRef(next);

    useLayoutEffect(() => {
        nextRef.current = next;
        const gone = owed.current;
        owed.current = null;
        if (gone === null || gone.isConnected) return;
        const active = document.activeElement;
        if (active === null || active === document.body || holdsFocus(gone, active, within)) {
            nextRef.current()?.focus();
        }
    });

    return useCallback((el: HTMLElement | null) => {
        if (el !== null) {
            element.current = el;
            return;
        }
        const was = element.current;
        element.current = null;
        // React detaches a ref before it takes the element off the page, and an
        // element's before those of what it holds, so whether it still holds
        // focus is read here.
        if (was !== null && holdsFocus(was, document.activeElement, within)) owed.current = was;
    }, [within]);
}

/** For a control a list took away, the control that takes its focus. */
const successors = new WeakMap<HTMLElement, () => HTMLElement | null>();

/**
 * The control that takes the focus of `removed`, a row's control a list has
 * taken away, or null when no list took it away.
 */
export function successorOf(removed: HTMLElement): HTMLElement | null {
    return successors.get(removed)?.() ?? null;
}

/** A control that can take focus: on the page and not disabled. */
function usable(el: HTMLElement | undefined): el is HTMLElement {
    return el !== undefined && el.isConnected && !(el as HTMLButtonElement).disabled;
}

/**
 * Which of a row's controls a ref is for. A row has one control the rule moves
 * focus between, its `control`; a row may also carry a `companion` (the
 * sidebar's selected account has a kebab beside its link, which no other row
 * has), and a companion that goes with its row hands focus to the next row's
 * control, as the row's control would. A companion may be an element that
 * holds controls of its own (a consistency report row's list of what
 * disagrees, whose links go with the row's Repair): it hands focus on when it
 * goes with focus on it or anywhere inside it.
 */
export type RowControlRole = 'control' | 'companion';

/**
 * Hands focus on when a row of this list goes. `keys` are the rows' keys in
 * the order they are drawn; `home` is the list's add control, or for a list
 * with none its own region (THE RULE), which takes focus when no row is left.
 * It is required, so no list leaves that focus to the page by leaving it out.
 * Returns, for a row's key, the ref for the row's
 * control (its actions button, or the button that removes it), or with
 * `'companion'` for a second control of the row; the ref is the same function
 * for a key and role across renders.
 */
export function useRowRemovalFocus<K>(
    keys: readonly K[],
    home: RefObject<HTMLElement | null>,
): (key: K, role?: RowControlRole) => (el: HTMLElement | null) => void {
    const controls = useRef(new Map<K, HTMLElement>());
    const companions = useRef(new Map<K, HTMLElement>());
    const refs = useRef(new Map<K, (el: HTMLElement | null) => void>());
    const companionRefs = useRef(new Map<K, (el: HTMLElement | null) => void>());
    // The order the rows were last drawn in, read when one goes.
    const order = useRef<readonly K[]>(keys);
    const homeRef = useRef(home);
    // A removed control that held focus as it went, and whether it held it within (a companion).
    const owed = useRef<{ key: K; control: HTMLElement; within: boolean } | null>(null);

    useLayoutEffect(() => {
        homeRef.current = home;
        // A row that has gone needs its ref no longer; one still drawn keeps the same ref.
        const drawn = new Set<K>(keys);
        for (const key of refs.current.keys()) if (!drawn.has(key)) refs.current.delete(key);
        for (const key of companionRefs.current.keys()) if (!drawn.has(key)) companionRefs.current.delete(key);
        const gone = owed.current;
        owed.current = null;
        if (gone !== null && !keys.includes(gone.key) && !gone.control.isConnected) {
            const active = document.activeElement;
            if (active === null || active === document.body || holdsFocus(gone.control, active, gone.within)) {
                successorOf(gone.control)?.focus();
            }
        }
        order.current = keys;
    });

    return useCallback((key: K, role: RowControlRole = 'control') => {
        const cache = role === 'control' ? refs.current : companionRefs.current;
        let ref = cache.get(key);
        if (ref !== undefined) return ref;
        ref = (el: HTMLElement | null) => {
            const mine = role === 'control' ? controls.current : companions.current;
            if (el !== null) {
                mine.set(key, el);
                // A control given a row's ref again has not been taken away.
                successors.delete(el);
                return;
            }
            const control = mine.get(key);
            if (control === undefined) return;
            mine.delete(key);
            // React detaches a ref before it takes the element off the page, so
            // whether the control still holds focus is read here (a companion's
            // anywhere inside it). The next and previous rows' controls take
            // focus, whichever of the row's own controls held it.
            const drawn = order.current;
            const map = controls.current;
            successors.set(control, () => {
                const at = drawn.indexOf(key);
                for (let i = at + 1; i < drawn.length; i++) {
                    const next = map.get(drawn[i]!);
                    if (usable(next)) return next;
                }
                for (let i = at - 1; i >= 0; i--) {
                    const previous = map.get(drawn[i]!);
                    if (usable(previous)) return previous;
                }
                const add = homeRef.current.current ?? undefined;
                return usable(add) ? add : null;
            });
            const within = role === 'companion';
            if (holdsFocus(control, document.activeElement, within)) owed.current = { key, control, within };
        };
        cache.set(key, ref);
        return ref;
    }, []);
}
