import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { SplitsGrid } from './SplitsGrid';
import { postingIssues } from '../validation';
import type { PostingDraft } from '../postingDraft';

/**
 * The splits grid's own affordances — the ones the redesign added, which by
 * definition nothing else in the repo covers.
 *
 * Rendered directly rather than through TxnRowEdit: the grid takes plain props
 * and no queries, so a direct render is both faster and honest about the
 * boundary. What the shell does with it stays pinned in
 * ../../TxnRowEdit.characterisation.test.tsx.
 */

const draft = (over: Partial<PostingDraft> = {}): PostingDraft => ({
    key: 'k1',
    legId: null,
    counterpartyId: 'cat-1',
    amount: '-12.50',
    legMemo: '',
    ...over,
});

const threeSplits: readonly PostingDraft[] = [
    draft({ key: 'a', amount: '-10.00' }),
    draft({ key: 'b', amount: '-20.00' }),
    draft({ key: 'c', amount: '-30.00' }),
];

function renderGrid(
    postings: readonly PostingDraft[] = threeSplits,
    over: Partial<Parameters<typeof SplitsGrid>[0]> = {},
) {
    const props = {
        postings,
        issues: postingIssues(postings),
        accounts: [],
        isEligibleCounterparty: () => true,
        frequent: null,
        currency: 'USD',
        total: -60,
        disabled: false,
        focusKey: null,
        onAutoFocused: vi.fn(),
        onPatch: vi.fn(),
        onAdd: vi.fn(),
        onRemove: vi.fn(),
        onReorder: vi.fn(),
        onMove: vi.fn(),
        ...over,
    };
    render(<SplitsGrid {...props} />);
    return props;
}

describe('SplitsGrid — the row numbers', () => {
    it('numbers every row from 1, so a message can name one', () => {
        // The defect the numbers close: validation said "Posting 4" against
        // rows that carried no number, so matching a clause to a row meant
        // counting from the top of a list that scrolls.
        renderGrid();
        for (const n of [1, 2, 3]) {
            expect(screen.getByLabelText(`Posting ${n} amount`)).toBeInTheDocument();
            expect(screen.getByLabelText(`Actions for posting ${n}`)).toBeInTheDocument();
        }
        expect(screen.queryByLabelText('Posting 4 amount')).toBeNull();
    });

    it('shows the running total and the split count in the footer', () => {
        renderGrid();
        expect(screen.getByText('3 splits')).toBeInTheDocument();
        expect(screen.getByTitle(/Sum of postings/)).toHaveTextContent('60.00');
    });
});

describe('SplitsGrid — reorder without a drag', () => {
    it('moves a row one slot on Alt+ArrowUp / Alt+ArrowDown', async () => {
        // The whole point of the keyboard path: there is no drag on touch and
        // none at all from a keyboard, so a drag-only reorder is unreachable
        // for anyone not using a mouse.
        const props = renderGrid();
        const amount2 = screen.getByLabelText('Posting 2 amount');

        await userEvent.type(amount2, '{Alt>}{ArrowUp}{/Alt}');
        expect(props.onMove).toHaveBeenCalledWith('b', -1);

        await userEvent.type(amount2, '{Alt>}{ArrowDown}{/Alt}');
        expect(props.onMove).toHaveBeenCalledWith('b', 1);
    });

    it('leaves a bare arrow key alone', async () => {
        // Without the Alt guard the shortcut would eat caret movement inside
        // the number input it is typed in.
        const props = renderGrid();
        await userEvent.type(screen.getByLabelText('Posting 2 amount'), '{ArrowUp}');
        expect(props.onMove).not.toHaveBeenCalled();
    });

    it('offers Move up / Move down in the row menu, bounded at the ends', async () => {
        renderGrid();

        await userEvent.click(screen.getByLabelText('Actions for posting 1'));
        const first = within(screen.getByRole('menu'));
        // Row 1 cannot move up and row 3 cannot move down. Rendered disabled
        // rather than omitted so the menu keeps the same shape on every row —
        // a menu whose items shift position by row is one you have to read.
        // `disabled` is the native attribute, which ContextMenu also skips in
        // arrow-key navigation, so the state is real and not just dimmed.
        expect(first.getByRole('menuitem', { name: /move up/i })).toBeDisabled();
        expect(first.getByRole('menuitem', { name: /move down/i })).toBeEnabled();
    });

    it('moves the row when Move down is chosen', async () => {
        const props = renderGrid();
        await userEvent.click(screen.getByLabelText('Actions for posting 1'));
        await userEvent.click(screen.getByRole('menuitem', { name: /move down/i }));
        expect(props.onMove).toHaveBeenCalledWith('a', 1);
    });
});

describe('SplitsGrid — remove', () => {
    it('removes through the row menu', async () => {
        const props = renderGrid();
        await userEvent.click(screen.getByLabelText('Actions for posting 2'));
        await userEvent.click(screen.getByRole('menuitem', { name: /remove split/i }));
        expect(props.onRemove).toHaveBeenCalledWith('b');
    });

    it('will not remove the only posting', async () => {
        // The grid is not the enforcement point — the draft hook refuses too —
        // but an enabled control that does nothing is worse than a disabled one.
        renderGrid([draft({ key: 'solo' })]);
        await userEvent.click(screen.getByLabelText('Actions for posting 1'));
        expect(screen.getByRole('menuitem', { name: /remove split/i })).toBeDisabled();
    });
});

describe('SplitsGrid — defects', () => {
    it('marks the offending row and groups the summary by what is wrong', () => {
        renderGrid([
            draft({ key: 'a', amount: '-10.00' }),
            draft({ key: 'b', amount: '' }),
            draft({ key: 'c', counterpartyId: null }),
            draft({ key: 'd', amount: '' }),
        ]);

        // The field itself is marked, not just a sentence at the bottom.
        expect(screen.getByLabelText('Posting 2 amount')).toHaveAttribute('aria-invalid', 'true');
        expect(screen.getByLabelText('Posting 1 amount')).not.toHaveAttribute('aria-invalid');

        // One clause per KIND, with the row numbers as the work queue —
        // rather than one clause per defect, which wrapped to three lines.
        expect(screen.getByText('Enter an amount:')).toBeInTheDocument();
        expect(screen.getByText('Pick a category:')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /Posting 2: amount is required/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /Posting 4: amount is required/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /Posting 3: pick a counterparty/i })).toBeInTheDocument();
    });

    it('says nothing at all when every posting is complete', () => {
        // ADR-0025: there is no balance rule. These three sum to -60 and that
        // is not a warning, an error, or anything the editor comments on.
        renderGrid();
        expect(screen.queryByRole('alert')).toBeNull();
    });
});

describe('SplitsGrid — add', () => {
    it('adds through the footer button', async () => {
        const props = renderGrid();
        await userEvent.click(screen.getByLabelText('Add another posting'));
        expect(props.onAdd).toHaveBeenCalledTimes(1);
    });

    it('focuses the amount of the row the parent marks, once', async () => {
        // How the add lands the caret. `focusKey` is cleared by the callback,
        // so a later re-render must not steal focus back.
        const onAutoFocused = vi.fn();
        renderGrid(threeSplits, { focusKey: 'c', onAutoFocused });
        expect(screen.getByLabelText('Posting 3 amount')).toHaveFocus();
        expect(onAutoFocused).toHaveBeenCalledTimes(1);
    });
});

describe('SplitsGrid — amount colour matches the register it opens inside', () => {
    // The editor visually REPLACES bank register rows, so the same figure must
    // not change colour just because the row was opened. bankRowStrategy paints
    // a debit red and a credit plain on the main row, the split parent and the
    // leg row alike; this grid has to agree with it.
    it('paints a debit red and a credit plain', () => {
        renderGrid([
            draft({ key: 'out', amount: '-192.00' }),
            draft({ key: 'in', amount: '2400.00' }),
        ]);
        expect(screen.getByLabelText('Posting 1 amount').className).toContain('text-state-danger');
        expect(screen.getByLabelText('Posting 2 amount').className).toContain('text-text');
        expect(screen.getByLabelText('Posting 2 amount').className).not.toContain('text-state-success');
    });

    it('leaves zero and a blank amount uncoloured', () => {
        // Zero is legal (paycheck splits carry $0 lines) and is neither a debit
        // nor a credit; a half-typed "-" must not flash red.
        renderGrid([draft({ key: 'z', amount: '0.00' }), draft({ key: 'b', amount: '' })]);
        expect(screen.getByLabelText('Posting 1 amount').className).not.toContain('text-state-danger');
        expect(screen.getByLabelText('Posting 2 amount').className).not.toContain('text-state-danger');
    });
});
