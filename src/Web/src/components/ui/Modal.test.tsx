import { StrictMode, useEffect, useRef, useState, type ReactElement } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { Modal } from './Modal';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import type { AccountSummary } from '@/lib/types';

// The dialog shell's keyboard and focus contract (ADR-0023 §L): Escape belongs
// to the dialog it was pressed in, a control that used an Escape keeps the
// dialog open, and a host re-render never moves focus.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

const category = (id: string, name: string): AccountSummary => ({
    id,
    ledgerId: LEDGER_ID,
    parentId: null,
    name,
    accountType: 'category',
    categoryKind: 'expense',
    currencyCode: 'USD',
    isActive: true,
    isSystem: false,
    feedConnectionId: null,
    needsReviewCount: 0,
    holdingsAccountId: null,
    isTradeCommission: false,
});

const CATEGORIES = [
    category('00000000-0000-0000-0000-000000000301', 'Category One'),
    category('00000000-0000-0000-0000-000000000302', 'Category Two'),
];

describe('Modal: Escape', () => {
    it('closes only the inner dialog when Escape is pressed in a dialog opened from another', async () => {
        const closeOuter = vi.fn();
        const closeInner = vi.fn();
        render(
            <Modal open onClose={closeOuter} ariaLabel="Outer dialog">
                <button type="button">Outer field</button>
                <Modal open onClose={closeInner} ariaLabel="Inner dialog">
                    <input aria-label="Inner field" />
                </Modal>
            </Modal>,
        );

        const innerField = screen.getByLabelText('Inner field');
        await userEvent.click(innerField);
        await userEvent.keyboard('{Escape}');

        expect(closeInner).toHaveBeenCalledTimes(1);
        expect(closeOuter).not.toHaveBeenCalled();
    });

    it('keeps an Escape pressed in a dialog away from handlers outside it', async () => {
        // A page shortcut (clear the selection, leave edit mode) listening
        // around the dialog must not act on a key that closed the dialog.
        const onClose = vi.fn();
        const pageHandler = vi.fn();
        const documentHandler = vi.fn();
        document.addEventListener('keydown', documentHandler);
        render(
            <div onKeyDown={(e) => { if (e.key === 'Escape') pageHandler(); }}>
                <Modal open onClose={onClose} ariaLabel="Page dialog">
                    <input aria-label="Dialog field" />
                </Modal>
            </div>,
        );

        await userEvent.click(screen.getByLabelText('Dialog field'));
        await userEvent.keyboard('{Escape}');
        document.removeEventListener('keydown', documentHandler);

        expect(onClose).toHaveBeenCalledTimes(1);
        expect(pageHandler).not.toHaveBeenCalled();
        expect(documentHandler).not.toHaveBeenCalled();
    });

    it('keeps the dialog open when Escape closes a picker inside it, and closes on the next one', async () => {
        const onClose = vi.fn();
        render(
            <Modal open onClose={onClose} ariaLabel="Picker dialog">
                <AccountCategoryPicker
                    accounts={CATEGORIES}
                    isEligible={() => true}
                    valueId={null}
                    onChangeId={() => {}}
                    label="Category"
                    ariaLabel="Category"
                />
            </Modal>,
        );

        const picker = screen.getByRole('combobox', { name: 'Category' });
        await userEvent.click(picker);
        await waitFor(() => expect(picker).toHaveAttribute('aria-expanded', 'true'));

        await userEvent.keyboard('{Escape}');
        await waitFor(() => expect(picker).toHaveAttribute('aria-expanded', 'false'));
        expect(onClose).not.toHaveBeenCalled();

        await userEvent.keyboard('{Escape}');
        expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('closes the newest dialog alone when Escape arrives with focus outside every dialog', async () => {
        const closeFirst = vi.fn();
        const closeSecond = vi.fn();
        render(
            <>
                <Modal open onClose={closeFirst} ariaLabel="First dialog">
                    <button type="button">First</button>
                </Modal>
                <Modal open onClose={closeSecond} ariaLabel="Second dialog">
                    <button type="button">Second</button>
                </Modal>
            </>,
        );
        expect(screen.getByRole('dialog', { name: 'Second dialog' })).toBeInTheDocument();

        // The focused control went away: focus is on the page, in no dialog.
        (document.activeElement as HTMLElement | null)?.blur();
        expect(document.activeElement).toBe(document.body);
        await userEvent.keyboard('{Escape}');

        expect(closeSecond).toHaveBeenCalledTimes(1);
        expect(closeFirst).not.toHaveBeenCalled();
    });
});

describe('Modal: focus', () => {
    function Host() {
        const [renders, setRenders] = useState(0);
        return (
            <Modal
                open
                // A new function on every render, as an inline host passes it.
                onClose={() => setRenders((n) => n + 1)}
                ariaLabel="Editing dialog"
            >
                <button type="button">First control</button>
                <input aria-label="Second field" />
                <button type="button" onClick={() => setRenders((n) => n + 1)}>
                    Re-render {renders}
                </button>
            </Modal>
        );
    }

    it('keeps focus where the user put it when the host re-renders with a new onClose', async () => {
        render(<Host />);
        // Opened: focus starts on the first control.
        expect(document.activeElement).toBe(screen.getByRole('button', { name: 'First control' }));

        const field = screen.getByLabelText('Second field');
        field.focus();
        expect(document.activeElement).toBe(field);

        // Re-render the host without touching focus: the click handler runs
        // through a programmatic click so focus stays on the field.
        screen.getByRole('button', { name: 'Re-render 0' }).click();
        await screen.findByRole('button', { name: 'Re-render 1' });

        expect(document.activeElement).toBe(field);
    });
});

// Where focus starts (ADR-0023 §L): the content's own focus wins. A dialog
// whose field focuses itself, by autoFocus or in an effect of the content,
// opens on that field; otherwise on the host's initialFocusRef, else on the
// first control. The trigger it gives focus back to is the same either way.
describe('Modal: the first field', () => {
    function FocusesItselfInAnEffect() {
        const ref = useRef<HTMLInputElement | null>(null);
        useEffect(() => { ref.current?.focus(); }, []);
        return <input ref={ref} aria-label="Name" />;
    }

    const CONTENT = [
        { content: 'a field with autoFocus', Name: () => <input aria-label="Name" autoFocus /> },
        { content: 'a field its own effect focuses', Name: FocusesItselfInAnEffect },
    ];
    const REF = [
        { ref: 'no initialFocusRef', withRef: false },
        { ref: 'an initialFocusRef on another field', withRef: true },
    ];
    const CASES = CONTENT.flatMap((content) => REF.map((ref) => ({ ...content, ...ref })));

    function Host({ Name, withRef }: { Name?: () => ReactElement; withRef: boolean }) {
        const [open, setOpen] = useState(false);
        const initial = useRef<HTMLInputElement | null>(null);
        return (
            <>
                <button type="button" onClick={() => setOpen(true)}>Open</button>
                <Modal
                    open={open}
                    onClose={() => setOpen(false)}
                    ariaLabel="Dialog"
                    initialFocusRef={withRef ? initial : undefined}
                >
                    <button type="button" onClick={() => setOpen(false)}>Close</button>
                    <input ref={initial} aria-label="Ticker" />
                    {Name !== undefined ? <Name /> : null}
                </Modal>
            </>
        );
    }

    it.each(CASES)('opens on the field the content focused: $content, $ref', async ({ Name, withRef }) => {
        const user = userEvent.setup();
        render(<Host Name={Name} withRef={withRef} />);
        const opener = screen.getByRole('button', { name: 'Open' });

        await user.click(opener);

        expect(screen.getByLabelText('Name')).toHaveFocus();

        await user.keyboard('{Escape}');
        expect(screen.queryByRole('dialog')).toBeNull();
        expect(opener).toHaveFocus();
    });

    it('opens on the initialFocusRef when the content takes no focus', async () => {
        const user = userEvent.setup();
        render(<Host withRef />);

        await user.click(screen.getByRole('button', { name: 'Open' }));

        expect(screen.getByLabelText('Ticker')).toHaveFocus();
    });

    it('opens on the first control when the content takes no focus and the host names no field', async () => {
        const user = userEvent.setup();
        render(<Host withRef={false} />);

        await user.click(screen.getByRole('button', { name: 'Open' }));

        expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus();
    });

    // The app renders under StrictMode, which runs the open effect, its
    // cleanup and the effect again on a fresh mount; an autoFocus field is not
    // focused a second time. The dialog opens on the same field all the same.
    // Mounted with the open, as a host that renders its dialog only while open
    // does.
    function MountsOnOpen({ Name, withRef }: { Name?: () => ReactElement; withRef: boolean }) {
        const [open, setOpen] = useState(false);
        const initial = useRef<HTMLInputElement | null>(null);
        return (
            <>
                <button type="button" onClick={() => setOpen(true)}>Open</button>
                {open ? (
                    <Modal
                        open
                        onClose={() => setOpen(false)}
                        ariaLabel="Dialog"
                        initialFocusRef={withRef ? initial : undefined}
                    >
                        <button type="button" onClick={() => setOpen(false)}>Close</button>
                        <input ref={initial} aria-label="Ticker" />
                        {Name !== undefined ? <Name /> : null}
                    </Modal>
                ) : null}
            </>
        );
    }

    it.each(CASES)('opens on the field the content focused under StrictMode: $content, $ref', async ({ Name, withRef }) => {
        const user = userEvent.setup();
        render(<StrictMode><MountsOnOpen Name={Name} withRef={withRef} /></StrictMode>);
        const opener = screen.getByRole('button', { name: 'Open' });

        await user.click(opener);

        expect(screen.getByLabelText('Name')).toHaveFocus();

        await user.keyboard('{Escape}');
        expect(screen.queryByRole('dialog')).toBeNull();
        expect(opener).toHaveFocus();
    });

    it('opens on the initialFocusRef under StrictMode when the content takes no focus', async () => {
        const user = userEvent.setup();
        render(<StrictMode><MountsOnOpen withRef /></StrictMode>);

        await user.click(screen.getByRole('button', { name: 'Open' }));

        expect(screen.getByLabelText('Ticker')).toHaveFocus();
    });

    it('opens on the first control under StrictMode when the content takes no focus and the host names no field', async () => {
        const user = userEvent.setup();
        render(<StrictMode><MountsOnOpen withRef={false} /></StrictMode>);

        await user.click(screen.getByRole('button', { name: 'Open' }));

        expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus();
    });
});

// Focus goes back to the trigger on close (ADR-0023 §L). The dialog's content
// can take focus before the dialog has recorded what opened it: a field with
// autoFocus is focused while React applies the dialog, and an effect of the
// content's own runs before the dialog's. Neither may become the trigger, or
// closing gives focus back to a field that has gone and it falls to the page.
describe('Modal: focus back to the trigger', () => {
    function FocusesItselfInAnEffect() {
        const ref = useRef<HTMLInputElement | null>(null);
        useEffect(() => { ref.current?.focus(); }, []);
        return <input ref={ref} aria-label="Name" />;
    }

    const CONTENT: { content: string; Name: () => ReactElement }[] = [
        { content: 'a field with autoFocus', Name: () => <input aria-label="Name" autoFocus /> },
        { content: 'a field its own effect focuses', Name: FocusesItselfInAnEffect },
    ];
    const MOUNTING = [
        { mounting: 'rendered only while open', keepMounted: false },
        { mounting: 'kept mounted with open toggled', keepMounted: true },
    ];
    const CASES = CONTENT.flatMap((content) =>
        MOUNTING.map((mounting) => ({ ...content, ...mounting })));

    /** A dialog opened from a field of the dialog behind it, as Add security
     *  opens from the security field of the investment editor's Review. */
    function Host({ Name, keepMounted }: { Name: () => ReactElement; keepMounted: boolean }) {
        const [open, setOpen] = useState(false);
        const close = () => setOpen(false);
        const content = (
            <>
                <input aria-label="Ticker" />
                <Name />
            </>
        );
        return (
            <Modal open onClose={() => {}} ariaLabel="Review dialog">
                <input
                    aria-label="Security"
                    onKeyDown={(e) => { if (e.key === 'Enter') setOpen(true); }}
                />
                {keepMounted ? (
                    <Modal open={open} onClose={close} ariaLabel="Add dialog">{content}</Modal>
                ) : open ? (
                    <Modal open onClose={close} ariaLabel="Add dialog">{content}</Modal>
                ) : null}
            </Modal>
        );
    }

    it.each(CASES)('gives focus back to the field it was opened from: $content, dialog $mounting', async ({ Name, keepMounted }) => {
        const user = userEvent.setup();
        render(<Host Name={Name} keepMounted={keepMounted} />);
        const security = screen.getByLabelText('Security');
        await user.click(security);

        await user.keyboard('{Enter}');
        const added = screen.getByRole('dialog', { name: 'Add dialog' });
        expect(added.contains(document.activeElement)).toBe(true);

        await user.keyboard('{Escape}');

        expect(screen.queryByRole('dialog', { name: 'Add dialog' })).toBeNull();
        expect(screen.getByRole('dialog', { name: 'Review dialog' })).toBeInTheDocument();
        expect(security).toHaveFocus();
    });
});

// A host may name where focus goes when the trigger cannot take it back: the
// trigger is disabled by the time the dialog closes, or gone from the page.
// While the trigger can take it, it does.
describe('Modal: a fallback for a trigger that cannot take focus back', () => {
    function Host({ triggerDisabledOnClose }: { triggerDisabledOnClose: boolean }) {
        const [open, setOpen] = useState(false);
        const [disabled, setDisabled] = useState(false);
        const fallback = useRef<HTMLInputElement>(null);
        return (
            <>
                <input ref={fallback} aria-label="Fallback field" />
                <button type="button" disabled={disabled} onClick={() => setOpen(true)}>Open</button>
                <Modal open={open} onClose={() => setOpen(false)} fallbackFocusRef={fallback} ariaLabel="Dialog">
                    <button type="button" onClick={() => { setDisabled(triggerDisabledOnClose); setOpen(false); }}>
                        Done
                    </button>
                </Modal>
            </>
        );
    }

    it('gives focus to the fallback when the trigger is disabled by then', async () => {
        const user = userEvent.setup();
        render(<Host triggerDisabledOnClose />);
        await user.click(screen.getByRole('button', { name: 'Open' }));
        await user.click(screen.getByRole('button', { name: 'Done' }));

        expect(screen.queryByRole('dialog')).toBeNull();
        expect(screen.getByRole('button', { name: 'Open' })).toBeDisabled();
        expect(screen.getByLabelText('Fallback field')).toHaveFocus();
    });

    it('gives focus back to the trigger while it can take it', async () => {
        const user = userEvent.setup();
        render(<Host triggerDisabledOnClose={false} />);
        await user.click(screen.getByRole('button', { name: 'Open' }));
        await user.click(screen.getByRole('button', { name: 'Done' }));

        expect(screen.queryByRole('dialog')).toBeNull();
        expect(screen.getByRole('button', { name: 'Open' })).toHaveFocus();
    });
});

// A dialog that opens as another closes, in one update: Delete's "Merge
// instead…", an edit's "Merge … instead…". React takes the first dialog off the
// page while it puts the next one on, so focus is on the page at that moment,
// or still on a control of the first dialog when the next one comes earlier in
// the page. The first dialog gives focus back to its own trigger after both are
// applied, and that trigger is the one the next dialog gives focus back to.
describe('Modal: a dialog that opens as another closes', () => {
    const MOUNTING = [
        { mounting: 'rendered only while open', keepMounted: false },
        { mounting: 'kept mounted with open toggled', keepMounted: true },
    ];
    const ORDER = [
        { order: 'after the first', nextFirst: false },
        { order: 'before the first', nextFirst: true },
    ];
    const CASES = MOUNTING.flatMap((mounting) => ORDER.map((order) => ({ ...mounting, ...order })));

    function Host({ keepMounted, nextFirst, nextField = false }: {
        keepMounted: boolean;
        nextFirst: boolean;
        /** The next dialog's content puts focus on a field of its own, after its first control. */
        nextField?: boolean;
    }) {
        const [step, setStep] = useState<'none' | 'first' | 'next'>('none');
        const close = () => setStep('none');
        const dialog = (which: 'first' | 'next') => {
            const label = which === 'first' ? 'First dialog' : 'Next dialog';
            const content = which === 'first'
                ? <button type="button" onClick={() => setStep('next')}>Hand over</button>
                : (
                    <>
                        <button type="button" onClick={close}>Done</button>
                        {nextField ? <input aria-label="Name" autoFocus /> : null}
                    </>
                );
            if (keepMounted) {
                return <Modal open={step === which} onClose={close} ariaLabel={label}>{content}</Modal>;
            }
            return step === which ? <Modal open onClose={close} ariaLabel={label}>{content}</Modal> : null;
        };
        return (
            <>
                <button type="button" onClick={() => setStep('first')}>Open first</button>
                {nextFirst ? dialog('next') : dialog('first')}
                {nextFirst ? dialog('first') : dialog('next')}
            </>
        );
    }

    it.each(CASES)('gives focus back to the first dialog’s trigger: dialogs $mounting, the next one $order', async ({ keepMounted, nextFirst }) => {
        const user = userEvent.setup();
        render(<Host keepMounted={keepMounted} nextFirst={nextFirst} />);
        const opener = screen.getByRole('button', { name: 'Open first' });
        await user.click(opener);
        expect(screen.getByRole('button', { name: 'Hand over' })).toHaveFocus();

        await user.keyboard('{Enter}');
        expect(screen.queryByRole('dialog', { name: 'First dialog' })).toBeNull();
        expect(screen.getByRole('button', { name: 'Done' })).toHaveFocus();

        await user.keyboard('{Escape}');

        expect(screen.queryByRole('dialog', { name: 'Next dialog' })).toBeNull();
        expect(opener).toHaveFocus();
    });

    // A dialog opens where its content puts focus, also as another closes: the
    // closing dialog's return of focus to its own trigger comes after the next
    // dialog's field took focus, and must not cost the field its focus.
    it.each(CASES)('opens the next dialog on the field its content focuses: dialogs $mounting, the next one $order', async ({ keepMounted, nextFirst }) => {
        const user = userEvent.setup();
        render(<Host keepMounted={keepMounted} nextFirst={nextFirst} nextField />);
        const opener = screen.getByRole('button', { name: 'Open first' });
        await user.click(opener);

        await user.click(screen.getByRole('button', { name: 'Hand over' }));
        expect(screen.queryByRole('dialog', { name: 'First dialog' })).toBeNull();
        expect(screen.getByRole('textbox', { name: 'Name' })).toHaveFocus();

        await user.click(screen.getByRole('button', { name: 'Done' }));
        expect(screen.queryByRole('dialog', { name: 'Next dialog' })).toBeNull();
        expect(opener).toHaveFocus();
    });
});

// jsdom lays nothing out, so these pin the classes that decide it: the overlay
// is the scroll container and the panel never clips. The overlap itself (a
// picker's list over the panel's edge, a tall dialog scrolling) is a browser
// check.
describe('Modal: scrolling', () => {
    it('scrolls the overlay, keeps a short panel centred, and never clips the panel', () => {
        const onClose = vi.fn();
        render(
            <Modal open onClose={onClose} ariaLabel="Tall dialog" className="max-w-5xl">
                <button type="button">Inside</button>
            </Modal>,
        );

        const panel = screen.getByRole('dialog', { name: 'Tall dialog' });
        const wrapper = panel.parentElement!;
        const overlay = wrapper.parentElement!;
        expect(overlay).toHaveAttribute('role', 'presentation');
        expect(overlay).toHaveClass('fixed', 'inset-0', 'overflow-y-auto');
        expect(wrapper).toHaveClass('flex', 'min-h-full', 'items-center', 'justify-center');
        expect(panel).toHaveClass('w-full', 'max-w-5xl');
        const clipping = Array.from(panel.classList).filter((c) => /^(overflow|max-h)-/.test(c));
        expect(clipping).toEqual([]);

        // A click beside the panel is still a backdrop click; one on it is not.
        fireEvent.click(panel);
        expect(onClose).not.toHaveBeenCalled();
        fireEvent.click(wrapper);
        expect(onClose).toHaveBeenCalledTimes(1);
    });
});
