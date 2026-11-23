import {
    createContext,
    createElement,
    useContext,
    useEffect,
    useLayoutEffect,
    useRef,
    useState,
    type ChangeEvent,
    type ChangeEventHandler,
    type KeyboardEvent,
    type KeyboardEventHandler,
    type MouseEvent,
    type MouseEventHandler,
    type ReactNode,
    type RefObject,
} from 'react';
import { QueryClientContext, type Mutation, type Query, type QueryClient } from '@tanstack/react-query';

/**
 * A button that is busy, not unavailable (ADR-0023 §L): a request it waits on
 * is pending.
 *
 * THE RULE. While its request is pending a button keeps focus and its place in
 * the tab order, and refuses to act. It says so with aria-disabled and
 * aria-busy, never with the native disabled attribute. A focused button that
 * becomes disabled loses focus to the page, and after a refusal from the server
 * the dialog or form stays open with nothing in it focused. Native disabled
 * stays for a button that is unavailable for another reason: a required field
 * still empty, a choice not yet made, no permission.
 *
 * WHAT IT REFUSES. Every way a button is activated arrives as a click: a press
 * of the mouse, Enter or Space on the button, and Enter in a field of its form,
 * which clicks the form's default button (implicit submission). While pending
 * that click is refused: the button's own handler is not called, and the click
 * is cancelled (preventDefault), which also keeps a submit button from
 * submitting its form. So a second press, from anywhere, sends no second
 * request.
 *
 * FROM THE PRESS, NOT FROM THE RENDER. The pending state a page passes in
 * renders some time after the press: TanStack Query tells a component its
 * mutation is pending on a later task, and React renders after that. A second
 * press in between would reach the handler again. So the press holds the
 * button itself: every request the press starts, a TanStack mutation or fetch
 * started while its handler runs (a submit button's form is submitted inside
 * that run too), and a promise its handler returns, holds the button until
 * each has settled, and a press on a held button is refused as a pending one
 * is. The hold is taken and checked synchronously, with no timer, so it does
 * not depend on when anything renders. A press that starts no request (a
 * refusal of what was typed, a press that opens a confirm) holds nothing, and
 * the next press acts. A handler therefore starts its request in the press:
 * one started after an await or a timer is not the press's, unless the promise
 * the handler returns waits for it. Another way into the same action (a field
 * whose Enter runs it) clicks the button rather than calling the handler, so
 * it shares the hold.
 *
 * A PRESS HOLDS ITS GROUP. Controls that wait on the same requests, or must
 * exclude each other (a dialog's Post and Skip of one occurrence, a panel
 * whose two buttons write one setting), are one group: wrapped in a
 * PendingScope, which may span components. A press in a group holds every
 * control in it, buttons and choices alike, until what the press started has
 * settled: a press on another control of the group in that time is refused as
 * a pending one is, so a second, conflicting request never goes out in the
 * window before the page's pending state renders. Outside a PendingScope a
 * press holds its own control only: a list whose rows each wait on their own
 * request is not one group.
 *
 * NEVER A CHANGE OF SIZE. A button that swaps its label while pending does it
 * through `pendingLabel` (Button, an ActionFooter slot) or PendingLabel (a raw
 * button, or a Button whose label swaps for its own request only), which lays
 * both labels out from the first render in one grid cell: the button's box is
 * the wider of the two, at rest and while pending alike. A button that narrows
 * under the pointer lets the second press of a double click land beside it,
 * and focus goes to the page; one that grows pushes a right-aligned button and
 * its neighbours aside while a retry is in flight.
 *
 * HOW IT LOOKS. As a disabled button does. Button and IconButton map their
 * disabled styles to aria-disabled; a raw button that uses this helper maps its
 * own. One style is not mapped: pointer-events-none, which a disabled Button
 * uses to drop its hover. A press on a button with no pointer events lands on
 * what is behind it, and that takes focus away, so a pending button keeps its
 * pointer events and restates its resting look under `aria-disabled:hover:`
 * instead, which shows no hover.
 */

/** The props that make a button pending, or that leave it as it is. */
export interface PendingButtonProps<E extends HTMLElement> {
    'aria-disabled'?: true;
    'aria-busy'?: true;
    onClick?: MouseEventHandler<E>;
}

/**
 * Makes the props for a button that is pending when `pending` is true:
 * announced as busy and unavailable, focusable, and refusing the click that
 * every way of activating it fires. When `pending` is false the click goes to
 * `onClick`, and the press holds the button until the requests it started have
 * settled. When `pending` is undefined the button is not in the mechanism and
 * `onClick` is passed on as it is. Spread after the button's other props, and
 * pass its click handler here rather than to the button.
 */
export type PendingButtonPropsFor = <E extends HTMLElement>(
    pending: boolean | undefined,
    onClick?: MouseEventHandler<E>,
) => PendingButtonProps<E>;

/**
 * The maker of a button's pending props, bound to the page's QueryClient, whose
 * requests a press holds the button for. Call it once in the component, and
 * call what it returns for each button, in a list as well.
 */
export function usePendingButtonProps(): PendingButtonPropsFor {
    const client = useContext(QueryClientContext);
    const group = useContext(PendingScopeContext);
    return (pending, onClick) => {
        if (pending === undefined) return { onClick };
        if (pending) return { 'aria-disabled': true, 'aria-busy': true, onClick: refuse };
        return { onClick: pressFor(client, group, onClick) };
    };
}

/** A pending button's click: its handler is not called, and the click does nothing. */
function refuse(event: MouseEvent): void {
    event.preventDefault();
}

/**
 * The controls that wait on the same requests (PendingScope): each press in
 * the group whose requests have not all settled.
 */
interface HoldGroup {
    holds: Set<object>;
}

const PendingScopeContext = createContext<HoldGroup | null>(null);

/**
 * Makes the controls inside one group (A PRESS HOLDS ITS GROUP, above): a press
 * of any of them holds them all until what it started has settled.
 */
export function PendingScope({ children }: { children?: ReactNode }): ReactNode {
    const [group] = useState<HoldGroup>(() => ({ holds: new Set() }));
    return createElement(PendingScopeContext.Provider, { value: group }, children);
}

/** The buttons held by a press whose requests have not all settled. */
const held = new WeakSet<HTMLElement>();

/** Whether a control is held: by its own press, or by one in its group. */
function isHeld(control: HTMLElement, group: HoldGroup | null): boolean {
    return held.has(control) || (group !== null && group.holds.size > 0);
}

/** The requests a press started. */
interface Started {
    mutations: ReadonlySet<Mutation>;
    queries: ReadonlySet<Query>;
}

/**
 * A button's click that refuses while the button is held, and otherwise runs
 * the handler, notes what it started, and holds the button until that settles.
 */
function pressFor<E extends HTMLElement>(
    client: QueryClient | undefined,
    group: HoldGroup | null,
    onClick: MouseEventHandler<E> | undefined,
): MouseEventHandler<E> {
    return (event) => {
        const button = event.currentTarget;
        if (isHeld(button, group)) {
            event.preventDefault();
            return;
        }
        const stop = client === undefined ? undefined : watchStarts(client);
        let returned: unknown;
        try {
            returned = onClick?.(event);
            submitWithin(event);
        } finally {
            const started = stop?.() ?? { mutations: new Set(), queries: new Set() };
            holdUntilSettled(button, group, client, started, returned);
        }
    };
}

/** Notes every mutation and fetch that starts until the returned stop is called. */
function watchStarts(client: QueryClient): () => Started {
    const mutations = new Set<Mutation>();
    const queries = new Set<Query>();
    const stopMutations = client.getMutationCache().subscribe((event) => {
        if (event.type === 'updated' && event.action.type === 'pending') mutations.add(event.mutation);
    });
    const stopQueries = client.getQueryCache().subscribe((event) => {
        if (event.type === 'updated' && event.action.type === 'fetch') queries.add(event.query);
    });
    return () => {
        stopMutations();
        stopQueries();
        return { mutations, queries };
    };
}

/**
 * A submit button's press submits its form here, inside the press, so a request
 * the form's submit handler starts is the press's own. The click's own
 * submission, which would come after the press, is cancelled; requestSubmit
 * checks the form's fields as that submission would.
 */
function submitWithin(event: MouseEvent<HTMLElement>): void {
    const button = event.currentTarget;
    if (event.isDefaultPrevented()) return;
    if (!(button instanceof HTMLButtonElement) || button.type !== 'submit' || button.form === null) return;
    event.preventDefault();
    button.form.requestSubmit(button);
}

const isThenable = (value: unknown): value is PromiseLike<unknown> =>
    typeof value === 'object' && value !== null && typeof (value as { then?: unknown }).then === 'function';

/**
 * Holds `button`, and its group, until every request in `started`, and the
 * promise `returned`, has settled.
 */
function holdUntilSettled(
    button: HTMLElement,
    group: HoldGroup | null,
    client: QueryClient | undefined,
    started: Started,
    returned: unknown,
): void {
    const mutations = new Set([...started.mutations].filter((m) => m.state.status === 'pending'));
    const queries = new Set([...started.queries].filter((q) => q.state.fetchStatus !== 'idle'));
    const promise = isThenable(returned) ? returned : undefined;
    if (mutations.size === 0 && queries.size === 0 && promise === undefined) return;
    let waiting = promise !== undefined;
    const stops: Array<() => void> = [];
    const hold = {};
    const settle = () => {
        if (mutations.size > 0 || queries.size > 0 || waiting) return;
        for (const stop of stops) stop();
        held.delete(button);
        group?.holds.delete(hold);
    };
    held.add(button);
    group?.holds.add(hold);
    if (client !== undefined && mutations.size > 0) {
        stops.push(client.getMutationCache().subscribe((event) => {
            const mutation = event.mutation;
            if (mutation === undefined || !mutations.has(mutation)) return;
            if (event.type === 'removed' || mutation.state.status !== 'pending') {
                mutations.delete(mutation);
                settle();
            }
        }));
    }
    if (client !== undefined && queries.size > 0) {
        stops.push(client.getQueryCache().subscribe((event) => {
            if (!queries.has(event.query)) return;
            if (event.type === 'removed' || event.query.state.fetchStatus === 'idle') {
                queries.delete(event.query);
                settle();
            }
        }));
    }
    // A rejection still reaches the page as it would have without the hold.
    promise?.then(
        () => { waiting = false; settle(); },
        (error: unknown) => { waiting = false; settle(); throw error; },
    );
}

/**
 * Hands focus on when a button that holds it becomes unavailable (ADR-0023 §L).
 *
 * A pending button keeps focus through its request. When the request's own
 * success leaves the button disabled (it cleared the field the button waits
 * on, or left nothing changed), a browser drops focus to the page. Focus goes
 * to `fallback` instead: the field the button waits on. A field disabled while
 * the request was pending can take focus only once it is enabled again, so
 * focus stays owed to it until a later render of the button finds it enabled.
 * Focus the user has put somewhere else since is left where it is, and a button
 * enabled again before the fallback could take focus takes it back itself.
 *
 * Whether the button holds focus is followed through its focus and blur, not
 * read from the page: a browser moves focus off a disabled control to no
 * element, which a blur says with no element focus went to, and that leaves
 * the button holding it as far as this hook is concerned. jsdom leaves focus
 * on the disabled control instead, and both arrive at the same answer.
 */
export function useFallbackFocus(
    button: RefObject<HTMLElement | null>,
    disabled: boolean,
    fallback: RefObject<HTMLElement | null> | undefined,
): void {
    const held = useRef(false);
    const owed = useRef(false);

    useEffect(() => {
        const el = button.current;
        if (el === null || fallback === undefined) return;
        held.current = document.activeElement === el;
        const onFocus = () => { held.current = true; };
        // Focus that went to another element was moved there; focus that went
        // to none was dropped, as a browser drops it from a disabled control.
        const onBlur = (event: FocusEvent) => { if (event.relatedTarget !== null) held.current = false; };
        el.addEventListener('focus', onFocus);
        el.addEventListener('blur', onBlur);
        return () => {
            el.removeEventListener('focus', onFocus);
            el.removeEventListener('blur', onBlur);
        };
    }, [button, fallback]);

    useLayoutEffect(() => {
        const el = button.current;
        if (el === null || fallback === undefined) return;
        if (disabled && held.current) {
            held.current = false;
            owed.current = true;
        }
        if (!owed.current) return;
        const active = document.activeElement;
        if (active !== el && active !== null && active !== document.body) {
            owed.current = false;
            return;
        }
        const target = disabled ? fallback.current : el;
        target?.focus();
        if (target != null && document.activeElement === target) owed.current = false;
    });
}

/**
 * A field that is busy, not unavailable (ADR-0023 §L): the request it belongs
 * to is pending.
 *
 * THE RULE. The button rule applies to fields. While its request is pending a
 * field keeps focus and its place in the tab order, and refuses input. It says
 * so with aria-disabled and aria-busy, never with the native disabled
 * attribute: a focused field that becomes disabled drops focus to the page, so
 * Enter in a sign-in field, which submits the form, would leave nothing
 * focused. Native disabled stays for a field that is unavailable for another
 * reason: no permission, a choice that does not apply.
 *
 * HOW IT REFUSES. A text-like field (an input that takes typing, a textarea)
 * is readOnly: typing, pasting, cutting and dropping change nothing, and a date,
 * time or number field's own picker and steppers do nothing. A choice (a
 * select, a checkbox, a radio, a switch, a file field) has no readOnly, so its
 * change is refused: a change is not handed on, so a controlled field keeps its
 * value, and React puts a clicked checkbox or radio back as it was; the click
 * of any other choice is cancelled, which keeps a file field's picker shut; a
 * press of the pointer is cancelled, which keeps a select's list shut; and a
 * key that would change it or open its list is cancelled (Space, a letter, an
 * arrow, Enter), while Tab still leaves it. A checkbox's or radio's click is not
 * cancelled as well: the browser would then put back, after React has, the
 * state from before the click, and an engine that does that by toggling leaves
 * it changed. A field component (a picker, a date field) takes `pending` and
 * refuses its own ways of changing the value (a list, a shortcut) the same
 * way, through these helpers.
 *
 * HOW IT LOOKS. As the field primitives' disabled field does
 * (cursor-not-allowed, opacity-50). The primitives (Input, Checkbox) map their
 * disabled styles to aria-disabled; a raw field with disabled styles of its own
 * maps those; a raw field with none takes the primitives' look, for its
 * disabled state as well as its pending one, so the two never differ.
 */

/** The handlers a choice refuses while pending. */
export interface FieldHandlers<E extends HTMLElement> {
    onChange?: ChangeEventHandler<E>;
    onClick?: MouseEventHandler<E>;
    onKeyDown?: KeyboardEventHandler<E>;
    onMouseDown?: MouseEventHandler<E>;
}

/** The props that make a field pending, or that leave it as it is. */
export interface PendingFieldProps<E extends HTMLElement> extends FieldHandlers<E> {
    readOnly?: true;
    'aria-disabled'?: true;
    'aria-busy'?: true;
}

/**
 * The props for a text-like field that is pending when `pending` is true:
 * announced as busy and unavailable, focusable, and read-only. When `pending`
 * is false or undefined nothing is added. Spread after the field's other props.
 */
export function pendingTextProps<E extends HTMLElement>(pending: boolean | undefined): PendingFieldProps<E> {
    return pending === true ? { readOnly: true, 'aria-disabled': true, 'aria-busy': true } : {};
}

/**
 * The props for a choice (select, checkbox, radio, switch) that is pending
 * when `pending` is true: announced as busy and unavailable, focusable, and
 * refusing every change. Otherwise its handlers go through as they are. Spread
 * after the field's other props, and pass its handlers here rather than to the
 * field.
 */
export function pendingChoiceProps<E extends HTMLElement>(
    pending: boolean | undefined,
    handlers: FieldHandlers<E> = {},
): PendingFieldProps<E> {
    if (pending !== true) return { ...handlers };
    return {
        'aria-disabled': true,
        'aria-busy': true,
        onChange: ignoreChange,
        onClick: refuseClick,
        onMouseDown: refuse,
        onKeyDown: refuseKey,
    };
}

/**
 * The maker of a choice's props, as usePendingButtonProps is for a button,
 * for a choice whose change writes (a switch that saves a setting, a select
 * that changes a member's role). Bound to the page's QueryClient and to the
 * choice's PendingScope. When `pending` is true the choice is pending, as
 * pendingChoiceProps makes it. When it is false, the change holds the choice
 * and its group from the change, as a press holds a button: every request the
 * change's handler starts, and a promise it returns, holds them until each has
 * settled, and a change of a held choice is refused as a pending one is (not
 * handed on, so React puts a controlled field back), as are its click, the
 * press of the pointer and the keys that would change it. So a second change,
 * of the same choice or another in the group, sends no second request in the
 * window before the page's pending state renders. When `pending` is undefined
 * the choice is not in the mechanism and its handlers go through as they are.
 */
export type PendingChoicePropsFor = <E extends HTMLElement>(
    pending: boolean | undefined,
    handlers?: FieldHandlers<E>,
) => PendingFieldProps<E>;

export function usePendingChoiceProps(): PendingChoicePropsFor {
    const client = useContext(QueryClientContext);
    const group = useContext(PendingScopeContext);
    return (pending, handlers = {}) => {
        if (pending === undefined) return { ...handlers };
        if (pending) return pendingChoiceProps(true, handlers);
        return changeFor(client, group, handlers);
    };
}

/** A choice's handlers that refuse while it is held, and hold it from a change that writes. */
function changeFor<E extends HTMLElement>(
    client: QueryClient | undefined,
    group: HoldGroup | null,
    handlers: FieldHandlers<E>,
): PendingFieldProps<E> {
    return {
        onChange: (event: ChangeEvent<E>) => {
            const field = event.currentTarget;
            if (isHeld(field, group)) return;
            const stop = client === undefined ? undefined : watchStarts(client);
            let returned: unknown;
            try {
                returned = handlers.onChange?.(event);
            } finally {
                const started = stop?.() ?? { mutations: new Set(), queries: new Set() };
                holdUntilSettled(field, group, client, started, returned);
            }
        },
        onClick: (event: MouseEvent<E>) => {
            if (isHeld(event.currentTarget, group)) refuseClick(event);
            else handlers.onClick?.(event);
        },
        onMouseDown: (event: MouseEvent<E>) => {
            if (isHeld(event.currentTarget, group)) refuse(event);
            else handlers.onMouseDown?.(event);
        },
        onKeyDown: (event: KeyboardEvent<E>) => {
            if (isHeld(event.currentTarget, group)) {
                refuseKey(event);
                if (event.defaultPrevented) return;
            }
            handlers.onKeyDown?.(event);
        },
    };
}

/** A pending choice's change: not handed on, so a controlled field keeps its value. */
function ignoreChange(): void {}

/**
 * A pending choice's click: cancelled, except on a checkbox or radio, which
 * React puts back to its controlled value once its change is not handed on.
 */
function refuseClick(event: MouseEvent<HTMLElement>): void {
    const el = event.currentTarget;
    if (el instanceof HTMLInputElement && (el.type === 'checkbox' || el.type === 'radio')) return;
    event.preventDefault();
}

/**
 * The named keys that change a choice or open its list: an arrow, Enter, the
 * keys that jump through a list or clear a value, and F4, which opens a
 * select's list in some browsers.
 */
const CHANGING_KEYS = new Set([
    'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Enter', 'Home', 'End', 'PageUp', 'PageDown',
    'Backspace', 'Delete', 'F4',
]);

/**
 * A pending field's key: refused when it would change the value or open a
 * list, a named key above or a character (Space, a letter that picks the
 * option it starts) typed without Control or Meta. Every other key goes
 * through: Tab and Shift+Tab leave the field, and a shortcut of the page or
 * the browser still works.
 */
export function refuseKey(event: KeyboardEvent): void {
    const character = event.key.length === 1 && !event.ctrlKey && !event.metaKey;
    if (character || CHANGING_KEYS.has(event.key)) event.preventDefault();
}
