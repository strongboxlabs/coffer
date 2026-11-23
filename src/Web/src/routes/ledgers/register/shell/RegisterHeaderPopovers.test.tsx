import { afterEach, describe, expect, it, onTestFinished, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';

import * as apiModule from '@/lib/api';
import { useSelection } from '@/lib/useSelection';
import type { TagDto } from '@/lib/types';
import { RegisterFilterControls } from './RegisterFilterControls';
import { RegisterSortMenu } from './RegisterSortMenu';
import { RegisterStatusMenu } from './RegisterStatusMenu';
import { DEFAULT_SORT } from './registerSort';

// The register header's popovers (Filter, Show, Sort) close on Esc (ADR-0023
// §L), as a list does by the marking convention (§F): their own Esc is marked
// and stopped, so the register's selection never clears with it, and an Esc a
// list inside already used is not theirs. However they close, focus goes back
// to the button that opened them.

const TAGS: TagDto[] = [
    { id: 't1', name: 'work', color: null, usageCount: 1, transactionCount: 1 },
];

afterEach(() => {
    vi.restoreAllMocks();
});

/**
 * A register in miniature: a selection that Escape clears, and one popover
 * inside a handler that hears every Escape that reaches it, as an editor or a
 * dialog around a popover would.
 */
function Register({ children, onParentEscape }: {
    children: ReactNode;
    onParentEscape: (defaultPrevented: boolean) => void;
}) {
    const selection = useSelection({ ledgerId: 'ledger-1', accountId: 'account-1', statusFilter: 'all' });
    const selected = selection.selection.kind === 'explicit' ? selection.selection.headerIds.length : -1;
    return (
        <div>
            <button type="button" onClick={() => selection.setExplicit(['header-1'])}>Select a row</button>
            <output aria-label="Selected rows">{selected}</output>
            <div onKeyDown={(e) => { if (e.key === 'Escape') onParentEscape(e.defaultPrevented); }}>
                {children}
            </div>
        </div>
    );
}

interface Case {
    name: string;
    popover: ReactNode;
    toggle: RegExp;
    isOpen: () => boolean;
    /** A control inside the open popover that is not a field. */
    inside: () => HTMLElement;
}

const CASES: Case[] = [
    {
        name: 'Show',
        popover: <RegisterStatusMenu statusFilter="all" onChange={vi.fn()} counts={null} />,
        toggle: /Show:/,
        isOpen: () => screen.queryByRole('listbox') !== null,
        inside: () => screen.getAllByRole('option')[1]!,
    },
    {
        name: 'Sort',
        popover: <RegisterSortMenu sort={DEFAULT_SORT} onChange={vi.fn()} investment={false} />,
        toggle: /Sort:/,
        isOpen: () => screen.queryByRole('listbox') !== null,
        inside: () => screen.getAllByRole('option')[1]!,
    },
    {
        name: 'Filter',
        popover: <RegisterFilterControls filter={{}} onChange={vi.fn()} accounts={[]} currency="USD" tags={TAGS} />,
        toggle: /^Filter/,
        // By role: the currency's symbol inside the field is hidden from its name.
        isOpen: () => screen.queryByRole('spinbutton', { name: 'Amount min' }) !== null,
        inside: () => screen.getByRole('spinbutton', { name: 'Amount min' }),
    },
];

function renderRegister(popover: ReactNode) {
    vi.spyOn(apiModule, 'fetchSelectionSummary')
        .mockResolvedValue({ count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD' });
    // The native event, taken by a capture listener before anything handles
    // it and read once it is done.
    const escapes: KeyboardEvent[] = [];
    const capture = (e: KeyboardEvent) => { if (e.key === 'Escape') escapes.push(e); };
    window.addEventListener('keydown', capture, true);
    onTestFinished(() => window.removeEventListener('keydown', capture, true));
    const parentEscapes: boolean[] = [];
    render(<Register onParentEscape={(marked) => parentEscapes.push(marked)}>{popover}</Register>);
    return { parentEscapes, marks: () => escapes.map((e) => e.defaultPrevented) };
}

const selectedRows = () => screen.getByRole('status', { name: 'Selected rows' }).textContent;

async function selectARowAndOpen(user: ReturnType<typeof userEvent.setup>, c: Case) {
    await user.click(screen.getByRole('button', { name: 'Select a row' }));
    expect(selectedRows()).toBe('1');
    await user.click(screen.getByRole('button', { name: c.toggle }));
    expect(c.isOpen()).toBe(true);
}

describe.each(CASES)('the register\'s $name popover on Escape', (c) => {
    it('closes on Escape from its button, marked and stopped, and leaves the selection to the next Escape', async () => {
        const user = userEvent.setup();
        const { parentEscapes, marks } = renderRegister(c.popover);
        await selectARowAndOpen(user, c);
        const toggle = screen.getByRole('button', { name: c.toggle });
        expect(toggle).toHaveFocus();

        await user.keyboard('{Escape}');

        expect(c.isOpen()).toBe(false);
        expect(marks()).toEqual([true]);
        expect(parentEscapes).toEqual([]);
        expect(toggle).toHaveFocus();
        expect(selectedRows()).toBe('1');

        // Closed, it lets the Escape go on unmarked.
        await user.keyboard('{Escape}');
        expect(parentEscapes).toEqual([false]);
        expect(selectedRows()).toBe('0');
    });

    it('closes on Escape from inside, stopped, and gives focus back to its button', async () => {
        const user = userEvent.setup();
        const { parentEscapes } = renderRegister(c.popover);
        await selectARowAndOpen(user, c);
        act(() => c.inside().focus());

        await user.keyboard('{Escape}');

        expect(c.isOpen()).toBe(false);
        expect(parentEscapes).toEqual([]);
        expect(screen.getByRole('button', { name: c.toggle })).toHaveFocus();
        expect(selectedRows()).toBe('1');
    });

    it('closes on an Escape pressed with focus on the page, and leaves the selection', async () => {
        // A click that does not focus the button leaves focus on the page.
        const user = userEvent.setup();
        renderRegister(c.popover);
        await selectARowAndOpen(user, c);
        act(() => (document.activeElement as HTMLElement | null)?.blur());

        await user.keyboard('{Escape}');

        expect(c.isOpen()).toBe(false);
        expect(selectedRows()).toBe('1');

        await user.keyboard('{Escape}');
        expect(selectedRows()).toBe('0');
    });
});

describe.each(CASES.filter((c) => c.name !== 'Filter'))('the register\'s $name menu', (c) => {
    it('gives focus back to its button when an option is picked', async () => {
        const user = userEvent.setup();
        renderRegister(c.popover);
        await selectARowAndOpen(user, c);

        await user.click(c.inside());

        expect(c.isOpen()).toBe(false);
        expect(screen.getByRole('button', { name: c.toggle })).toHaveFocus();
    });
});

describe('the register\'s Filter popover around its lists', () => {
    const filter = CASES.find((c) => c.name === 'Filter')!;

    it('stays open on the Escape that closes the tag suggestions, and closes on the next', async () => {
        const user = userEvent.setup();
        const { parentEscapes } = renderRegister(filter.popover);
        await selectARowAndOpen(user, filter);
        const tag = screen.getByRole('combobox', { name: 'Filter by tag' });
        act(() => tag.focus());
        await user.keyboard('wo');
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        await user.keyboard('{Escape}');

        expect(screen.queryByRole('listbox')).toBeNull();
        expect(filter.isOpen()).toBe(true);
        expect(tag).toHaveFocus();

        await user.keyboard('{Escape}');
        expect(filter.isOpen()).toBe(false);
        expect(screen.getByRole('button', { name: filter.toggle })).toHaveFocus();
        expect(selectedRows()).toBe('1');
        expect(parentEscapes).toEqual([]);

        await user.keyboard('{Escape}');
        expect(selectedRows()).toBe('0');
    });

    it('leaves an Escape in the search box to the box while the popover is closed', async () => {
        const user = userEvent.setup();
        const { marks } = renderRegister(filter.popover);
        act(() => screen.getByRole('searchbox', { name: 'Search transactions' }).focus());

        await user.keyboard('{Escape}');

        expect(marks()).toEqual([false]);
    });
});
