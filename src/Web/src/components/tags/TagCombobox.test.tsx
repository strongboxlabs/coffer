import { describe, it, expect, onTestFinished, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { TagCombobox } from './TagCombobox';
import type { TagDto } from '@/lib/types';

// The shared single-tag autocomplete. Covers: draft filtering (case-
// insensitive) + transaction-count display, commit-on-click, commit-on-Enter,
// the "Create" row gating (allowCreate), and excludeNames hiding applied tags.

// "work" is tagged on a transaction and on one of its postings, so the stored
// pairings (7) are not the transactions (5): the two figures differ on purpose,
// so a test can tell which one the list shows.
const TAGS: TagDto[] = [
    { id: 't1', name: 'work', color: '#3b82f6', usageCount: 7, transactionCount: 5 },
    { id: 't2', name: 'home', color: null, usageCount: 2, transactionCount: 2 },
    { id: 't3', name: 'Wolf', color: null, usageCount: 1, transactionCount: 1 },
];

describe('TagCombobox', () => {
    it('filters suggestions case-insensitively and shows how many transactions carry each', async () => {
        const user = userEvent.setup();
        render(<TagCombobox tags={TAGS} onCommit={vi.fn()} aria-label="Add tag" />);

        await user.click(screen.getByRole('combobox'));
        await user.type(screen.getByRole('combobox'), 'wo');

        // "work" + "Wolf" match "wo"; "home" doesn't.
        expect(screen.getByText('work')).toBeInTheDocument();
        expect(screen.getByText('Wolf')).toBeInTheDocument();
        expect(screen.queryByText('home')).not.toBeInTheDocument();
        // The transactions, not the stored pairings.
        expect(screen.getByText('5')).toBeInTheDocument();
        expect(screen.queryByText('7')).not.toBeInTheDocument();
    });

    it('lists every matching tag, so a long dictionary can be scrolled to its end', async () => {
        const many: TagDto[] = Array.from({ length: 40 }, (_, i) => ({
            id: `m${i}`, name: `Tag ${String(i).padStart(2, '0')}`, color: null, usageCount: 1, transactionCount: 1,
        }));
        const user = userEvent.setup();
        render(<TagCombobox tags={many} onCommit={vi.fn()} aria-label="Add tag" />);

        await user.click(screen.getByRole('combobox'));

        expect(screen.getAllByRole('option')).toHaveLength(40);
    });

    it('commits an existing tag by click with its stored casing', async () => {
        const onCommit = vi.fn();
        const user = userEvent.setup();
        render(<TagCombobox tags={TAGS} onCommit={onCommit} aria-label="Add tag" />);

        await user.type(screen.getByRole('combobox'), 'wol');
        await user.click(screen.getByText('Wolf'));

        expect(onCommit).toHaveBeenCalledWith('Wolf');
    });

    it('commits the highlighted suggestion on Enter', async () => {
        const onCommit = vi.fn();
        const user = userEvent.setup();
        render(<TagCombobox tags={TAGS} onCommit={onCommit} aria-label="Add tag" />);

        await user.type(screen.getByRole('combobox'), 'home{Enter}');

        expect(onCommit).toHaveBeenCalledWith('home');
    });

    it('offers a Create row for a new name and commits the raw draft', async () => {
        const onCommit = vi.fn();
        const user = userEvent.setup();
        render(<TagCombobox tags={TAGS} onCommit={onCommit} aria-label="Add tag" />);

        await user.type(screen.getByRole('combobox'), 'travel');
        await user.click(screen.getByText(/Create/));

        expect(onCommit).toHaveBeenCalledWith('travel');
    });

    it('suppresses the Create row when allowCreate is false', async () => {
        const onCommit = vi.fn();
        const user = userEvent.setup();
        render(
            <TagCombobox tags={TAGS} allowCreate={false} onCommit={onCommit} aria-label="Filter by tag" />,
        );

        await user.type(screen.getByRole('combobox'), 'zzz');
        expect(screen.queryByText(/Create/)).not.toBeInTheDocument();

        // Enter on a non-matching draft does nothing (filter-only mode).
        await user.type(screen.getByRole('combobox'), '{Enter}');
        expect(onCommit).not.toHaveBeenCalled();
    });

    it('hides already-applied tags via excludeNames', async () => {
        const user = userEvent.setup();
        render(
            <TagCombobox tags={TAGS} excludeNames={['work']} onCommit={vi.fn()} aria-label="Add tag" />,
        );

        await user.click(screen.getByRole('combobox'));
        expect(screen.queryByText('work')).not.toBeInTheDocument();
        expect(screen.getByText('home')).toBeInTheDocument();
    });
});

describe('TagCombobox Escape', () => {
    // The rule every list follows: with the suggestions showing, Escape closes
    // them and nothing else, marked handled and stopped; with none showing it
    // is not this control's, and goes on unmarked to whatever is further out.
    function renderInParent(props: Partial<Parameters<typeof TagCombobox>[0]> = {}) {
        const parentEscapes: { defaultPrevented: boolean }[] = [];
        // The native event, taken by a capture listener before anything
        // handles it and read once it is done; and the Escapes that went on
        // past the React tree to the window.
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
        render(
            <div
                onKeyDown={(e) => {
                    if (e.key === 'Escape') parentEscapes.push({ defaultPrevented: e.defaultPrevented });
                }}
            >
                <TagCombobox tags={TAGS} onCommit={vi.fn()} aria-label="Filter by tag" {...props} />
            </div>,
        );
        return { parentEscapes, windowEscapes, marks: () => escapes.map((e) => e.defaultPrevented) };
    }

    it('closes the suggestions on Escape, marked and stopped, and keeps focus and the text', async () => {
        const user = userEvent.setup();
        const { parentEscapes, windowEscapes, marks } = renderInParent();
        const input = screen.getByRole('combobox', { name: 'Filter by tag' });
        await user.type(input, 'wo');
        expect(screen.getByRole('listbox')).toBeInTheDocument();

        await user.keyboard('{Escape}');

        expect(screen.queryByRole('listbox')).toBeNull();
        expect(input).toHaveFocus();
        expect(input).toHaveValue('wo');
        expect(marks()).toEqual([true]);
        expect(parentEscapes).toEqual([]);
        expect(windowEscapes).toEqual([]);
    });

    it('leaves the next Escape, with the suggestions closed, to whatever is further out', async () => {
        const user = userEvent.setup();
        const { parentEscapes, windowEscapes, marks } = renderInParent();
        await user.type(screen.getByRole('combobox', { name: 'Filter by tag' }), 'wo');
        await user.keyboard('{Escape}');

        await user.keyboard('{Escape}');

        expect(marks()).toEqual([true, false]);
        expect(parentEscapes).toEqual([{ defaultPrevented: false }]);
        expect(windowEscapes).toHaveLength(1);
    });

    it('leaves an Escape alone when nothing matches, so no list is showing', async () => {
        const user = userEvent.setup();
        const { parentEscapes, marks } = renderInParent({ allowCreate: false });
        await user.type(screen.getByRole('combobox', { name: 'Filter by tag' }), 'zzz');
        expect(screen.queryByRole('listbox')).toBeNull();

        await user.keyboard('{Escape}');

        expect(marks()).toEqual([false]);
        expect(parentEscapes).toEqual([{ defaultPrevented: false }]);
    });
});
