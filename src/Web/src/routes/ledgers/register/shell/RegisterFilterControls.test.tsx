import { createRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { afterEach, describe, it, expect, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import * as apiModule from '@/lib/api';
import type { RegisterFilterArgs } from '@/lib/api/register';
import { currencyAffix, formatCurrency } from '@/lib/money';
import { useSelection } from '@/lib/useSelection';
import type { AccountSummary, SecuritySummary } from '@/lib/types';
import { RegisterFilterChips } from './RegisterFilterChips';
import { RegisterFilterControls } from './RegisterFilterControls';

afterEach(() => {
    vi.restoreAllMocks();
});

// The editors' tag selects read "Tags (optional)" while empty (ADR-0023). The
// register's filter bar has its own tag control, and its empty state asks a
// different question — which tag to filter by — so it keeps its own wording.
describe('RegisterFilterControls tag filter', () => {
    it('keeps its "Any tag" placeholder, not the editors\' "Tags (optional)"', () => {
        render(
            <RegisterFilterControls
                filter={{}}
                onChange={() => {}}
                accounts={[]}
                currency="USD"
                tags={[{ id: 't1', name: 'Tag A', color: null, usageCount: 1, transactionCount: 1 }]}
            />,
        );
        fireEvent.click(screen.getByRole('button', { name: /^Filter/ }));

        expect(screen.getByRole('combobox', { name: 'Filter by tag' })).toHaveAttribute('placeholder', 'Any tag');
        expect(screen.queryByText('Tags (optional)')).not.toBeInTheDocument();
        expect(screen.queryByPlaceholderText('Tags (optional)')).not.toBeInTheDocument();
    });
});

// The tag slot holds one control at a time: the tag field, or the tag with its
// clear button. Clearing the tag and picking one each replace the control that
// held focus, so focus goes on to the control that takes its place (ADR-0023
// §L) and stays in the popover, rather than falling to the page under it.
describe('RegisterFilterControls tag filter, where focus goes', () => {
    const TAGS = [{ id: 't1', name: 'Tag A', color: null, usageCount: 1, transactionCount: 1 }];

    function TagFilter({ initial }: { initial: RegisterFilterArgs }) {
        const [filter, setFilter] = useState<RegisterFilterArgs>(initial);
        return <RegisterFilterControls filter={filter} onChange={setFilter} accounts={[]} currency="USD" tags={TAGS} />;
    }

    const PRESS = [
        { how: 'Enter', press: async (user: ReturnType<typeof userEvent.setup>, el: HTMLElement) => {
            act(() => el.focus());
            await user.keyboard('{Enter}');
        } },
        { how: 'a click', press: (user: ReturnType<typeof userEvent.setup>, el: HTMLElement) => user.click(el) },
    ];

    it.each(PRESS)('moves focus to the tag field when the tag is cleared with $how, and the popover still closes on Escape', async ({ press }) => {
        const user = userEvent.setup();
        render(<TagFilter initial={{ tag: 'Tag A' }} />);
        const toggle = screen.getByRole('button', { name: /^Filter/ });
        await user.click(toggle);

        await press(user, screen.getByRole('button', { name: 'Clear tag filter Tag A' }));

        const field = screen.getByRole('combobox', { name: 'Filter by tag' });
        expect(field).toHaveFocus();
        // Focused, the field shows its suggestions: the first Escape closes them, the next
        // the popover.
        expect(screen.getByRole('listbox')).toBeInTheDocument();
        await user.keyboard('{Escape}');
        expect(screen.queryByRole('listbox')).toBeNull();
        expect(field).toHaveFocus();
        await user.keyboard('{Escape}');
        expect(screen.queryByRole('combobox', { name: 'Filter by tag' })).toBeNull();
        expect(toggle).toHaveFocus();
    });

    it('moves focus to the clear button when a tag is picked with Enter', async () => {
        const user = userEvent.setup();
        render(<TagFilter initial={{}} />);
        await user.click(screen.getByRole('button', { name: /^Filter/ }));
        act(() => screen.getByRole('combobox', { name: 'Filter by tag' }).focus());

        await user.keyboard('Tag{Enter}');

        const clear = screen.getByRole('button', { name: 'Clear tag filter Tag A' });
        expect(clear).toHaveFocus();
        // Focus moved there from the keyboard, so it shows: jsdom draws no ring, so this pins
        // the classes that do.
        expect(clear).toHaveClass('focus-visible:ring-2', 'focus-visible:ring-accent');
    });

    it('moves focus to the clear button when a suggestion is clicked', async () => {
        const user = userEvent.setup();
        render(<TagFilter initial={{}} />);
        await user.click(screen.getByRole('button', { name: /^Filter/ }));
        await user.click(screen.getByRole('combobox', { name: 'Filter by tag' }));

        await user.click(screen.getByRole('button', { name: /^Tag A/ }));

        expect(screen.getByRole('button', { name: 'Clear tag filter Tag A' })).toHaveFocus();
    });
});

// A filter change resets the register's selection (ADR-0024), and the register
// re-reads its rows on a new filter object. An edit that leaves the filter as it
// was is no change, so the Filter popover hands up nothing for it: typing in the
// security search before the text names a security, or picking the category
// the filter already has.

const LEDGER_ID = '00000000-0000-0000-0000-000000000010';

const CATEGORY: AccountSummary = {
    id: '00000000-0000-0000-0000-000000000301',
    ledgerId: LEDGER_ID,
    parentId: null,
    name: 'Category One',
    accountType: 'category',
    categoryKind: 'expense',
    currencyCode: 'USD',
    isActive: true,
    isSystem: false,
    feedConnectionId: null,
    needsReviewCount: 0,
    holdingsAccountId: null,
    isTradeCommission: false,
};

const SECURITY: SecuritySummary = {
    id: '00000000-0000-0000-0000-000000000701',
    ticker: 'AAA',
    cusip: null,
    name: 'Synthetic Fund',
    assetClass: null,
    exchange: null,
    isActive: true,
    totalQuantity: 0,
    latestPrice: null,
    latestPriceAsOf: null,
    priceCurrencyCode: 'USD',
};

/** A register in miniature: the filter the popover edits, and the selection
 *  that resets when the filter changes. */
function Register({ initial, securities }: { initial: RegisterFilterArgs; securities?: SecuritySummary[] }) {
    const [filter, setFilter] = useState<RegisterFilterArgs>(initial);
    const [changes, setChanges] = useState(0);
    const selection = useSelection({ ledgerId: LEDGER_ID, accountId: 'account-1', statusFilter: 'all', filter });
    const selected = selection.selection.kind === 'explicit' ? selection.selection.headerIds.length : -1;
    return (
        <div>
            <button type="button" onClick={() => selection.setExplicit(['header-1'])}>Select a row</button>
            <output aria-label="Selected rows">{selected}</output>
            <output aria-label="Filter changes">{changes}</output>
            <RegisterFilterControls
                filter={filter}
                onChange={(next) => { setFilter(next); setChanges((n) => n + 1); }}
                accounts={[CATEGORY]}
                currency="USD"
                tags={[]}
                securities={securities}
            />
        </div>
    );
}

const selectedRows = () => screen.getByRole('status', { name: 'Selected rows' }).textContent;
const filterChanges = () => screen.getByRole('status', { name: 'Filter changes' }).textContent;

async function selectARowAndOpenFilter(user: ReturnType<typeof userEvent.setup>) {
    vi.spyOn(apiModule, 'fetchSelectionSummary')
        .mockResolvedValue({ count: 1, totalCount: 1, readOnlyCount: 0, sumOnAccount: 0, currencyCode: 'USD' });
    await user.click(screen.getByRole('button', { name: 'Select a row' }));
    expect(selectedRows()).toBe('1');
    await user.click(screen.getByRole('button', { name: /^Filter/ }));
}

describe('RegisterFilterControls and the selection', () => {
    it('keeps the selection while the security search names no security, and resets it on a pick', async () => {
        const user = userEvent.setup();
        render(<Register initial={{}} securities={[SECURITY]} />);
        await selectARowAndOpenFilter(user);

        await user.type(screen.getByRole('combobox', { name: 'Security' }), 'A');

        expect(filterChanges()).toBe('0');
        expect(selectedRows()).toBe('1');

        // A real change still resets it.
        await user.click(screen.getByRole('option', { name: 'AAA · Synthetic Fund' }));

        expect(filterChanges()).toBe('1');
        expect(selectedRows()).toBe('0');
    });

    it('keeps the selection when the category the filter already has is picked again', async () => {
        const user = userEvent.setup();
        render(<Register initial={{ categoryId: CATEGORY.id }} />);
        await selectARowAndOpenFilter(user);

        await user.click(screen.getByRole('combobox', { name: 'Category' }));
        await user.click(screen.getByRole('option', { name: 'Category One' }));

        expect(screen.getByRole('combobox', { name: 'Category' })).toHaveValue('Category One');
        expect(filterChanges()).toBe('0');
        expect(selectedRows()).toBe('1');
    });
});

/**
 * The mouse going down on `target`, as a browser dispatches it.
 *
 * A browser runs microtasks after every listener it calls, and React renders a
 * discrete update in one, so a handler that removes the node pressed has done
 * so before any listener on the document hears the press. jsdom calls every
 * listener in one stretch of script with no microtasks between them, so each
 * still sees the page as it was. React's work is flushed here where a browser
 * would do it: on the body, after the app's root (which sits inside it) and
 * before the document. Outside act, which would hold that work back until the
 * end.
 */
function mouseDownAsABrowserDoes(target: Element): void {
    const env = globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean };
    const wasActEnvironment = env.IS_REACT_ACT_ENVIRONMENT;
    const flush = () => flushSync(() => {});
    env.IS_REACT_ACT_ENVIRONMENT = false;
    document.body.addEventListener('pointerdown', flush);
    document.body.addEventListener('mousedown', flush);
    try {
        const init = { bubbles: true, cancelable: true, composed: true, button: 0, buttons: 1 };
        const pointer = new PointerEvent('pointerdown', { ...init, pointerType: 'mouse', isPrimary: true });
        // A cancelled pointerdown sends no mousedown (Pointer Events).
        if (target.dispatchEvent(pointer)) target.dispatchEvent(new MouseEvent('mousedown', init));
        flush();
    } finally {
        document.body.removeEventListener('pointerdown', flush);
        document.body.removeEventListener('mousedown', flush);
        env.IS_REACT_ACT_ENVIRONMENT = wasActEnvironment;
    }
}

// The picker commits a pick as the mouse goes down and takes its list away, so
// the option pressed has left the page before the popover's own listener hears
// the press. It is still a press inside the popover (ADR-0023 §L): the pick
// applies and the popover stays open, as it does for a pick made with Enter.
describe('RegisterFilterControls, a mouse pick in the category picker', () => {
    it('applies the pick and keeps the popover open', async () => {
        const user = userEvent.setup();
        render(<Register initial={{}} />);
        await user.click(screen.getByRole('button', { name: /^Filter/ }));
        await user.click(screen.getByRole('combobox', { name: 'Category' }));
        const option = screen.getByRole('option', { name: 'Category One' });

        mouseDownAsABrowserDoes(option);

        // The press removed the option it landed on, which is the case at issue.
        expect(option.isConnected).toBe(false);
        expect(filterChanges()).toBe('1');
        expect(screen.getByRole('combobox', { name: 'Category' })).toHaveValue('Category One');
        expect(screen.getByRole('button', { name: /^Filter/ })).toHaveAttribute('aria-expanded', 'true');
    });
});

// An account register's amount range is in the account's currency: its inputs show the
// symbol and round at its places, and its chip reads each bound as the account's
// amounts read. A category register's rows can be in different currencies, so its
// range carries no symbol and no places (ADR-0114 D14).
describe('the amount range\'s currency', () => {
    function Range({ currency, initial = {} }: { currency: string | null; initial?: RegisterFilterArgs }) {
        const [filter, setFilter] = useState<RegisterFilterArgs>(initial);
        const search = createRef<HTMLInputElement>();
        return (
            <>
                <RegisterFilterControls filter={filter} onChange={setFilter} accounts={[]} currency={currency} tags={[]} />
                <RegisterFilterChips
                    filter={filter} onChange={setFilter} accounts={[]} currency={currency}
                    resultCount={null} fallbackFocusRef={search}
                />
            </>
        );
    }
    const openRange = (currency: string | null, initial?: RegisterFilterArgs) => {
        render(<Range currency={currency} initial={initial} />);
        fireEvent.click(screen.getByRole('button', { name: /^Filter/ }));
        // By role: the symbol inside each field is hidden from its name.
        return {
            min: screen.getByRole('spinbutton', { name: 'Amount min' }) as HTMLInputElement,
            max: screen.getByRole('spinbutton', { name: 'Amount max' }) as HTMLInputElement,
        };
    };
    const affixOf = (input: HTMLElement) =>
        input.parentElement!.querySelector('[data-money-affix]')?.textContent ?? null;
    const typeAndLeave = (input: HTMLElement, text: string) => {
        fireEvent.focus(input);
        fireEvent.change(input, { target: { value: text } });
        fireEvent.blur(input);
    };
    const chip = () => screen.getByTitle('Remove filter').textContent;

    it('shows the account\'s symbol on both bounds and formats both ends of the chip', () => {
        const { min, max } = openRange('JPY', { amountMin: 10, amountMax: 2500 });
        expect(affixOf(min)).toBe(currencyAffix('JPY')!.symbol);
        expect(affixOf(max)).toBe(currencyAffix('JPY')!.symbol);
        expect(chip()).toContain(`Amount: ${formatCurrency(10, 'JPY')} – ${formatCurrency(2500, 'JPY')}`);
    });

    it('keeps the chip\'s open end', () => {
        openRange('USD', { amountMin: 10 });
        expect(chip()).toContain(`Amount: ${formatCurrency(10, 'USD')} – ∞`);
    });

    it('shows no symbol on a category register, in the inputs or the chip', () => {
        const { min, max } = openRange(null, { amountMin: 10, amountMax: 2500 });
        expect(affixOf(min)).toBeNull();
        expect(affixOf(max)).toBeNull();
        expect(chip()).toContain('Amount: 10 – 2500');
    });

    it('rounds a typed bound at the account\'s places on blur, and nothing with no currency', () => {
        const { min } = openRange('JPY');
        typeAndLeave(min, '12.5');
        expect(min.value).toBe('13');
        cleanup();

        const loose = openRange(null).min;
        typeAndLeave(loose, '12.345');
        expect(loose.value).toBe('12.345');
    });
});
