import { useRef, type FormEvent, type ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider, useMutation, useQuery } from '@tanstack/react-query';

import { Button } from './Button';
import { ConfirmDialog } from './ConfirmDialog';
import { IconButton } from './IconButton';
import { PendingLabel } from './PendingLabel';
import { PendingScope, usePendingButtonProps } from './pending';

// A button whose request is pending keeps focus and refuses to act (ADR-0023
// §L). Every way of activating a button arrives as a click (a press, Enter or
// Space on it, Enter in a field of its form), so each is pressed here, and each
// must send nothing while the button is pending.
//
// jsdom leaves focus on a control that becomes disabled, where a browser drops
// it to the page, so a focus check alone passes either way. What keeps focus on
// a pending button in the browser is that it is never natively disabled, and
// that is asserted wherever focus is.

/** A raw button on the shared helper, as a site that cannot use Button writes it. */
function RawButton({ pending, onClick }: { pending: boolean; onClick: () => void }) {
    const pendingProps = usePendingButtonProps();
    return (
        <button type="button" className="disabled:opacity-50 aria-disabled:opacity-50" {...pendingProps(pending, onClick)}>
            Raw
        </button>
    );
}

const kinds = [
    ['Button', (pending: boolean, onClick: () => void) => <Button pending={pending} onClick={onClick}>Save</Button>, 'Save'],
    ['IconButton', (pending: boolean, onClick: () => void) => (
        <IconButton aria-label="Sync" pending={pending} onClick={onClick}>↻</IconButton>
    ), 'Sync'],
    ['a raw button on the helper', (pending: boolean, onClick: () => void) => <RawButton pending={pending} onClick={onClick} />, 'Raw'],
] as const;

describe('a pending button', () => {
    it.each(kinds)('%s refuses a press, Enter and Space while pending, keeps focus, and acts once it is not', async (_kind, make, name) => {
        const onClick = vi.fn();
        const user = userEvent.setup();
        const { rerender } = render(<div><input aria-label="Before" />{make(false, onClick)}</div>);
        const button = screen.getByRole('button', { name });
        button.focus();

        rerender(<div><input aria-label="Before" />{make(true, onClick)}</div>);
        // Busy, not unavailable: it says so, and keeps focus and its tab stop.
        expect(button).toHaveAttribute('aria-disabled', 'true');
        expect(button).toHaveAttribute('aria-busy', 'true');
        expect(button).toBeEnabled();
        expect(button).toHaveFocus();

        await user.click(button);
        await user.keyboard('{Enter}');
        await user.keyboard(' ');
        expect(onClick).not.toHaveBeenCalled();
        expect(button).toHaveFocus();

        // Reached by Tab like any other control.
        screen.getByRole('textbox', { name: 'Before' }).focus();
        await user.tab();
        expect(button).toHaveFocus();

        rerender(<div><input aria-label="Before" />{make(false, onClick)}</div>);
        expect(button).not.toHaveAttribute('aria-disabled');
        expect(button).not.toHaveAttribute('aria-busy');
        await user.click(button);
        expect(onClick).toHaveBeenCalledTimes(1);
    });

    it('keeps a form from submitting on Enter in its field, and on Enter or Space on the button', async () => {
        const onSubmit = vi.fn((e: FormEvent) => e.preventDefault());
        const user = userEvent.setup();
        const form = (pending: boolean) => (
            <form onSubmit={onSubmit}>
                <input aria-label="Name" />
                <Button type="submit" pending={pending}>Save</Button>
            </form>
        );
        const { rerender } = render(form(true));
        const field = screen.getByRole('textbox', { name: 'Name' });

        await user.type(field, 'A name{Enter}');
        expect(field).toHaveFocus();
        screen.getByRole('button', { name: 'Save' }).focus();
        await user.keyboard('{Enter}');
        await user.keyboard(' ');
        await user.click(screen.getByRole('button', { name: 'Save' }));
        expect(onSubmit).not.toHaveBeenCalled();

        rerender(form(false));
        await user.type(field, '{Enter}');
        expect(onSubmit).toHaveBeenCalledTimes(1);
    });

    it('sends one request for a double press, Enter and Space while the first is in flight', async () => {
        let finish!: () => void;
        const send = vi.fn(() => new Promise<void>((resolve) => { finish = resolve; }));
        function Saver() {
            const save = useMutation({ mutationFn: send });
            return <Button pending={save.isPending} pendingLabel="Saving…" onClick={() => save.mutate()}>Save</Button>;
        }
        const user = userEvent.setup();
        render(<QueryClientProvider client={new QueryClient()}><Saver /></QueryClientProvider>);

        await user.dblClick(screen.getByRole('button', { name: 'Save' }));
        const busy = await screen.findByRole('button', { name: 'Saving…' });
        await user.keyboard('{Enter}');
        await user.keyboard(' ');
        await user.click(busy);
        expect(send).toHaveBeenCalledTimes(1);
        expect(busy).toHaveAttribute('aria-busy', 'true');
        expect(busy).toBeEnabled();
        expect(busy).toHaveFocus();

        finish();
        await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).not.toHaveAttribute('aria-busy'));
        expect(screen.getByRole('button', { name: 'Save' })).toHaveFocus();
    });

    it('stays natively disabled when it is unavailable, pending or not', async () => {
        const onClick = vi.fn();
        render(<Button disabled pending onClick={onClick}>Save</Button>);
        expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
        await userEvent.click(screen.getByRole('button', { name: 'Save' }));
        expect(onClick).not.toHaveBeenCalled();
    });
});

// A success that clears the field a button waits on, or leaves nothing changed,
// disables the button it was pressed on while it holds focus, and a browser
// drops focus to the page. The button hands focus to the field it waits on.
// jsdom leaves focus on the disabled button, so focus on the field is what
// shows it was handed on.
describe('a button that becomes unavailable holding focus', () => {
    function Form({ disabled, fieldDisabled = false }: { disabled: boolean; fieldDisabled?: boolean }) {
        const field = useRef<HTMLInputElement>(null);
        return (
            <div>
                <input ref={field} aria-label="Name" disabled={fieldDisabled} />
                <input aria-label="Elsewhere" />
                <Button disabled={disabled} fallbackFocusRef={field}>Add</Button>
            </div>
        );
    }

    it('gives focus to the field it waits on', () => {
        const { rerender } = render(<Form disabled={false} />);
        const add = screen.getByRole('button', { name: 'Add' });
        add.focus();

        rerender(<Form disabled />);

        expect(add).toBeDisabled();
        expect(screen.getByRole('textbox', { name: 'Name' })).toHaveFocus();
    });

    it('gives it once that field is enabled, when the field was disabled as the button was', () => {
        const { rerender } = render(<Form disabled={false} fieldDisabled />);
        screen.getByRole('button', { name: 'Add' }).focus();

        rerender(<Form disabled fieldDisabled />);
        expect(screen.getByRole('textbox', { name: 'Name' })).not.toHaveFocus();
        rerender(<Form disabled />);

        expect(screen.getByRole('textbox', { name: 'Name' })).toHaveFocus();
    });

    it('leaves focus where the user put it since', async () => {
        const { rerender } = render(<Form disabled={false} />);
        screen.getByRole('button', { name: 'Add' }).focus();
        await userEvent.tab({ shift: true });
        expect(screen.getByRole('textbox', { name: 'Elsewhere' })).toHaveFocus();

        rerender(<Form disabled />);

        expect(screen.getByRole('textbox', { name: 'Elsewhere' })).toHaveFocus();
    });

    it('takes nothing from a button that did not hold focus', () => {
        const { rerender } = render(<Form disabled={false} />);
        const elsewhere = screen.getByRole('textbox', { name: 'Elsewhere' });
        elsewhere.focus();

        rerender(<Form disabled />);

        expect(elsewhere).toHaveFocus();
    });
});

// The look: a pending button looks as a disabled one does. Each disabled
// style has its aria-disabled twin, but for pointer-events-none, which would
// let a press through to what is behind the button and take focus with it: a
// pending button keeps its pointer events, shows the default cursor, and
// restates its resting look under hover instead.
describe('a pending button looks as a disabled one does', () => {
    const looks = [
        ['primary', <Button key="p" variant="primary">x</Button>],
        ['secondary', <Button key="s" variant="secondary">x</Button>],
        ['ghost', <Button key="g" variant="ghost">x</Button>],
        ['danger', <Button key="d" variant="danger">x</Button>],
        ['link', <Button key="l" variant="link" size="xs">x</Button>],
        ['IconButton', <IconButton key="i" aria-label="x">x</IconButton>],
    ] as const;

    it.each(looks)('%s', (_name, element) => {
        render(element);
        const classes = screen.getByRole('button').className.split(/\s+/);
        const twin = (style: string) => `aria-disabled:${style}`;
        for (const style of classes.filter((c) => c.startsWith('disabled:')).map((c) => c.slice('disabled:'.length))) {
            if (style === 'pointer-events-none') {
                expect(classes).toContain(twin('cursor-default'));
                expect(classes).not.toContain(twin('pointer-events-none'));
            } else {
                expect(classes).toContain(twin(style));
            }
        }
        // Every property its hover changes is put back while pending.
        const hovered = classes.filter((c) => c.startsWith('hover:')).map((c) => c.slice('hover:'.length));
        expect(hovered.length).toBeGreaterThan(0);
        for (const style of hovered) {
            const property = style === 'underline' ? 'no-underline' : style.split('-')[0]!;
            expect(classes.some((c) => c.startsWith(`aria-disabled:hover:${property}`)), style).toBe(true);
        }
    });
});

// The state that drives it is read here as the components read it.
describe('usePendingButtonProps', () => {
    const pendingProps = () => renderHook(() => usePendingButtonProps()).result.current;

    it('passes the click of a button outside the mechanism on as it is', () => {
        const onClick = vi.fn();
        expect(pendingProps()(undefined, onClick)).toEqual({ onClick });
    });

    it('marks nothing on a button that is not pending, and runs its click through the press', () => {
        const onClick = vi.fn();
        const props = pendingProps()(false, onClick);
        expect(props['aria-disabled']).toBeUndefined();
        expect(props['aria-busy']).toBeUndefined();
        expect(props.onClick).not.toBe(onClick);
    });

    it('marks a pending button busy and unavailable, and swaps its click for a refusal', () => {
        const onClick = vi.fn();
        const props = pendingProps()(true, onClick);
        expect(props['aria-disabled']).toBe(true);
        expect(props['aria-busy']).toBe(true);
        expect(props.onClick).not.toBe(onClick);
    });
});

// A press holds its button from the press until what it started has settled
// (ADR-0023 §L, pending.ts), not from the render of the pending state: TanStack
// Query tells a component its mutation is pending on a later task (its notify
// scheduler is a setTimeout), and React renders after that. Real timers here:
// each pair of presses is dispatched before that timeout can fire, as a
// browser's back-to-back presses are, so only the press's own hold stands
// between the second press and a second request.

/** A request the test settles: answer() resolves it, refuse() rejects it. */
function settledByTest() {
    const answers: Array<() => void> = [];
    const refusals: Array<() => void> = [];
    const send = vi.fn(() => new Promise<void>((resolve, reject) => {
        answers.push(resolve);
        refusals.push(() => reject(new Error('Refused.')));
    }));
    return { send, answer: () => answers.shift()?.(), refuse: () => refusals.shift()?.() };
}

function withClient(ui: ReactNode) {
    const client = new QueryClient({ defaultOptions: { mutations: { retry: false }, queries: { retry: false } } });
    return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

/** Settles what is queued: a task, so TanStack's notify timeout runs, then React's render. */
const settle = () => act(() => new Promise<void>((resolve) => { setTimeout(resolve, 0); }));

/**
 * Lets the microtasks run, and no task: a mutation calls its function a
 * microtask after mutate(), while TanStack tells the page on a later task, so
 * after this the request is sent and the pending state has not rendered.
 */
const ticks = async () => { for (let i = 0; i < 10; i += 1) await Promise.resolve(); };

describe('a press holds its button until the request it started has settled', () => {
    function Saver({ send, valid = () => true, onRefused }: {
        send: () => Promise<void>; valid?: () => boolean; onRefused?: () => void;
    }) {
        const save = useMutation({ mutationFn: send });
        return (
            <Button pending={save.isPending} onClick={() => {
                if (!valid()) { onRefused?.(); return; }
                save.mutate();
            }}>
                Save
            </Button>
        );
    }

    it('sends one request for two presses before the pending state renders, and acts again once it settles', async () => {
        const { send, answer } = settledByTest();
        withClient(<Saver send={send} />);
        const save = screen.getByRole('button', { name: 'Save' });

        fireEvent.click(save);
        fireEvent.click(save);
        await ticks();
        // The pending state has not rendered: the press's hold refused the second.
        expect(save).not.toHaveAttribute('aria-busy');
        expect(send).toHaveBeenCalledTimes(1);

        await settle();
        expect(save).toHaveAttribute('aria-busy', 'true');
        answer();
        await waitFor(() => expect(save).not.toHaveAttribute('aria-busy'));
        fireEvent.click(save);
        await ticks();
        expect(send).toHaveBeenCalledTimes(2);
    });

    it('sends one request for Space pressed twice, and for Enter pressed twice, before the pending state renders', async () => {
        const { send, answer } = settledByTest();
        const user = userEvent.setup({ delay: null });
        withClient(<Saver send={send} />);
        const save = screen.getByRole('button', { name: 'Save' });
        save.focus();

        await user.keyboard('  ');
        await ticks();
        expect(send).toHaveBeenCalledTimes(1);
        answer();
        await waitFor(() => expect(save).not.toHaveAttribute('aria-busy'));
        await settle();

        await user.keyboard('{Enter}{Enter}');
        await ticks();
        expect(send).toHaveBeenCalledTimes(2);
        expect(save).toHaveFocus();
    });

    it('holds nothing for a press that starts no request, so the next press acts', async () => {
        const { send } = settledByTest();
        const onRefused = vi.fn();
        let ok = false;
        withClient(<Saver send={send} valid={() => ok} onRefused={onRefused} />);
        const save = screen.getByRole('button', { name: 'Save' });

        fireEvent.click(save);
        fireEvent.click(save);
        expect(onRefused).toHaveBeenCalledTimes(2);
        ok = true;
        fireEvent.click(save);
        await ticks();
        expect(send).toHaveBeenCalledTimes(1);
    });

    it('lets go when the request is refused, so a retry acts', async () => {
        const { send, refuse } = settledByTest();
        withClient(<Saver send={send} />);
        const save = screen.getByRole('button', { name: 'Save' });

        fireEvent.click(save);
        await ticks();
        refuse();
        await settle();
        await waitFor(() => expect(save).not.toHaveAttribute('aria-busy'));
        fireEvent.click(save);
        fireEvent.click(save);
        await ticks();
        expect(send).toHaveBeenCalledTimes(2);
    });

    it('holds a submit button for the request its form starts, for a double press and for Enter twice in a field', async () => {
        const { send, answer } = settledByTest();
        function Form() {
            const save = useMutation({ mutationFn: send });
            return (
                <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
                    <input aria-label="Name" />
                    <Button type="submit" pending={save.isPending}>Save</Button>
                </form>
            );
        }
        const user = userEvent.setup({ delay: null });
        withClient(<Form />);

        const save = screen.getByRole('button', { name: 'Save' });
        fireEvent.click(save);
        fireEvent.click(save);
        await ticks();
        expect(send).toHaveBeenCalledTimes(1);
        answer();
        await waitFor(() => expect(save).not.toHaveAttribute('aria-busy'));
        await settle();

        await user.type(screen.getByRole('textbox', { name: 'Name' }), '{Enter}{Enter}');
        await ticks();
        expect(send).toHaveBeenCalledTimes(2);
    });

    it('holds a button for the promise its handler returns', async () => {
        let finish!: () => void;
        const onClick = vi.fn(() => new Promise<void>((resolve) => { finish = resolve; }));
        withClient(<Button pending={false} onClick={onClick}>Go</Button>);
        const go = screen.getByRole('button', { name: 'Go' });

        fireEvent.click(go);
        fireEvent.click(go);
        expect(onClick).toHaveBeenCalledTimes(1);
        await act(async () => { finish(); });
        fireEvent.click(go);
        expect(onClick).toHaveBeenCalledTimes(2);
    });

    it('holds a button for a fetch its press started', async () => {
        const { send, answer } = settledByTest();
        function Loader() {
            // A page already shown: a refetch over data cancels the fetch under way and fetches again.
            const query = useQuery({
                queryKey: ['more'], queryFn: async () => { await send(); return 1; }, enabled: false, initialData: 0,
            });
            return <Button pending={query.isFetching} onClick={() => void query.refetch()}>Load more</Button>;
        }
        withClient(<Loader />);
        const more = screen.getByRole('button', { name: 'Load more' });

        // The second press comes once the fetch is under way: a refetch then
        // would cancel it and fetch again.
        fireEvent.click(more);
        await ticks();
        fireEvent.click(more);
        await ticks();
        expect(more).not.toHaveAttribute('aria-busy');
        expect(send).toHaveBeenCalledTimes(1);
        answer();
        await waitFor(() => expect(more).not.toHaveAttribute('aria-busy'));
        fireEvent.click(more);
        await ticks();
        expect(send).toHaveBeenCalledTimes(2);
    });

    it('holds a raw button on the helper, and IconButton, as it holds Button, and a press holds its group', async () => {
        const { send } = settledByTest();
        function Both() {
            const save = useMutation({ mutationFn: send });
            return (
                <PendingScope>
                    <RawButton pending={save.isPending} onClick={() => save.mutate()} />
                    <IconButton aria-label="Sync" pending={save.isPending} onClick={() => save.mutate()}>↻</IconButton>
                </PendingScope>
            );
        }
        withClient(<Both />);
        fireEvent.click(screen.getByRole('button', { name: 'Raw' }));
        fireEvent.click(screen.getByRole('button', { name: 'Raw' }));
        // Sync waits on the same request, in the same group: Raw's press holds
        // it too, before the pending state has rendered.
        fireEvent.click(screen.getByRole('button', { name: 'Sync' }));
        await ticks();
        expect(send).toHaveBeenCalledTimes(1);
    });

    it('holds only its own button outside a group: two buttons each waiting on their own request each send', async () => {
        const { send } = settledByTest();
        function Row({ name }: { name: string }) {
            const save = useMutation({ mutationFn: send });
            return <Button pending={save.isPending} onClick={() => save.mutate()}>{name}</Button>;
        }
        withClient(<><Row name="First" /><Row name="Second" /></>);
        fireEvent.click(screen.getByRole('button', { name: 'First' }));
        fireEvent.click(screen.getByRole('button', { name: 'Second' }));
        fireEvent.click(screen.getByRole('button', { name: 'Second' }));
        await ticks();
        expect(send).toHaveBeenCalledTimes(2);
    });

    it('lets a group act again once the press\'s request has settled', async () => {
        const { send, answer } = settledByTest();
        function Both() {
            const save = useMutation({ mutationFn: send });
            return (
                <PendingScope>
                    <Button pending={save.isPending} onClick={() => save.mutate()}>One</Button>
                    <Button pending={save.isPending} onClick={() => save.mutate()}>Two</Button>
                </PendingScope>
            );
        }
        withClient(<Both />);
        fireEvent.click(screen.getByRole('button', { name: 'One' }));
        fireEvent.click(screen.getByRole('button', { name: 'Two' }));
        await ticks();
        expect(send).toHaveBeenCalledTimes(1);
        answer();
        await waitFor(() => expect(screen.getByRole('button', { name: 'Two' })).not.toHaveAttribute('aria-busy'));
        fireEvent.click(screen.getByRole('button', { name: 'Two' }));
        await ticks();
        expect(send).toHaveBeenCalledTimes(2);
    });
});

// A pending button never changes size (ADR-0023 §L): its two labels are laid
// out from the first render in one grid cell, so its box is the wider of the
// two at rest and while pending, and neither it nor a neighbour moves when the
// label swaps. jsdom lays nothing out, so the structure that holds the box is
// what is asserted: both labels in the cell from the first render, each in the
// cell's one grid area, the one not shown invisible and hidden from assistive
// technology, and the accessible name the label shown.
describe('a pending label', () => {
    /** The cell a pending label lays its labels in (the button's content), and the two labels in it. */
    function cellOf(button: HTMLElement) {
        const cell = button.firstElementChild as HTMLElement;
        const [resting, busy] = Array.from(cell.children) as HTMLElement[];
        return { cell, resting: resting!, busy: busy! };
    }

    function expectOneCell(button: HTMLElement, labels: { resting: string; busy: string }, pending: boolean) {
        const { cell, resting, busy } = cellOf(button);
        expect(cell).toHaveClass('inline-grid');
        expect(cell.children).toHaveLength(2);
        for (const label of [resting, busy]) expect(label).toHaveClass('col-start-1', 'row-start-1');
        expect(resting).toHaveTextContent(labels.resting);
        expect(busy).toHaveTextContent(labels.busy);
        const [shown, hidden] = pending ? [busy, resting] : [resting, busy];
        expect(hidden).toHaveClass('invisible');
        expect(hidden).toHaveAttribute('aria-hidden', 'true');
        expect(shown).not.toHaveClass('invisible');
        expect(shown).not.toHaveAttribute('aria-hidden');
        expect(button).toHaveAccessibleName(pending ? labels.busy : labels.resting);
    }

    it('lays a Button\'s two labels in one cell from the first render, the one not shown hidden', () => {
        const labels = { resting: 'Delete', busy: 'Working…' };
        const { rerender } = render(<Button pending={false} pendingLabel="Working…">Delete</Button>);
        const button = screen.getByRole('button', { name: 'Delete' });
        const { cell } = cellOf(button);
        expectOneCell(button, labels, false);

        rerender(<Button pending pendingLabel="Working…">Delete</Button>);
        expectOneCell(button, labels, true);
        // The same cell: nothing is laid out anew at the swap.
        expect(cellOf(button).cell).toBe(cell);

        rerender(<Button pending={false} pendingLabel="Working…">Delete</Button>);
        expectOneCell(button, labels, false);
    });

    it('lays a raw button\'s PendingLabel out the same way', () => {
        const labels = { resting: 'Load more', busy: 'Loading…' };
        const { rerender } = render(
            <button type="button"><PendingLabel pending={false} label="Load more" pendingLabel="Loading…" /></button>,
        );
        const more = screen.getByRole('button', { name: 'Load more' });
        expectOneCell(more, labels, false);

        rerender(<button type="button"><PendingLabel pending label="Load more" pendingLabel="Loading…" /></button>);
        expectOneCell(more, labels, true);
    });

    it('lays ConfirmDialog\'s confirm out with both labels in one cell', () => {
        const labels = { resting: 'Remove', busy: 'Working…' };
        const confirm = (isConfirming: boolean) => (
            <ConfirmDialog open title="Remove it?" confirmLabel="Remove" variant="danger"
                isConfirming={isConfirming} onConfirm={() => {}} onCancel={() => {}} />
        );
        const { rerender } = render(confirm(false));
        const remove = screen.getByRole('button', { name: 'Remove' });
        expectOneCell(remove, labels, false);

        rerender(confirm(true));
        expectOneCell(remove, labels, true);
    });

    it('reads the resting label on a Button pending with its group until its own request is in flight', () => {
        const labels = { resting: 'Save', busy: 'Saving…' };
        const { rerender } = render(
            <Button pending><PendingLabel pending={false} label="Save" pendingLabel="Saving…" /></Button>,
        );
        const save = screen.getByRole('button', { name: 'Save' });
        expect(save).toHaveAttribute('aria-busy', 'true');
        expectOneCell(save, labels, false);

        rerender(<Button pending><PendingLabel pending label="Save" pendingLabel="Saving…" /></Button>);
        expectOneCell(save, labels, true);
    });

    it('lays every stage of a pending label in stages out from the first render', () => {
        const add = (pending: boolean, started: boolean) => (
            <Button pending={pending} pendingLabel={(
                <PendingLabel pending={started} label="Starting…" pendingLabel="Tap your authenticator…" />
            )}>Add</Button>
        );
        const { rerender } = render(add(false, false));
        const button = screen.getByRole('button', { name: 'Add' });
        const stages = cellOf(cellOf(button).busy);
        expect(stages.cell).toHaveClass('inline-grid');
        expect(stages.resting).toHaveTextContent('Starting…');
        expect(stages.busy).toHaveTextContent('Tap your authenticator…');

        rerender(add(true, false));
        expect(button).toHaveAccessibleName('Starting…');
        rerender(add(true, true));
        expect(button).toHaveAccessibleName('Tap your authenticator…');
        expect(cellOf(cellOf(button).busy).cell).toBe(stages.cell);
    });
});
