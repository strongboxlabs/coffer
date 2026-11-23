import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { StructureGroupHeading } from './StructureGroupHeading';

// The heading over a group of rows on Structure's lists (ADR-0111 D4): one part for the accounts
// of a type and the categories of a kind, so the two read alike.

describe('StructureGroupHeading', () => {
    it('says the label and how many rows, the same way wherever it is used', () => {
        render(<StructureGroupHeading label="Banking" count={3} />);

        const heading = screen.getByRole('heading', { level: 3 });
        expect(heading).toHaveTextContent('Banking(3)');
        expect(screen.queryByRole('button')).not.toBeInTheDocument();
    });

    it('is plain with no disclosure: nothing to fold, so no button and no state', () => {
        render(<StructureGroupHeading label="Credit cards" count={1} />);

        // The heading is there, and it is not a toggle.
        const heading = screen.getByRole('heading', { level: 3, name: /^Credit cards\s*\(1\)$/ });
        expect(screen.queryByRole('button')).not.toBeInTheDocument();
        expect(heading.querySelector('[aria-expanded]')).toBeNull();
    });

    it('becomes the toggle of what it folds, and says whether it is open', async () => {
        const onToggle = vi.fn();
        render(
            <StructureGroupHeading
                label="Income" count={12}
                disclosure={{ expanded: false, onToggle, controls: 'income-body' }}
            />,
        );

        const toggle = screen.getByRole('button', { name: /Income/ });
        expect(toggle).toHaveAttribute('aria-expanded', 'false');
        expect(toggle).toHaveAttribute('aria-controls', 'income-body');
        // The button is inside the heading, so a reader who moves by headings finds the group too.
        expect(screen.getByRole('heading', { level: 3 })).toContainElement(toggle);

        await userEvent.click(toggle);
        expect(onToggle).toHaveBeenCalledTimes(1);
    });

    it('cannot be folded while something else is holding the group open', async () => {
        const onToggle = vi.fn();
        render(
            <StructureGroupHeading
                label="Expense" count={4}
                disclosure={{ expanded: true, onToggle, controls: 'expense-body', disabled: true }}
            />,
        );

        const toggle = screen.getByRole('button', { name: /Expense/ });
        expect(toggle).toBeDisabled();
        await userEvent.click(toggle);
        expect(onToggle).not.toHaveBeenCalled();
    });

    it('puts a figure beside the heading, outside the toggle, so reading it does not fold the group', () => {
        render(
            <StructureGroupHeading
                label="Expense" count={2}
                total={{ text: '$1,234.00', title: 'Total expense' }}
                disclosure={{ expanded: true, onToggle: vi.fn(), controls: 'expense-body' }}
            />,
        );

        const figure = screen.getByText('$1,234.00');
        expect(figure).toHaveAttribute('title', 'Total expense');
        expect(screen.getByRole('button', { name: /Expense/ })).not.toContainElement(figure);
    });

    it('draws a mixed total as the rail\'s marker, muted, and a figure in full', () => {
        render(
            <>
                <StructureGroupHeading label="Expense" count={2} total={{ text: 'mixed', mixed: true }} />
                <StructureGroupHeading label="Income" count={1} total={{ text: '$1,234.00' }} />
            </>,
        );

        const marker = screen.getByText('mixed');
        expect(marker).toHaveClass('text-text-subtle');
        expect(marker).not.toHaveClass('text-text');
        const figure = screen.getByText('$1,234.00');
        expect(figure).toHaveClass('text-text');
        expect(figure).not.toHaveClass('text-text-subtle');
    });

    it('draws a leading mark inside the toggle, so it is part of what is clicked', () => {
        render(
            <StructureGroupHeading
                label="Income" count={1}
                icon={<svg data-testid="mark" aria-hidden />}
                disclosure={{ expanded: true, onToggle: vi.fn(), controls: 'income-body' }}
            />,
        );

        expect(screen.getByRole('button', { name: /Income/ })).toContainElement(screen.getByTestId('mark'));
    });
});
