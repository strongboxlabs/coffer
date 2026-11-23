import { useState } from 'react';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { stubFetch } from '@/lib/annotations/fetchStub';
import type { SecondAmountSide } from '@/lib/secondAmount';

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
    tagBaseline: 0,
    stored: null,
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
        currencyOf: () => 'USD',
        sideOf: () => null,
        ledgerId: 'ledger-1',
        date: '2026-11-12',
        total: -60,
        pending: false,
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
        // Validation names "Posting 4"; without a number on each row, matching a
        // clause to a row would mean counting from the top of a list that scrolls.
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

    // ADR-0023 §L: Remove split takes its leg and kebab away, the menu with
    // them (drawn inside the leg); focus goes to the next leg's kebab, else
    // the previous one's, never to the page.
    function Stateful({ initial }: { initial: readonly PostingDraft[] }) {
        const [postings, setPostings] = useState(initial);
        return (
            <SplitsGrid
                postings={postings}
                issues={postingIssues(postings)}
                accounts={[]}
                isEligibleCounterparty={() => true}
                frequent={null}
                allTags={[]}
                counterpartyKind="category"
                currency="USD"
                currencyOf={() => 'USD'}
                sideOf={() => null}
                ledgerId="ledger-1"
                date="2026-11-12"
                total={-60}
                pending={false}
                focusKey={null}
                onAutoFocused={() => {}}
                onPatch={() => {}}
                onAdd={() => {}}
                onRemove={(key) => setPostings((ps) => ps.filter((p) => p.key !== key))}
                onReorder={() => {}}
                onMove={() => {}}
            />
        );
    }

    it('gives focus to the next leg\'s kebab when Remove split is chosen by keyboard', async () => {
        const user = userEvent.setup();
        render(<Stateful initial={threeSplits} />);
        const next = screen.getByLabelText('Actions for posting 3');

        screen.getByLabelText('Actions for posting 2').focus();
        await user.keyboard('{Enter}');
        // Remove split is the menu's last item; ArrowUp wraps to it.
        await user.keyboard('{ArrowUp}{Enter}');

        expect(screen.getByText('2 splits')).toBeInTheDocument();
        expect(next).toHaveFocus();
    });

    it('gives focus to the next leg\'s kebab when Remove split is clicked', async () => {
        const user = userEvent.setup();
        render(<Stateful initial={threeSplits} />);
        const next = screen.getByLabelText('Actions for posting 3');

        await user.click(screen.getByLabelText('Actions for posting 2'));
        await user.click(screen.getByRole('menuitem', { name: /remove split/i }));

        expect(screen.getByText('2 splits')).toBeInTheDocument();
        expect(next).toHaveFocus();
    });

    it('gives focus to the previous leg\'s kebab when the last leg is removed', async () => {
        const user = userEvent.setup();
        render(<Stateful initial={threeSplits} />);
        const previous = screen.getByLabelText('Actions for posting 2');

        screen.getByLabelText('Actions for posting 3').focus();
        await user.keyboard('{Enter}');
        await user.keyboard('{ArrowUp}{Enter}');

        expect(screen.getByText('2 splits')).toBeInTheDocument();
        expect(previous).toHaveFocus();
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
        // rather than one clause per defect, which wraps to three lines.
        expect(screen.getByText('Enter an amount:')).toBeInTheDocument();
        expect(screen.getByText('Pick a category:')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /Posting 2: amount is required/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /Posting 4: amount is required/i })).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /Posting 3: pick a counterparty/i })).toBeInTheDocument();
    });

    it('asks for an ACCOUNT on a category register, everywhere it names one', () => {
        // On a category's own register the counterparty is a money account, so
        // the header, the issue strip, each row's screen-reader name and the
        // per-row hint must all read the kind the editor has already worked out
        // rather than saying "category".
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
    // These assert DELIVERY. A slot holding a placeholder keeps every other
    // test in this file green, which is exactly what an inert control looks
    // like from a test suite.

    it('shows each posting its own tags', () => {
        renderGrid([
            draft({ key: 'a', tags: ['Project A'] }),
            draft({ key: 'b', tags: ['Project B'] }),
        ]);

        // Within the posting's own field, not just somewhere on screen — the
        // whole point is that a tag belongs to ONE posting.
        expect(
            within(screen.getByLabelText('Posting 1 tags')).getByText('Project A'),
        ).toBeInTheDocument();
        expect(
            within(screen.getByLabelText('Posting 2 tags')).getByText('Project B'),
        ).toBeInTheDocument();
        expect(
            within(screen.getByLabelText('Posting 1 tags')).queryByText('Project B'),
        ).not.toBeInTheDocument();
    });

    it('reports a removed tag against the posting it came from', async () => {
        const props = renderGrid(
            [
                draft({ key: 'a', tags: ['Project A'] }),
                draft({ key: 'b', tags: ['Project B'] }),
            ],
            { allTags: [
                { id: 't1', name: 'Project A', color: null, usageCount: 1, transactionCount: 1 },
                { id: 't2', name: 'Project B', color: null, usageCount: 1, transactionCount: 1 },
            ] },
        );

        // Open THIS posting's editor and uncheck. Removal is unchecking since
        // ADR-0103 — a chip strip wraps, and a 30px leg row cannot grow.
        await userEvent.click(screen.getByRole('button', { name: 'Edit Posting 2 tags' }));
        await userEvent.click(screen.getByRole('checkbox', { name: 'Project B' }));

        // The KEY matters as much as the value: patching the wrong row is the
        // failure mode a single-posting test cannot see.
        expect(props.onPatch).toHaveBeenCalledWith('b', { tags: [] });
    });

    it('adds a tag to the posting whose editor is open', async () => {
        const props = renderGrid(
            [draft({ key: 'a', tags: [] }), draft({ key: 'b', tags: [] })],
            { allTags: [{ id: 't1', name: 'Project A', color: null, usageCount: 0, transactionCount: 0 }] },
        );

        await userEvent.click(screen.getByRole('button', { name: 'Edit Posting 2 tags' }));
        await userEvent.click(screen.getByRole('checkbox', { name: 'Project A' }));

        expect(props.onPatch).toHaveBeenCalledWith('b', { tags: ['Project A'] });
    });

    it('cannot be edited while the save is pending', async () => {
        // The legs' fields are disabled while the save is in flight, and the tag
        // control refuses to open, or a posting's tags could change under a save
        // that refuses every other change. It is pending, not disabled, so it
        // keeps focus (pending.ts).
        renderGrid([draft({ key: 'a', tags: ['Project A'] })], { pending: true });
        const edit = screen.getByRole('button', { name: 'Edit Posting 1 tags' });
        expect(edit).toHaveAttribute('aria-disabled', 'true');
        await userEvent.click(edit);
        expect(edit).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('checkbox', { name: 'Project A' })).toBeNull();
    });

    it('offers no drag on a grip that cannot be dragged, and keeps its dim while the save is pending', () => {
        renderGrid(threeSplits, { pending: true });
        const grip = screen.getByRole('button', { name: 'Reorder posting 1' });
        expect(grip).toHaveAttribute('draggable', 'false');
        expect(grip).toHaveClass('cursor-default', 'opacity-50');
        expect(grip).not.toHaveClass('cursor-grab', 'group-hover/leg:opacity-100');
    });
});

// ADR-0114 D14, board ED-5: while any line goes to a real account in another currency the
// grid gains one 6.5rem track right of Amount, captioned in the header the way Amount is;
// the header, the rows and the footer share it.
describe('SplitsGrid — the second amount\'s column', () => {
    afterEach(() => {
        vi.unstubAllGlobals();
    });

    const EUR: SecondAmountSide = {
        accountId: 'eur-bank', accountName: 'Test EUR Bank', currency: 'EUR', direction: 'received',
    };
    const lines: readonly PostingDraft[] = [
        draft({ key: 'a', counterpartyId: 'cat-1', amount: '-40.00' }),
        draft({ key: 'b', counterpartyId: 'eur-bank', amount: '-100.00', otherAmount: '90.00', otherFixed: true }),
    ];
    const sideOf = (p: PostingDraft) => (p.counterpartyId === 'eur-bank' ? EUR : null);

    function renderWithSides(postings: readonly PostingDraft[], sides = sideOf) {
        stubFetch();
        const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        const issues = postingIssues(postings, 'category', sides);
        render(
            <QueryClientProvider client={client}>
                <SplitsGrid
                    postings={postings}
                    issues={issues}
                    accounts={[]}
                    isEligibleCounterparty={() => true}
                    frequent={null}
                    allTags={[]}
                    counterpartyKind="category"
                    currency="USD"
                    currencyOf={() => 'USD'}
                    sideOf={sides}
                    ledgerId="ledger-1"
                    date="2026-11-12"
                    total={-140}
                    pending={false}
                    focusKey={null}
                    onAutoFocused={() => {}}
                    onPatch={() => {}}
                    onAdd={() => {}}
                    onRemove={() => {}}
                    onReorder={() => {}}
                    onMove={() => {}}
                />
            </QueryClientProvider>,
        );
    }

    const SIX = '2rem minmax(16rem, 2fr) minmax(12rem, 1fr) 6.5rem 6.5rem 3.5rem';
    const FIVE = '2rem minmax(16rem, 2fr) minmax(12rem, 1fr) 6.5rem 3.5rem';

    it('gains the track in the header, the rows and the footer, captioned Received', () => {
        renderWithSides(lines);
        const caption = screen.getByText('Received');
        expect(caption.parentElement).toHaveAttribute('style', expect.stringContaining(SIX));
        expect(screen.getByLabelText('Posting 1 amount').closest('.group\\/leg')).toHaveAttribute('style', expect.stringContaining(SIX));
        expect(screen.getByTitle(/Sum of postings/).parentElement).toHaveAttribute('style', expect.stringContaining(SIX));
        const field = screen.getByLabelText('Posting 2: Amount received in Test EUR Bank, in EUR');
        expect(field).toHaveValue(90);
        expect(field).toHaveAttribute('title', 'Amount received in Test EUR Bank, in EUR');
        expect(screen.getByText('1 USD = 0.9000 EUR, from the two amounts')).toBeInTheDocument();
    });

    // The header names a direction only when every line with a second amount shares it:
    // under lines going opposite ways either word would be false for one of them, so it
    // reads Other amount (ADR-0114 D14, decided by the owner).
    const GBP: SecondAmountSide = {
        accountId: 'gbp-bank', accountName: 'Test GBP Bank', currency: 'GBP', direction: 'sent',
    };
    const EUR2: SecondAmountSide = { ...EUR, accountId: 'eur-bank-2', accountName: 'Test EUR Bank 2' };
    const headerCaptionAfterAmount = () => {
        // Anchor: the header's Amount caption, then the cell right of it.
        const amount = screen.getAllByText('Amount')[0]!;
        expect(amount.parentElement).toHaveAttribute('style', expect.stringContaining(SIX));
        return amount.nextElementSibling;
    };

    it('captions the header Other amount, naming neither direction, while two lines go opposite ways', () => {
        const mixed: readonly PostingDraft[] = [
            draft({ key: 'a', counterpartyId: 'eur-bank', amount: '-100.00', otherAmount: '90.00', otherFixed: true }),
            draft({ key: 'b', counterpartyId: 'gbp-bank', amount: '50.00', otherAmount: '40.00', otherFixed: true }),
        ];
        renderWithSides(mixed, (p) => (p.counterpartyId === 'eur-bank' ? EUR : p.counterpartyId === 'gbp-bank' ? GBP : null));
        // Both lines carry their own direction in their field's name.
        expect(screen.getByLabelText('Posting 1: Amount received in Test EUR Bank, in EUR')).toBeInTheDocument();
        expect(screen.getByLabelText('Posting 2: Amount sent from Test GBP Bank, in GBP')).toBeInTheDocument();
        expect(headerCaptionAfterAmount()).toHaveTextContent(/^Other amount$/);
        expect(screen.queryByText('Received')).toBeNull();
        expect(screen.queryByText('Sent')).toBeNull();
    });

    it('captions the header with the direction every line shares', () => {
        const same: readonly PostingDraft[] = [
            draft({ key: 'a', counterpartyId: 'eur-bank', amount: '-100.00', otherAmount: '90.00', otherFixed: true }),
            draft({ key: 'b', counterpartyId: 'eur-bank-2', amount: '-20.00', otherAmount: '18.00', otherFixed: true }),
        ];
        renderWithSides(same, (p) => (p.counterpartyId === 'eur-bank' ? EUR : p.counterpartyId === 'eur-bank-2' ? EUR2 : null));
        expect(headerCaptionAfterAmount()).toHaveTextContent(/^Received$/);
    });

    it('keeps five tracks while no line goes to another currency', () => {
        renderWithSides(lines, () => null);
        // Anchor: the header is there, with its Amount caption.
        const amount = screen.getAllByText('Amount')[0]!;
        expect(amount.parentElement).toHaveAttribute('style', expect.stringContaining(FIVE));
        expect(screen.queryByText('Received')).toBeNull();
        expect(screen.queryByText('Other amount')).toBeNull();
    });

    // ADR-0114 D14: a missing second amount is listed in the strip under the field's own
    // sentence, its line numbered as the strip's other groups number theirs.
    it('marks a missing second amount on its row and field, and lists it in the strip under the field\'s sentence', async () => {
        renderWithSides([lines[0]!, { ...lines[1]!, otherAmount: '' }]);
        const field = screen.getByLabelText('Posting 2: Amount received in Test EUR Bank, in EUR');
        expect(field).toHaveAttribute('aria-invalid', 'true');
        expect(field).toHaveClass('border-state-danger');
        expect(screen.getByTitle('Enter the amount in EUR that arrived in Test EUR Bank')).toBeInTheDocument();

        const strip = screen.getByRole('alert');
        expect(within(strip).getByText('Enter the amount in EUR that arrived in Test EUR Bank:')).toBeInTheDocument();
        const link = within(strip).getByRole('button', {
            name: 'Posting 2: enter the amount in EUR that arrived in Test EUR Bank. Go to posting 2.',
        });
        expect(link).toHaveTextContent(/^2$/);
        await userEvent.click(link);
        expect(field).toHaveFocus();
    });

    it('gives each sentence its own group, after the strip\'s own, in the order of its first line', () => {
        const gbpLine = draft({ key: 'd', counterpartyId: 'gbp-bank', amount: '50.00', otherAmount: '', otherFixed: true });
        renderWithSides([
            { ...lines[0]!, amount: '' },
            { ...lines[1]!, otherAmount: '' },
            gbpLine,
            { ...lines[1]!, key: 'e', otherAmount: '' },
        ], (p) => (p.counterpartyId === 'eur-bank' ? EUR : p.counterpartyId === 'gbp-bank' ? GBP : null));
        const strip = screen.getByRole('alert');
        const groups = within(strip).getAllByText(/:$/, { selector: 'strong' }).map((h) => ({
            heading: h.textContent,
            lines: within(h.parentElement!).getAllByRole('button').map((b) => b.textContent),
        }));
        expect(groups).toEqual([
            { heading: 'Enter an amount:', lines: ['1'] },
            { heading: 'Enter the amount in EUR that arrived in Test EUR Bank:', lines: ['2', '4'] },
            { heading: 'Enter the amount in GBP that left Test GBP Bank:', lines: ['3'] },
        ]);
        expect(within(strip).getByRole('button', {
            name: 'Posting 3: enter the amount in GBP that left Test GBP Bank. Go to posting 3.',
        })).toBeInTheDocument();
    });
});
