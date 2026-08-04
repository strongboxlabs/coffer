import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { TagCombobox } from './TagCombobox';
import type { TagDto } from '@/lib/types';

// The shared single-tag autocomplete. Covers: draft filtering (case-
// insensitive) + usage display, commit-on-click, commit-on-Enter, the
// "Create" row gating (allowCreate), and excludeNames hiding applied tags.

const TAGS: TagDto[] = [
    { id: 't1', name: 'work', color: '#3b82f6', usageCount: 5 },
    { id: 't2', name: 'home', color: null, usageCount: 2 },
    { id: 't3', name: 'Wolf', color: null, usageCount: 1 },
];

describe('TagCombobox', () => {
    it('filters suggestions case-insensitively and shows usage counts', async () => {
        const user = userEvent.setup();
        render(<TagCombobox tags={TAGS} onCommit={vi.fn()} aria-label="Add tag" />);

        await user.click(screen.getByRole('combobox'));
        await user.type(screen.getByRole('combobox'), 'wo');

        // "work" + "Wolf" match "wo"; "home" doesn't.
        expect(screen.getByText('work')).toBeInTheDocument();
        expect(screen.getByText('Wolf')).toBeInTheDocument();
        expect(screen.queryByText('home')).not.toBeInTheDocument();
        // Usage count rendered for a match.
        expect(screen.getByText('5')).toBeInTheDocument();
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
