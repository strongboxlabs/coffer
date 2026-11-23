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
//   * Escape with the list showing closes it and stops there (handled,
//     not bubbled), keeping focus and text; with no list showing it
//     bubbles to the parent form, whose Escape cancels the row.
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
    { id: '1', label: 'Online Store' },
    { id: '2', label: 'Food Market' },
    { id: '3', label: 'Warehouse Club' },
];

describe('Typeahead', () => {
    it('filters by case-insensitive substring', async () => {
        render(<Harness items={ITEMS} />);
        const user = userEvent.setup();

        await user.type(screen.getByRole('combobox'), 'foo');
        expect(screen.getByRole('option', { name: /food market/i })).toBeInTheDocument();
        // 'foo' substring only matches Food Market.
        expect(screen.queryByRole('option', { name: /online store/i })).not.toBeInTheDocument();
    });

    it('Enter with a highlight commits via onChange and closes the popover', async () => {
        render(<Harness items={ITEMS} />);
        const user = userEvent.setup();
        const input = screen.getByRole('combobox');

        await user.click(input);                  // opens, highlight = 0 (Online Store)
        await user.keyboard('{ArrowDown}');       // highlight = 1 (Food Market)
        await user.keyboard('{Enter}');

        expect(screen.getByTestId('committed-value')).toHaveTextContent('Food Market');
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
        await user.click(screen.getByRole('option', { name: /warehouse club/i }));

        expect(screen.getByTestId('committed-value')).toHaveTextContent('Warehouse Club');
        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    });

    it('Escape with the list showing closes it, keeps focus and text, and stops there', async () => {
        const onParentKeyDown = vi.fn();
        render(<Harness items={ITEMS} onParentKeyDown={onParentKeyDown} />);
        const user = userEvent.setup();
        // The native event, to read how the Escape left the component.
        const seen: KeyboardEvent[] = [];
        const capture = (e: KeyboardEvent) => { if (e.key === 'Escape') seen.push(e); };
        document.addEventListener('keydown', capture, true);

        const input = screen.getByRole('combobox');
        await user.type(input, 'o');
        expect(screen.getByRole('listbox')).toBeInTheDocument();
        await user.keyboard('{Escape}');
        document.removeEventListener('keydown', capture, true);

        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
        expect(input).toHaveFocus();
        expect(input).toHaveValue('o');
        // Marked handled, for the editor and Modal that read the flag.
        expect(seen).toHaveLength(1);
        expect(seen[0]!.defaultPrevented).toBe(true);
        // And stopped: the parent saw the typing but not this Escape.
        const parentEvents = onParentKeyDown.mock.calls.map(([e]) => e.key as string);
        expect(parentEvents).toContain('o');
        expect(parentEvents).not.toContain('Escape');
    });

    it('Escape with no list showing reaches the parent, unhandled', async () => {
        const onParentKeyDown = vi.fn();
        render(<Harness items={ITEMS} onParentKeyDown={onParentKeyDown} />);
        const user = userEvent.setup();

        // Focused (the field counts itself open), but nothing matches, so no
        // list is on screen: this Escape is the form's.
        await user.type(screen.getByRole('combobox'), 'zzz');
        expect(screen.getByRole('combobox')).toHaveValue('zzz');
        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
        await user.keyboard('{Escape}');

        const escapes = onParentKeyDown.mock.calls
            .map(([e]) => e as React.KeyboardEvent)
            .filter((e) => e.key === 'Escape');
        expect(escapes).toHaveLength(1);
        expect(escapes[0]!.defaultPrevented).toBe(false);
    });

    it('announces the list as expanded only while it is on screen', async () => {
        render(<Harness items={ITEMS} />);
        const user = userEvent.setup();
        const input = screen.getByRole('combobox');

        await user.type(input, 'o');
        expect(screen.getByRole('listbox')).toBeInTheDocument();
        expect(input).toHaveAttribute('aria-expanded', 'true');

        // Still focused, but nothing matches: no list, so not expanded.
        await user.type(input, 'zzz');
        expect(input).toHaveFocus();
        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
        expect(input).toHaveAttribute('aria-expanded', 'false');
    });

    it('the Escape after the one that closed the list reaches the parent', async () => {
        const onParentKeyDown = vi.fn();
        render(<Harness items={ITEMS} onParentKeyDown={onParentKeyDown} />);
        const user = userEvent.setup();

        await user.type(screen.getByRole('combobox'), 'o');
        expect(screen.getByRole('listbox')).toBeInTheDocument();
        await user.keyboard('{Escape}');
        expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
        await user.keyboard('{Escape}');

        const escapes = onParentKeyDown.mock.calls
            .map(([e]) => e.key as string)
            .filter((k) => k === 'Escape');
        expect(escapes).toHaveLength(1);
    });

    it('Tab with a highlight commits AND lets focus advance to the next focusable', async () => {
        render(<Harness items={ITEMS} />);
        const user = userEvent.setup();

        await user.click(screen.getByRole('combobox'));   // opens, highlight = 0
        await user.tab();

        expect(screen.getByTestId('committed-value')).toHaveTextContent('Online Store');
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
