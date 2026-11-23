import { afterEach, describe, expect, it, onTestFinished, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';

import * as apiModule from '@/lib/api';
import { useSelection } from '@/lib/useSelection';
import { TagCombobox } from '@/components/tags/TagCombobox';
import { ContextMenu } from '@/components/ui/ContextMenu';
import { Modal } from '@/components/ui/Modal';
import type { IndexBucketDto } from '@/lib/types/register';
import type { TagDto } from '@/lib/types';
import { RegisterDateJumpPopover } from './RegisterDateJumpPopover';
import { RegisterScrollSurface } from './shell/RegisterScrollSurface';
import { useRegisterFocusHome } from './shell/useRegisterFocusHome';

// The Ctrl/Cmd+J popover closes on Escape as a list does (ADR-0023 §F, §L):
// its own Escape is marked handled and stopped, so nothing further out acts on
// it, the register's selection above all; and an Escape a list nearer the
// focus already used is not the popover's to act on.

const BUCKETS: IndexBucketDto[] = [
    { yearMonth: '2026-01', count: 1, sampleHeaderId: 'header-1' },
    { yearMonth: '2026-02', count: 1, sampleHeaderId: 'header-2' },
];

const TAGS: TagDto[] = [
    { id: 't1', name: 'work', color: null, usageCount: 1, transactionCount: 1 },
];

afterEach(() => {
    vi.restoreAllMocks();
});

/** A register in miniature: a selection that Escape clears, and the popover. */
function Register({ onParentEscape }: { onParentEscape: (defaultPrevented: boolean) => void }) {
    const selection = useSelection({ ledgerId: 'ledger-1', accountId: 'account-1', statusFilter: 'all' });
    const selected = selection.selection.kind === 'explicit' ? selection.selection.headerIds.length : -1;
    return (
        <div onKeyDown={(e) => { if (e.key === 'Escape') onParentEscape(e.defaultPrevented); }}>
            <button type="button" onClick={() => selection.setExplicit(['header-1'])}>Select a row</button>
            <output aria-label="Selected rows">{selected}</output>
            <input
                aria-label="Marks only"
                onKeyDown={(e) => { if (e.key === 'Escape') e.preventDefault(); }}
            />
            <TagCombobox tags={TAGS} onCommit={vi.fn()} aria-label="Filter by tag" />
            <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
        </div>
    );
}

function renderRegister() {
    vi.spyOn(apiModule, 'fetchSelectionSummary')
        .mockResolvedValue({ count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD' });
    const parentEscapes: boolean[] = [];
    // The native event, taken by a capture listener before anything handles
    // it and read once it is done; and the Escapes that went on past the React
    // tree to the window.
    const escapes: KeyboardEvent[] = [];
    const windowEscapes: KeyboardEvent[] = [];
    const capture = (e: KeyboardEvent) => { if (e.key === 'Escape') escapes.push(e); };
    const bubble = (e: KeyboardEvent) => { if (e.key === 'Escape') windowEscapes.push(e); };
    window.addEventListener('keydown', capture, true);
    window.addEventListener('keydown', bubble);
    onTestFinished(() => {
        window.removeEventListener('keydown', capture, true);
        window.removeEventListener('keydown', bubble);
    });
    render(<Register onParentEscape={(marked) => parentEscapes.push(marked)} />);
    return { parentEscapes, windowEscapes, marks: () => escapes.map((e) => e.defaultPrevented) };
}

const popover = () => screen.queryByRole('dialog', { name: 'Jump to date' });
const selectedRows = () => screen.getByRole('status', { name: 'Selected rows' }).textContent;

async function selectARowAndOpen(user: ReturnType<typeof userEvent.setup>) {
    await user.click(screen.getByRole('button', { name: 'Select a row' }));
    expect(selectedRows()).toBe('1');
    // The shortcut is the page's, not a field's: from the page itself.
    act(() => (document.activeElement as HTMLElement | null)?.blur());
    await user.keyboard('{Control>}j{/Control}');
    expect(popover()).toBeInTheDocument();
}

describe('RegisterDateJumpPopover Escape', () => {
    it('closes on Escape in its field, marked and stopped', async () => {
        const user = userEvent.setup();
        const { parentEscapes, windowEscapes, marks } = renderRegister();
        await selectARowAndOpen(user);
        expect(popover()!.querySelector('input[type="date"]')).toHaveFocus();

        await user.keyboard('{Escape}');

        expect(popover()).toBeNull();
        expect(marks()).toEqual([true]);
        expect(parentEscapes).toEqual([]);
        expect(windowEscapes).toEqual([]);
    });

    it('never also clears the register\'s selection when it closes from its own button', async () => {
        // A button is not a field, so the selection's Escape would act on it.
        const user = userEvent.setup();
        renderRegister();
        await selectARowAndOpen(user);
        act(() => screen.getByRole('button', { name: 'Cancel' }).focus());

        await user.keyboard('{Escape}');

        expect(popover()).toBeNull();
        expect(selectedRows()).toBe('1');

        // The next Escape, with the popover gone, is the selection's.
        await user.keyboard('{Escape}');
        expect(selectedRows()).toBe('0');
    });

    it('closes on an Escape pressed with focus elsewhere on the page, and leaves the selection', async () => {
        // Tab leaves the popover; the Escape pressed on the page still closes
        // it, and the selection's Escape, heard later, does not also act.
        const user = userEvent.setup();
        renderRegister();
        await selectARowAndOpen(user);
        act(() => screen.getByRole('button', { name: 'Select a row' }).focus());

        await user.keyboard('{Escape}');

        expect(popover()).toBeNull();
        expect(selectedRows()).toBe('1');

        await user.keyboard('{Escape}');
        expect(selectedRows()).toBe('0');
    });

    it('stays open on an Escape a control nearer the focus already marked handled', async () => {
        const user = userEvent.setup();
        renderRegister();
        await selectARowAndOpen(user);
        act(() => screen.getByRole('textbox', { name: 'Marks only' }).focus());

        await user.keyboard('{Escape}');

        expect(popover()).toBeInTheDocument();
    });

    it('stays open on the Escape that closes a list\'s suggestions, and closes on the next', async () => {
        const user = userEvent.setup();
        renderRegister();
        await selectARowAndOpen(user);
        const tag = screen.getByRole('combobox', { name: 'Filter by tag' });
        act(() => tag.focus());
        await user.keyboard('wo');
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        await user.keyboard('{Escape}');

        expect(screen.queryByRole('listbox')).toBeNull();
        expect(tag).toHaveFocus();
        expect(popover()).toBeInTheDocument();

        await user.keyboard('{Escape}');
        expect(popover()).toBeNull();
        expect(selectedRows()).toBe('1');
    });
});

/**
 * A register with an inline editor open: a selection Escape clears, and an
 * editor around a Save button that cancels on an Escape nothing nearer used
 * and marks it, as the bank and investment editors do.
 */
function EditingRegister({ onCancel }: { onCancel: () => void }) {
    const selection = useSelection({ ledgerId: 'ledger-1', accountId: 'account-1', statusFilter: 'all' });
    const selected = selection.selection.kind === 'explicit' ? selection.selection.headerIds.length : -1;
    return (
        <div>
            <button type="button" onClick={() => selection.setExplicit(['header-1'])}>Select a row</button>
            <output aria-label="Selected rows">{selected}</output>
            <div
                role="group"
                aria-label="Editor"
                onKeyDown={(e) => {
                    if (e.key === 'Escape' && !e.defaultPrevented) {
                        e.preventDefault();
                        onCancel();
                    }
                }}
            >
                <button type="button">Save</button>
            </div>
            <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
        </div>
    );
}

/** Select a row, put focus on the editor's Save button, and open the popover from there. */
async function openFromTheEditor() {
    vi.spyOn(apiModule, 'fetchSelectionSummary')
        .mockResolvedValue({ count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD' });
    const onCancel = vi.fn();
    const user = userEvent.setup();
    render(<EditingRegister onCancel={onCancel} />);
    await user.click(screen.getByRole('button', { name: 'Select a row' }));
    const save = screen.getByRole('button', { name: 'Save' });
    act(() => save.focus());
    await user.keyboard('{Control>}j{/Control}');
    expect(popover()).toBeInTheDocument();
    expect(save).not.toHaveFocus();
    return { user, onCancel, save };
}

describe('RegisterDateJumpPopover is a popover, not a modal', () => {
    // As the register header's Filter, Show and Sort (ADR-0023 §B.4, §L):
    // nothing behind it is blocked or claimed to be.
    it('is a non-modal dialog named Jump to date', async () => {
        const user = userEvent.setup();
        renderRegister();
        await selectARowAndOpen(user);

        expect(popover()).not.toHaveAttribute('aria-modal');
    });

    it('closes on a press outside, which goes on to what it landed on and leaves focus there', async () => {
        const user = userEvent.setup();
        renderRegister();
        await selectARowAndOpen(user);
        const elsewhere = screen.getByRole('textbox', { name: 'Marks only' });

        await user.click(elsewhere);

        expect(popover()).toBeNull();
        expect(elsewhere).toHaveFocus();
    });

    it('lets a press outside act on the control it landed on', async () => {
        // Nothing covers the page: the press closes the popover and is the
        // control's too, as it is for the header's popovers.
        const user = userEvent.setup();
        renderRegister();
        await user.keyboard('{Control>}j{/Control}');
        expect(popover()).toBeInTheDocument();
        expect(selectedRows()).toBe('0');
        const select = screen.getByRole('button', { name: 'Select a row' });

        await user.click(select);

        expect(popover()).toBeNull();
        expect(selectedRows()).toBe('1');
        expect(select).toHaveFocus();
    });

    it('stays open on a press inside it that is on no control', async () => {
        const user = userEvent.setup();
        renderRegister();
        await selectARowAndOpen(user);

        await user.click(screen.getByText('to jump', { exact: false }));

        expect(popover()).toBeInTheDocument();
    });

    it('stays open when Tab leaves it', async () => {
        // No focus trap: Shift+Tab from its date field goes back to the control
        // it was opened from.
        const user = userEvent.setup();
        renderRegister();
        const select = screen.getByRole('button', { name: 'Select a row' });
        act(() => select.focus());
        await user.keyboard('{Control>}j{/Control}');
        expect(popover()!.querySelector('input[type="date"]')).toHaveFocus();

        await user.tab({ shift: true });

        expect(select).toHaveFocus();
        expect(popover()).toBeInTheDocument();
    });
});

describe('RegisterDateJumpPopover in the page\'s Tab order', () => {
    // It sits in the Tab order right after where it was opened from, as a header
    // popover sits right after its button (ADR-0023 §L), so Tab runs from that
    // control into it and out of it to the control after that one, and never
    // drops to the page. Here it is rendered after every control but one, as the
    // registers render it outside the list.
    function Page({ openerShown = true }: { openerShown?: boolean }) {
        return (
            <div>
                <button type="button">First</button>
                {openerShown ? (
                    <div>
                        <button type="button">Opener</button>
                    </div>
                ) : null}
                <button type="button">Next</button>
                <button type="button">Last</button>
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
                <button type="button">After</button>
            </div>
        );
    }

    /** Open the popover from the named button, or from the page itself with nothing focused. */
    async function openFrom(name: string | null) {
        const user = userEvent.setup();
        const view = render(<Page />);
        if (name === null) act(() => (document.activeElement as HTMLElement | null)?.blur());
        else act(() => screen.getByRole('button', { name }).focus());
        await user.keyboard('{Control>}j{/Control}');
        expect(dateField()).toHaveFocus();
        return { user, view };
    }

    const dateField = () => popover()!.querySelector<HTMLInputElement>('input[type="date"]')!;

    it('goes on from its last control to the control after where it was opened from', async () => {
        const { user } = await openFrom('Opener');
        await user.tab();
        expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus();
        await user.tab();
        expect(screen.getByRole('button', { name: 'Jump' })).toHaveFocus();

        await user.tab();

        expect(document.activeElement).not.toBe(document.body);
        expect(screen.getByRole('button', { name: 'Next' })).toHaveFocus();
        expect(popover()).toBeInTheDocument();
    });

    it('goes back from its first control to where it was opened from', async () => {
        const { user } = await openFrom('Opener');

        await user.tab({ shift: true });

        expect(screen.getByRole('button', { name: 'Opener' })).toHaveFocus();
        expect(popover()).toBeInTheDocument();
    });

    it('opened from the page itself, goes on from its last control to the page\'s first control', async () => {
        const { user } = await openFrom(null);
        await user.tab();
        await user.tab();
        expect(screen.getByRole('button', { name: 'Jump' })).toHaveFocus();

        await user.tab();

        expect(document.activeElement).not.toBe(document.body);
        expect(screen.getByRole('button', { name: 'First' })).toHaveFocus();
        expect(popover()).toBeInTheDocument();
    });

    it('comes back into it by Tab from where it was opened, and by Shift+Tab from the control after that', async () => {
        const { user } = await openFrom('Opener');
        await user.tab({ shift: true });
        expect(screen.getByRole('button', { name: 'Opener' })).toHaveFocus();

        await user.tab();
        expect(dateField()).toHaveFocus();

        act(() => screen.getByRole('button', { name: 'Next' }).focus());
        await user.tab({ shift: true });
        expect(screen.getByRole('button', { name: 'Jump' })).toHaveFocus();
    });

    it('is passed over by Tab where the page renders it, so it is in the Tab order once', async () => {
        const { user } = await openFrom('Opener');
        act(() => screen.getByRole('button', { name: 'Last' }).focus());

        await user.tab();
        expect(screen.getByRole('button', { name: 'After' })).toHaveFocus();

        await user.tab({ shift: true });
        expect(screen.getByRole('button', { name: 'Last' })).toHaveFocus();
        expect(popover()).toBeInTheDocument();
    });

    it('stays open, with what was typed and the focus, when what it was opened from leaves the page', async () => {
        // The register's rows leave the page as they scroll out of its window, and
        // its list is swapped for a placeholder while loading or empty: opened from
        // either, the popover is still the reader's.
        const { user, view } = await openFrom('Opener');
        await user.type(dateField(), '2026-02-14');
        const typed = dateField().value;
        expect(typed).not.toBe('');

        view.rerender(<Page openerShown={false} />);
        await act(async () => {});

        expect(popover()).toBeInTheDocument();
        expect(dateField()).toHaveFocus();
        expect(dateField().value).toBe(typed);

        // With nothing left to sit after, it sits at the start of the page.
        act(() => screen.getByRole('button', { name: 'Jump' }).focus());
        await user.tab();
        expect(screen.getByRole('button', { name: 'First' })).toHaveFocus();
    });

    it('opened with nothing focused, is reached by Tab from a fresh start and past the page\'s last control, and by Shift+Tab from its first', async () => {
        // It sits at the start of the page: a guard at the start of the body leads
        // into it, so once Tab has left it, it can be reached again.
        const { user } = await openFrom(null);

        act(() => (document.activeElement as HTMLElement | null)?.blur());
        await user.tab();
        expect(dateField()).toHaveFocus();

        act(() => screen.getByRole('button', { name: 'After' }).focus());
        await user.tab();
        await user.tab();
        expect(dateField()).toHaveFocus();

        act(() => screen.getByRole('button', { name: 'First' }).focus());
        await user.tab({ shift: true });
        expect(screen.getByRole('button', { name: 'Jump' })).toHaveFocus();
    });

    /**
     * The reader moves to the browser's own controls (the address bar, by F6 or a
     * click there): focus leaves the page, and time passes before the next key.
     */
    async function toTheBrowsersControls() {
        act(() => (document.activeElement as HTMLElement | null)?.blur());
        await act(() => new Promise<void>((resolve) => { setTimeout(resolve, 0); }));
    }

    /**
     * A Tab pressed in the browser's own controls: the browser moves focus into the
     * page itself, with no keydown in the page, at its first Tab stop going forward
     * and at its last going back.
     */
    function tabFromTheBrowsersControls(stop: Element | null) {
        expect(stop).toHaveAttribute('data-focus-guard');
        act(() => (stop as HTMLElement).focus());
    }

    it('sitting at the start of the page, goes into its date field when Tab from the browser\'s own controls enters the page, whichever way the page\'s last Tab went', async () => {
        // The page last heard a Shift+Tab, from the date field to the page's last
        // control; the Tab from the address bar is not heard at all.
        const { user } = await openFrom(null);
        await user.tab({ shift: true });
        expect(screen.getByRole('button', { name: 'After' })).toHaveFocus();
        await toTheBrowsersControls();

        tabFromTheBrowsersControls(document.body.firstElementChild);

        expect(dateField()).toHaveFocus();
    });

    it('drawn last, passes over itself to the page\'s last control when Shift+Tab from the browser\'s own controls enters the page at its end, whichever way the page\'s last Tab went', async () => {
        // As the registers draw it: after every control. The page last heard a
        // Tab, from Jump to the control after the opener.
        const user = userEvent.setup();
        render(
            <div>
                <button type="button">First</button>
                <button type="button">Opener</button>
                <button type="button">Next</button>
                <button type="button">Last</button>
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
            </div>,
        );
        act(() => screen.getByRole('button', { name: 'Opener' }).focus());
        await user.keyboard('{Control>}j{/Control}');
        act(() => screen.getByRole('button', { name: 'Jump' }).focus());
        await user.tab();
        expect(screen.getByRole('button', { name: 'Next' })).toHaveFocus();
        await toTheBrowsersControls();

        tabFromTheBrowsersControls(popover()!.lastElementChild);

        expect(screen.getByRole('button', { name: 'Last' })).toHaveFocus();
        expect(popover()).toBeInTheDocument();
    });

    it.each([
        ['the page\'s last control', () => (
            <div>
                <button type="button">First</button>
                <button type="button">Opener</button>
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
            </div>
        ), () => screen.getByRole('button', { name: 'Opener' })],
        ['the row list with nothing after it', () => (
            <div>
                <button type="button">First</button>
                <div role="grid" aria-label="Transactions" tabIndex={-1}>
                    <button type="button">Row status</button>
                </div>
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
            </div>
        ), () => screen.getByRole('grid', { name: 'Transactions' })],
    ])('drawn last and opened from %s, goes into Jump when Shift+Tab from the browser\'s own controls enters the page at its end', async (_, page, opener) => {
        // Right after its opener, with no Tab stop after that, it is the page's
        // last Tab stop, where Shift+Tab from the address bar enters. Passing over
        // it to the opener would leave it out of the order.
        const user = userEvent.setup();
        render(page());
        act(() => opener().focus());
        await user.keyboard('{Control>}j{/Control}');
        expect(dateField()).toHaveFocus();
        await toTheBrowsersControls();

        tabFromTheBrowsersControls(popover()!.lastElementChild);

        expect(screen.getByRole('button', { name: 'Jump' })).toHaveFocus();
    });

    it('drawn before an opener that is the page\'s last control, passes over itself on a Shift+Tab heard from that opener', async () => {
        // Nothing follows the opener here either, but the Shift+Tab is the page's
        // own, from a control after the place the page renders it.
        const user = userEvent.setup();
        render(
            <div>
                <button type="button">First</button>
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
                <button type="button">Opener</button>
            </div>,
        );
        const opener = screen.getByRole('button', { name: 'Opener' });
        act(() => opener.focus());
        await user.keyboard('{Control>}j{/Control}');
        act(() => opener.focus());

        await user.tab({ shift: true });

        expect(screen.getByRole('button', { name: 'First' })).toHaveFocus();
    });

    it('once what it was opened from has left the page, is reached by Shift+Tab from the page\'s first control and by Tab from a fresh start', async () => {
        // The register replaces the list's wrapper on a filter, a sort or a
        // refresh, and the list unmounts rows: the guard beside the opener goes
        // with them, and the popover then sits at the start of the page.
        const { user, view } = await openFrom('Opener');
        view.rerender(<Page openerShown={false} />);
        await act(async () => {});

        act(() => screen.getByRole('button', { name: 'First' }).focus());
        await user.tab({ shift: true });
        expect(screen.getByRole('button', { name: 'Jump' })).toHaveFocus();

        act(() => (document.activeElement as HTMLElement | null)?.blur());
        await user.tab();
        expect(dateField()).toHaveFocus();

        // Still in the Tab order once: where the page renders it, it is passed over.
        act(() => screen.getByRole('button', { name: 'Last' }).focus());
        await user.tab();
        expect(screen.getByRole('button', { name: 'After' })).toHaveFocus();
    });

    it('with nothing after the place the page renders it and no opener on the page, goes into it by Tab from the page\'s last control', async () => {
        // The registers render it last. The Tab order starts again at the start of
        // the page past the last control, which is where it sits.
        const user = userEvent.setup();
        render(
            <div>
                <button type="button">First</button>
                <button type="button">Last</button>
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
            </div>,
        );
        act(() => (document.activeElement as HTMLElement | null)?.blur());
        await user.keyboard('{Control>}j{/Control}');
        act(() => screen.getByRole('button', { name: 'Last' }).focus());

        await user.tab();

        expect(dateField()).toHaveFocus();
    });

    it('with nothing before the place the page renders it and no opener on the page, comes back into it by Shift+Tab from the page\'s first control', async () => {
        // Rendered first, it is still at the start of the page, before the first
        // control: Shift+Tab from that control goes into it, not round to the end.
        const user = userEvent.setup();
        render(
            <div>
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
                <button type="button">First</button>
                <button type="button">Last</button>
            </div>,
        );
        act(() => (document.activeElement as HTMLElement | null)?.blur());
        await user.keyboard('{Control>}j{/Control}');
        act(() => screen.getByRole('button', { name: 'First' }).focus());

        await user.tab({ shift: true });

        expect(screen.getByRole('button', { name: 'Jump' })).toHaveFocus();
    });

    it('when its opener is removed from a parent that stays, takes the guard left behind away and sits at the start of the page', async () => {
        function SharedParent({ openerShown }: { openerShown: boolean }) {
            return (
                <div>
                    <button type="button">First</button>
                    <div>
                        {openerShown ? <button type="button">Opener</button> : null}
                        <button type="button">Sibling</button>
                    </div>
                    <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
                    <button type="button">After</button>
                </div>
            );
        }
        const user = userEvent.setup();
        const view = render(<SharedParent openerShown />);
        act(() => screen.getByRole('button', { name: 'Opener' }).focus());
        await user.keyboard('{Control>}j{/Control}');
        expect(dateField()).toHaveFocus();

        view.rerender(<SharedParent openerShown={false} />);
        await act(async () => {});

        act(() => screen.getByRole('button', { name: 'First' }).focus());
        await user.tab();
        expect(screen.getByRole('button', { name: 'Sibling' })).toHaveFocus();

        act(() => screen.getByRole('button', { name: 'First' }).focus());
        await user.tab({ shift: true });
        expect(screen.getByRole('button', { name: 'Jump' })).toHaveFocus();
    });

    it('when its opener moves within the page, keeps its place right after it', async () => {
        function Reordered({ order }: { order: string[] }) {
            return (
                <div>
                    <button type="button">First</button>
                    <div>
                        {order.map((name) => <button key={name} type="button">{name}</button>)}
                    </div>
                    <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
                    <button type="button">After</button>
                </div>
            );
        }
        const user = userEvent.setup();
        const view = render(<Reordered order={['Opener', 'Second', 'Third']} />);
        act(() => screen.getByRole('button', { name: 'Opener' }).focus());
        await user.keyboard('{Control>}j{/Control}');
        expect(dateField()).toHaveFocus();

        // Keyed, so the opener itself is moved to the end, not the others past it.
        view.rerender(<Reordered order={['Second', 'Third', 'Opener']} />);
        await act(async () => {});

        act(() => screen.getByRole('button', { name: 'First' }).focus());
        await user.tab();
        expect(screen.getByRole('button', { name: 'Second' })).toHaveFocus();

        act(() => screen.getByRole('button', { name: 'Opener' }).focus());
        await user.tab();
        expect(dateField()).toHaveFocus();
    });

    it('opened from a list that is no Tab stop of its own, goes back to the list, not to a row inside it', async () => {
        // The register's row list: focusable, no Tab stop, holding the rows' own
        // controls, which do take Tab.
        const user = userEvent.setup();
        render(
            <div>
                <button type="button">First</button>
                <div role="grid" aria-label="Transactions" tabIndex={-1}>
                    <button type="button">Row 1 status</button>
                    <button type="button">Row 2 status</button>
                </div>
                <button type="button">Next</button>
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
            </div>,
        );
        const list = screen.getByRole('grid', { name: 'Transactions' });
        act(() => list.focus());
        await user.keyboard('{Control>}j{/Control}');
        expect(dateField()).toHaveFocus();

        await user.tab({ shift: true });

        expect(list).toHaveFocus();
        expect(popover()).toBeInTheDocument();

        // Out of its last control, Tab goes on to what follows the list.
        act(() => screen.getByRole('button', { name: 'Jump' }).focus());
        await user.tab();
        expect(screen.getByRole('button', { name: 'Next' })).toHaveFocus();
    });
});

describe('RegisterDateJumpPopover focus on close', () => {
    // Closing removes the focused control. Focus left on the page would be out
    // of the editor's reach, and the next Escape would clear the selection
    // instead of cancelling the edit.
    it('gives focus back to where it opened from on Escape, so the next Escape cancels the edit', async () => {
        const { user, onCancel, save } = await openFromTheEditor();

        await user.keyboard('{Escape}');

        expect(popover()).toBeNull();
        expect(save).toHaveFocus();
        expect(selectedRows()).toBe('1');
        expect(onCancel).not.toHaveBeenCalled();

        await user.keyboard('{Escape}');
        expect(onCancel).toHaveBeenCalledTimes(1);
        expect(selectedRows()).toBe('1');
    });

    it('gives focus back when the shortcut closes it', async () => {
        const { user, save } = await openFromTheEditor();

        await user.keyboard('{Control>}j{/Control}');

        expect(popover()).toBeNull();
        expect(save).toHaveFocus();
    });

    it('gives focus back when its Cancel button closes it from the keyboard', async () => {
        const { user, save } = await openFromTheEditor();
        act(() => screen.getByRole('button', { name: 'Cancel' }).focus());

        await user.keyboard('{Enter}');

        expect(popover()).toBeNull();
        expect(save).toHaveFocus();
    });

    /**
     * A register in miniature whose opener can leave the page: the row list (the
     * register's own scroll surface, its focus home), an add control, and a row
     * control to open from that the list can take away, or the whole list.
     */
    function RegisterWithList({ rowShown, listShown = true }: { rowShown: boolean; listShown?: boolean }) {
        return (
            <div>
                <button type="button" data-register-home="add">+ New transaction</button>
                {listShown ? (
                    <RegisterScrollSurface scrollRef={() => {}}>
                        {rowShown ? (
                            <div>
                                <button type="button">Row status</button>
                            </div>
                        ) : null}
                    </RegisterScrollSurface>
                ) : null}
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
            </div>
        );
    }

    async function openFromTheRowThenTakeIt(next: { rowShown: boolean; listShown?: boolean }) {
        const user = userEvent.setup();
        const view = render(<RegisterWithList rowShown />);
        act(() => screen.getByRole('button', { name: 'Row status' }).focus());
        await user.keyboard('{Control>}j{/Control}');
        expect(popover()).toBeInTheDocument();
        view.rerender(<RegisterWithList {...next} />);
        await act(async () => {});
        expect(screen.queryByRole('button', { name: 'Row status' })).toBeNull();
        return user;
    }

    it('gives focus to the register\'s row list when what it was opened from has left the page', async () => {
        const user = await openFromTheRowThenTakeIt({ rowShown: false });

        await user.keyboard('{Escape}');

        expect(popover()).toBeNull();
        expect(document.activeElement).not.toBe(document.body);
        expect(screen.getByRole('grid', { name: 'Transactions' })).toHaveFocus();
    });

    it('gives it to the list when Cancel closes it from the keyboard after its opener has gone', async () => {
        const user = await openFromTheRowThenTakeIt({ rowShown: false });
        act(() => screen.getByRole('button', { name: 'Cancel' }).focus());

        await user.keyboard('{Enter}');

        expect(popover()).toBeNull();
        expect(screen.getByRole('grid', { name: 'Transactions' })).toHaveFocus();
    });

    it('gives it to the add control when the list is not on the page either', async () => {
        const user = await openFromTheRowThenTakeIt({ rowShown: false, listShown: false });

        await user.keyboard('{Escape}');

        expect(popover()).toBeNull();
        expect(screen.getByRole('button', { name: '+ New transaction' })).toHaveFocus();
    });

    it.each([
        ['Escape', async (user: ReturnType<typeof userEvent.setup>) => { await user.keyboard('{Escape}'); }],
        ['Cancel', async (user: ReturnType<typeof userEvent.setup>) => {
            act(() => screen.getByRole('button', { name: 'Cancel' }).focus());
            await user.keyboard('{Enter}');
        }],
        ['Jump', async (user: ReturnType<typeof userEvent.setup>) => {
            await user.type(popover()!.querySelector<HTMLInputElement>('input[type="date"]')!, '2026-02-14');
            act(() => screen.getByRole('button', { name: 'Jump' }).focus());
            await user.keyboard('{Enter}');
        }],
        ['the shortcut', async (user: ReturnType<typeof userEvent.setup>) => { await user.keyboard('{Control>}j{/Control}'); }],
    ])('opened with nothing focused, gives focus to the register\'s row list when %s closes it', async (_, close) => {
        // With no opener there is nothing to give focus back to: it goes to the
        // register's focus home, as it does once an opener has left the page.
        const user = userEvent.setup();
        render(<RegisterWithList rowShown />);
        act(() => (document.activeElement as HTMLElement | null)?.blur());
        await user.keyboard('{Control>}j{/Control}');
        expect(popover()).toBeInTheDocument();

        await close(user);

        expect(popover()).toBeNull();
        expect(document.activeElement).not.toBe(document.body);
        expect(screen.getByRole('grid', { name: 'Transactions' })).toHaveFocus();
    });

    /**
     * A register in miniature with its focus home: the hook that gives lost focus
     * to the row list, an add control (none in a category register or a viewer's),
     * the search box, and the list, which a filter takes off the page while the
     * filtered rows load.
     */
    function RegisterWithFocusHome({ listShown, add = true }: { listShown: boolean; add?: boolean }) {
        const [list, setList] = useState<HTMLDivElement | null>(null);
        useRegisterFocusHome(list, { editing: false, hasSelection: false });
        return (
            <div>
                {add ? <button type="button" data-register-home="add">+ New transaction</button> : null}
                <input type="search" aria-label="Search transactions" data-register-home="search" />
                {listShown ? (
                    <RegisterScrollSurface scrollRef={setList}>
                        <div>Row</div>
                    </RegisterScrollSurface>
                ) : null}
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
            </div>
        );
    }

    /** Opened from the row list, which a filter then takes off the page while it loads. */
    async function openFromTheListThenFilter({ add = true }: { add?: boolean } = {}) {
        const user = userEvent.setup();
        const view = render(<RegisterWithFocusHome listShown add={add} />);
        act(() => screen.getByRole('grid', { name: 'Transactions' }).focus());
        await user.keyboard('{Control>}j{/Control}');
        view.rerender(<RegisterWithFocusHome listShown={false} add={add} />);
        await act(async () => {});
        expect(popover()).toBeInTheDocument();
        expect(popover()!.querySelector('input[type="date"]')).toHaveFocus();
        return { user, view };
    }

    it('closed by a press outside once its opener has gone, leaves focus where the press put it and records no stand-in', async () => {
        // No add control (a category register, a viewer's): the search box is the
        // register's home while the list is off the page. A press on it closes the
        // popover; the focus the press gives it is the reader's, not the list's to
        // take back when the filtered rows land.
        const { user, view } = await openFromTheListThenFilter({ add: false });
        const search = screen.getByRole('searchbox', { name: 'Search transactions' });

        await user.click(search);

        expect(popover()).toBeNull();
        expect(search).toHaveFocus();

        // The filtered rows land, with no key pressed since.
        view.rerender(<RegisterWithFocusHome listShown add={false} />);
        await act(async () => {});

        expect(search).toHaveFocus();
    });

    it('opened with nothing focused and closed by a press outside, leaves focus where the press put it and records no stand-in', async () => {
        const user = userEvent.setup();
        const view = render(<RegisterWithFocusHome listShown={false} add={false} />);
        act(() => (document.activeElement as HTMLElement | null)?.blur());
        await user.keyboard('{Control>}j{/Control}');
        expect(popover()).toBeInTheDocument();
        const search = screen.getByRole('searchbox', { name: 'Search transactions' });

        await user.click(search);

        expect(popover()).toBeNull();
        expect(search).toHaveFocus();

        view.rerender(<RegisterWithFocusHome listShown add={false} />);
        await act(async () => {});

        expect(search).toHaveFocus();
    });

    it('opened again after a press outside closed it, gives focus to the focus home when Escape closes it', async () => {
        // The press belongs to the close it caused, not to the next one.
        const user = userEvent.setup();
        render(<RegisterWithList rowShown />);
        act(() => (document.activeElement as HTMLElement | null)?.blur());
        await user.keyboard('{Control>}j{/Control}');
        await user.click(screen.getByRole('button', { name: '+ New transaction' }));
        expect(popover()).toBeNull();

        act(() => (document.activeElement as HTMLElement | null)?.blur());
        await user.keyboard('{Control>}j{/Control}');
        expect(popover()).toBeInTheDocument();
        await user.keyboard('{Escape}');

        expect(popover()).toBeNull();
        expect(screen.getByRole('grid', { name: 'Transactions' })).toHaveFocus();
    });

    it('closed while the list is off the page, gives focus to the add control as the list\'s stand-in, which the list takes back when it lands', async () => {
        const { user, view } = await openFromTheListThenFilter();
        await user.keyboard('{Escape}');
        expect(popover()).toBeNull();
        expect(screen.getByRole('button', { name: '+ New transaction' })).toHaveFocus();

        // The filtered rows land, with nothing pressed since.
        view.rerender(<RegisterWithFocusHome listShown />);
        await act(async () => {});

        expect(screen.getByRole('grid', { name: 'Transactions' })).toHaveFocus();
    });

    it('leaves focus on the add control when the reader pressed a key there before the list landed', async () => {
        const { user, view } = await openFromTheListThenFilter();
        await user.keyboard('{Escape}');
        const add = screen.getByRole('button', { name: '+ New transaction' });
        expect(add).toHaveFocus();
        await user.keyboard('{Shift}');

        view.rerender(<RegisterWithFocusHome listShown />);
        await act(async () => {});

        expect(add).toHaveFocus();
    });

    it('leaves focus on a control the user moved to before closing it, without moving it even briefly', async () => {
        // Even a passing move would blur that control, and a list closes when
        // its control loses focus.
        const { user } = await openFromTheEditor();
        const elsewhere = screen.getByRole('button', { name: 'Select a row' });
        act(() => elsewhere.focus());
        const blurs = vi.fn();
        elsewhere.addEventListener('blur', blurs);

        await user.keyboard('{Escape}');

        expect(popover()).toBeNull();
        expect(elsewhere).toHaveFocus();
        expect(blurs).not.toHaveBeenCalled();
    });
});

describe('RegisterDateJumpPopover leaves a key pressed in a popup to that popup', () => {
    // As the register's other shortcuts do (ADR-0023 §B.2): Ctrl/⌘+J pressed
    // in a dialog or a menu is that popup's, and opens nothing behind it.
    function WithPopups() {
        return (
            <>
                <Modal open onClose={vi.fn()} ariaLabel="Delete transactions">
                    <button type="button">Confirm</button>
                </Modal>
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
            </>
        );
    }

    it('does not open from a key pressed in a dialog', async () => {
        const user = userEvent.setup();
        render(<WithPopups />);
        const confirm = screen.getByRole('button', { name: 'Confirm' });
        act(() => confirm.focus());

        await user.keyboard('{Control>}j{/Control}');

        expect(popover()).toBeNull();
        expect(confirm).toHaveFocus();
    });

    it('does not open from a key pressed in a menu', async () => {
        const user = userEvent.setup();
        render(
            <>
                <ContextMenu anchor={{ x: 10, y: 10 }} items={[{ id: 'edit', label: 'Edit', onSelect: vi.fn() }]} onClose={vi.fn()} label="Row actions" />
                <RegisterDateJumpPopover buckets={BUCKETS} onSeek={vi.fn()} />
            </>,
        );
        const menu = screen.getByRole('menu', { name: 'Row actions' });
        expect(menu).toHaveFocus();

        await user.keyboard('{Control>}j{/Control}');

        expect(popover()).toBeNull();
        expect(menu).toHaveFocus();
    });

    it('sits on the header popovers\' tier, under every dialog', async () => {
        const user = userEvent.setup();
        renderRegister();
        await user.keyboard('{Control>}j{/Control}');

        expect(popover()).toHaveClass('z-30');
        expect(popover()).not.toHaveClass('z-50');
    });
});
