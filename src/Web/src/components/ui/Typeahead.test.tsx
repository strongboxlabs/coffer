import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';

import { Typeahead } from './Typeahead';

// Behaviour we lock down:
//
//   * Filters by case-insensitive substring against getSearchableText.
//   * ↑/↓ moves the highlight; Enter (with highlight) commits via
//     onChange and closes the popover.
//   * Enter without a highlight does NOT call onChange — the event
//     bubbles to the parent form.
//   * Clicking an item commits it via onChange.
//   * Escape closes the popover and bubbles up (parent form gets to
//     handle it for cancel-row).
//   * Tab with a highlight commits AND lets focus advance naturally
//     (no preventDefault, so the next focusable receives focus).
//   * Custom getSearchableText drives the filter (compound-path use).

interface Item { id: string; label: string; }

function Harness({
    initialValue = '',
    items,
    onParentKeyDown,
    getSearchableText,
}: {
    initialValue?: string;
    items: readonly Item[];
    onParentKeyDown?: (e: React.KeyboardEvent) => void;
    getSearchableText?: (item: Item) => string;
}) {
    const [value, setValue] = useState(initialValue);
    return (
        <div onKeyDown={onParentKeyDown}>
            <Typeahead
                items={items}
                value={value}
                onChange={setValue}
                getKey={(i) => i.id}
                getLabel={(i) => i.label}
                getSearchableText={getSearchableText}
                aria-label="Payee"
                autoFocus
            />
            <button type="button">next</button>
            <span data-testid="committed-value">{value}</span>
        </div>
    );
}

const ITEMS: Item[] = [
    { id: '1', label: 'Amazon' },
    { id: '2', label: 'Whole Foods' },
    { id: '3', label: 'Bulk Mart' },
];

describe('Typeahead', () => {
    it('filters by case-insensitive substring', async () => {
        render(<Harness items={ITEMS} />);
        const user = userEvent.setup();

        await user.type(screen.getByRole('combobox'), 'foo');
        expect(screen.getByRole('option', { name: /whole foods/i })).toBeInTheDocument();
        // 'foo' substring only matches Whole Foods.
        expect(screen.queryByRole('option', { name: /amazon/i })).not.toBeInTheDocument();
    });

    it('Enter with a highlight commits via onChange and closes the popover', async () => {
        render(<Harness items={ITEMS} />);
        const user = userEvent.setup();
        const input = screen.getByRole('combobox');

        await user.click(input);                  // opens, highlight = 0 (Amazon)
        await user.keyboard('{ArrowDown}');       // highlight = 1 (Whole Foods)
        await user.keyboard('{Enter}');

        expect(screen.getByTestId('committed-value')).toHaveTextContent('Whole Foods');
        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    });

    it('Enter with no highlight does not call onChange — event bubbles', async () => {
        const onParentKeyDown = vi.fn();
        render(<Harness items={[]} onParentKeyDown={onParentKeyDown} />);
        const user = userEvent.setup();

        await user.type(screen.getByRole('combobox'), 'New Payee');
        await user.keyboard('{Enter}');

        // onChange was called for each typed character but not by
        // Enter — the final value is what was typed.
        expect(screen.getByTestId('committed-value')).toHaveTextContent('New Payee');
        // The Enter event reaches the wrapping div.
        const parentEvents = onParentKeyDown.mock.calls.map(([e]) => e.key as string);
        expect(parentEvents).toContain('Enter');
    });

    it('clicking an option commits it via onChange', async () => {
        render(<Harness items={ITEMS} />);
        const user = userEvent.setup();

        await user.click(screen.getByRole('combobox'));   // opens
        await user.click(screen.getByRole('option', { name: /bulk mart/i }));

        expect(screen.getByTestId('committed-value')).toHaveTextContent('Bulk Mart');
        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    });

    it('Escape closes the popover and bubbles to the parent', async () => {
        const onParentKeyDown = vi.fn();
        render(<Harness items={ITEMS} onParentKeyDown={onParentKeyDown} />);
        const user = userEvent.setup();

        await user.click(screen.getByRole('combobox'));   // opens
        await user.keyboard('{Escape}');

        // Popover closed.
        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
        // Escape reached the parent — that's the cancel-row hook.
        const parentEvents = onParentKeyDown.mock.calls.map(([e]) => e.key as string);
        expect(parentEvents).toContain('Escape');
    });

    it('Tab with a highlight commits AND lets focus advance to the next focusable', async () => {
        render(<Harness items={ITEMS} />);
        const user = userEvent.setup();

        await user.click(screen.getByRole('combobox'));   // opens, highlight = 0
        await user.tab();

        expect(screen.getByTestId('committed-value')).toHaveTextContent('Amazon');
        // Focus advanced past the input to the next button.
        expect(screen.getByRole('button', { name: /next/i })).toHaveFocus();
    });

    it('uses a custom searchable text (e.g. compound account path)', async () => {
        const items = [
            { id: 'a', label: 'Groceries' },
            { id: 'b', label: 'Groceries' },
        ];
        const paths: Record<string, string> = {
            a: 'Food/Groceries',
            b: 'Household/Groceries',
        };
        const getSearchableText = (item: Item) => paths[item.id]!;

        render(<Harness items={items} getSearchableText={getSearchableText} />);
        const user = userEvent.setup();
        await user.type(screen.getByRole('combobox'), 'Household');

        const options = screen.getAllByRole('option');
        expect(options).toHaveLength(1);
    });
});
