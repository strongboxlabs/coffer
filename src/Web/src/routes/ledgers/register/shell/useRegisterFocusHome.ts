import { useEffect, useRef } from 'react';

/**
 * The stand-in that holds focus for a list not on the page, until the reader
 * presses anything or the list comes back: focus the hook gave the add control
 * or the search box, or focus the date jump gave them as it closed
 * (registerFocusHome). The module's rather than one hook's, since the date jump
 * gives it from outside the hook; a page draws one register at a time.
 */
let parked: HTMLElement | null = null;

/**
 * The register's focus home is its row list (ADR-0023 §D, §E, §L).
 *
 * Rows never take focus: the arrows move a highlighted row from anywhere on the
 * page (useRegisterKeyboardNav). But a control that takes itself away leaves
 * focus on the page, outside every control: the bulk bar's Clear selection and
 * a bulk action that clears the selection take the bar's buttons with them, and
 * an inline edit or the new-row form takes its fields and buttons with it
 * however it closes (Escape, Cancel, a Save, Accept or Post that closes it).
 * Focus goes to the row list instead, which is focusable but no Tab stop of its
 * own; there the arrows, Enter and N work as anywhere else, and the next Tab
 * goes on from the list.
 *
 * WHEN THE LIST IS NOT ON THE PAGE. An empty register draws a placeholder
 * instead of the list, and a register that is loading draws neither: the last
 * rows deleted, moved or filtered away, the new-row form closed in an empty
 * account, or the list taken away while it held focus. Focus goes to the
 * register's add control, "+ New transaction", when it is on the page and
 * enabled, else to the search box, the register's other home (a category
 * register, a viewer, an account whose currency is not known yet). Focus put
 * there stands in for the list: if the list comes back (the first transaction
 * of an empty account just saved, a refresh that lands) while that focus is
 * still where it was put and the reader has pressed nothing since, the list
 * takes it.
 *
 * Focus the date jump gives the add control or the search box as it closes with
 * nothing to give it back to (it was opened with nothing focused, or what it was
 * opened from has left the page), stands in for the list the same way
 * (registerFocusHome).
 *
 * Only focus that was lost moves: focus on the page, or on nothing. A press
 * outside an editor closes it and puts focus where it landed, and focus the
 * user moved elsewhere before the edit closed stays there.
 *
 * AN EDITOR DRAWN AS A ROW. An existing row's inline edit is drawn inside the
 * list, by the list's own renderer, and the list draws its rows in a commit of
 * its own after the one that closes the editor: Virtuoso takes the page's new
 * row renderer in a layout effect and redraws its items from that. So when the
 * editing flag goes false the editor's field or button still holds focus, and
 * it leaves the page only after this hook has looked. Focus still held inside
 * the list as an edit closes is therefore followed: when the control that held
 * it leaves the page and focus has gone to nothing, the list takes it. Focus
 * that moves to another element first, or a key or press of the reader's,
 * ends the wait; a control that stays on the page keeps focus.
 */
export function useRegisterFocusHome(
    list: HTMLElement | null,
    { editing, hasSelection }: { editing: boolean; hasSelection: boolean },
): void {
    const before = useRef({ editing, hasSelection, list });
    const owed = useRef(false);
    // Stops following a control in a row that still held focus as an edit
    // closed (AN EDITOR DRAWN AS A ROW).
    const following = useRef<(() => void) | null>(null);

    useEffect(() => {
        const clear = () => { parked = null; };
        document.addEventListener('keydown', clear, true);
        document.addEventListener('pointerdown', clear, true);
        return () => {
            document.removeEventListener('keydown', clear, true);
            document.removeEventListener('pointerdown', clear, true);
            following.current?.();
            parked = null;
        };
    }, []);

    useEffect(() => {
        following.current?.();
        following.current = null;
        const was = before.current;
        before.current = { editing, hasSelection, list };
        if ((was.editing && !editing) || (was.hasSelection && !hasSelection)) owed.current = true;
        // The list left the page; focus it held went with it.
        if (was.list !== null && list === null) owed.current = true;
        const holder = document.activeElement;
        const lost = holder === null || holder === document.body;

        if (list !== null) {
            const standIn = parked;
            parked = null;
            const wasOwed = owed.current;
            owed.current = false;
            if ((wasOwed && lost) || (standIn !== null && holder === standIn)) {
                list.focus({ preventScroll: true });
            } else if (wasOwed && holder instanceof HTMLElement && holder !== list && list.contains(holder)) {
                following.current = focusListWhenGone(list, holder);
            }
            return;
        }
        if (!owed.current) return;
        if (!lost) {
            owed.current = false;
            return;
        }
        // With no home on the page either, the list takes focus once it is.
        const home = registerHome();
        if (home === null) return;
        owed.current = false;
        home.focus({ preventScroll: true });
        parked = home;
    }, [list, editing, hasSelection]);
}

/**
 * Follows `holder`, a control in a row of `list` that held focus as an edit
 * closed, and gives focus to the list once the control has left the page with
 * focus gone to nothing. The list redraws its rows by changing what is inside
 * it, so a change there is when to look. Focus that moves to another element,
 * and the reader's next key or press, end the wait. Returns the stop.
 */
function focusListWhenGone(list: HTMLElement, holder: HTMLElement): () => void {
    const observer = new MutationObserver(() => {
        if (holder.isConnected) return;
        stop();
        const active = document.activeElement;
        if (active === null || active === document.body) list.focus({ preventScroll: true });
    });
    const onFocusIn = (event: FocusEvent) => {
        if (event.target !== holder) stop();
    };
    function stop() {
        observer.disconnect();
        document.removeEventListener('focusin', onFocusIn, true);
        document.removeEventListener('keydown', stop, true);
        document.removeEventListener('pointerdown', stop, true);
    }
    observer.observe(list, { childList: true, subtree: true });
    document.addEventListener('focusin', onFocusIn, true);
    document.addEventListener('keydown', stop, true);
    document.addEventListener('pointerdown', stop, true);
    return stop;
}

/**
 * The register's focus home as it is on the page now: the row list
 * (RegisterScrollSurface marks it), else, while the list is not on the page, the
 * add control, else the search box. For focus that has nowhere to go back to
 * outside this hook: the date jump closed when it was opened with nothing
 * focused, or after what it was opened from has left the page. The caller gives
 * focus to what this returns, so the add control or the search box is recorded
 * as the list's stand-in, as the hook records the focus it gives them: the list
 * takes focus back when it comes onto the page, if the reader has pressed
 * nothing since.
 */
export function registerFocusHome(): HTMLElement | null {
    const list = document.querySelector<HTMLElement>('[data-register-home="list"]');
    if (list !== null) return list;
    parked = registerHome();
    return parked;
}

/**
 * Where the register's focus goes while its list is not on the page: the add
 * control, else the search box.
 */
function registerHome(): HTMLElement | null {
    const add = document.querySelector<HTMLButtonElement>('[data-register-home="add"]');
    if (add !== null && !add.disabled) return add;
    return document.querySelector<HTMLElement>('[data-register-home="search"]');
}
