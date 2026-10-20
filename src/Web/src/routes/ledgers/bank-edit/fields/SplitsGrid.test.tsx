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
    tags: [],
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
        allTags: [],
        counterpartyKind: 'category' as const,
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

    it('asks for an ACCOUNT on a category register, everywhere it names one', () => {
        // The third time this defect shipped. On a category's own register the
        // counterparty is a money account, and the header, the issue strip,
        // each row's screen-reader name and the per-row hint all said
        // "category" — four surfaces, none of which read the kind the editor
        // had already worked out.
        const postings = [draft({ key: 'a', counterpartyId: null })];
        renderGrid(postings, {
            counterpartyKind: 'account',
            issues: postingIssues(postings, 'account'),
        });

        expect(screen.getByText('Account · Tags')).toBeInTheDocument();
        expect(screen.getByText('Pick an account:')).toBeInTheDocument();
        expect(screen.getByLabelText('Posting 1 account')).toBeInTheDocument();
        expect(postingIssues(postings, 'account')[0]!.hint).toBe('Pick an account for this split');

        // The negative, anchored by the four positives above: none of the
        // category wording survived anywhere the grid draws.
        expect(screen.queryByText(/Category · Tags|Pick a category/)).toBeNull();
        expect(screen.queryByLabelText('Posting 1 category')).toBeNull();
    });

    it('says nothing at all when every posting is complete', () => {
        // ADR-0025: there is no balance rule. These three are all debits, so they
        // do not sum to zero, and that is not a warning, an error, or anything
        // the editor comments on.
        renderGrid();
        // Anchored on the rows themselves: the negative alone passes against a
        // grid that rendered nothing, so it could not tell "three complete
        // postings draw no message" from "the grid failed to draw".
        expect(screen.getByLabelText('Posting 3 amount')).toBeInTheDocument();
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
        // Zero is legal (paycheck splits carry zero-amount lines) and is neither a debit
        // nor a credit; a half-typed "-" must not flash red.
        renderGrid([draft({ key: 'z', amount: '0.00' }), draft({ key: 'b', amount: '' })]);
        expect(screen.getByLabelText('Posting 1 amount').className).not.toContain('text-state-danger');
        expect(screen.getByLabelText('Posting 2 amount').className).not.toContain('text-state-danger');
    });
    // ----- per-posting tags (ADR-0103) -------------------------------------
    //
    // These assert DELIVERY. The slot held a placeholder for months and every
    // test in this file stayed green throughout, which is exactly what an inert
    // control looks like from a test suite.

    it('shows each posting its own tags', () => {
        renderGrid([
            draft({ key: 'a', tags: ['Property A'] }),
            draft({ key: 'b', tags: ['Property B'] }),
        ]);

        // Within the posting's own field, not just somewhere on screen — the
        // whole point is that a tag belongs to ONE posting.
        expect(
            within(screen.getByLabelText('Posting 1 tags')).getByText('Property A'),
        ).toBeInTheDocument();
        expect(
            within(screen.getByLabelText('Posting 2 tags')).getByText('Property B'),
        ).toBeInTheDocument();
        expect(
            within(screen.getByLabelText('Posting 1 tags')).queryByText('Property B'),
        ).not.toBeInTheDocument();
    });

    it('reports a removed tag against the posting it came from', async () => {
        const props = renderGrid(
            [
                draft({ key: 'a', tags: ['Property A'] }),
                draft({ key: 'b', tags: ['Property B'] }),
            ],
            { allTags: [
                { id: 't1', name: 'Property A', color: null, usageCount: 1 },
                { id: 't2', name: 'Property B', color: null, usageCount: 1 },
            ] },
        );

        // Open THIS posting's editor and uncheck. Removal is unchecking since
        // ADR-0103 — a chip strip wraps, and a 30px leg row cannot grow.
        await userEvent.click(screen.getByRole('button', { name: 'Edit Posting 2 tags' }));
        await userEvent.click(screen.getByRole('checkbox', { name: 'Property B' }));

        // The KEY matters as much as the value: patching the wrong row is the
        // failure mode a single-posting test cannot see.
        expect(props.onPatch).toHaveBeenCalledWith('b', { tags: [] });
    });

    it('adds a tag to the posting whose editor is open', async () => {
        const props = renderGrid(
            [draft({ key: 'a', tags: [] }), draft({ key: 'b', tags: [] })],
            { allTags: [{ id: 't1', name: 'Property A', color: null, usageCount: 0 }] },
        );

        await userEvent.click(screen.getByRole('button', { name: 'Edit Posting 2 tags' }));
        await userEvent.click(screen.getByRole('checkbox', { name: 'Property A' }));

        expect(props.onPatch).toHaveBeenCalledWith('b', { tags: ['Property A'] });
    });

    it('cannot be edited when the form is disabled', () => {
        // Disabled has to reach the tag control too, or a read-only viewer can
        // edit a posting's tags from a form that refuses every other change.
        renderGrid([draft({ key: 'a', tags: ['Property A'] })], { disabled: true });
        expect(screen.getByRole('button', { name: 'Edit Posting 1 tags' })).toBeDisabled();
    });
});
