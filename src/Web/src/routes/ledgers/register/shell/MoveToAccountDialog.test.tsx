import { describe, expect, it } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';

import { MoveToAccountDialog } from './MoveToAccountDialog';
import type { AccountSummary } from '@/lib/types/account';

// The picker half of the move rule: a category and an account are not
// interchangeable. The SERVER refuses that crossing whatever the client sends,
// so none of this is the guard — it is what stops the dialog offering a choice
// that can only fail, and on a category register what puts the useful
// operation, recategorise, on screen.
//
// Banking and investment accounts used to be kept apart here too. That was
// reversed on 2026-10-09 — both are money accounts a cash row can move between
// — and whether a PARTICULAR row may cross (a split may not; an investment
// event may not, unless it is a cash-only transfer) is a per-row question only
// the server can answer.
//
// Worth having rather than trusting the server: the bug this came from was a
// category's register offering every money account, and a user who selects one
// and is rejected has learned nothing about what they were allowed to do.

function account(over: Partial<AccountSummary> & { id: string; name: string }): AccountSummary {
    return {
        accountType: 'bank',
        categoryKind: null,
        parentId: null,
        isActive: true,
        isSystem: false,
        currencyCode: 'USD',
        ...over,
    } as AccountSummary;
}

const SOURCE_BANK = account({ id: 'a-bank', name: 'Checking' });
const ACCOUNTS: AccountSummary[] = [
    SOURCE_BANK,
    account({ id: 'a-bank2', name: 'Savings' }),
    account({ id: 'a-card', name: 'Rewards Card', accountType: 'credit_card' }),
    account({ id: 'a-loan', name: 'Car Loan', accountType: 'loan' }),
    account({ id: 'a-inv', name: 'Brokerage', accountType: 'investment' }),
    account({ id: 'c-food', name: 'Food', accountType: 'category', categoryKind: 'expense' }),
    account({ id: 'c-fuel', name: 'Fuel', accountType: 'category', categoryKind: 'expense' }),
];

/** Render, open the picker, and return every option it offers. */
function offeredFor(sourceAccountId: string, sourceAccountType: string): string[] {
    render(
        <MoveToAccountDialog
            open
            accounts={ACCOUNTS}
            sourceAccountId={sourceAccountId}
            sourceAccountType={sourceAccountType}
            count={2}
            pending={false}
            error={null}
            onConfirm={() => {}}
            onCancel={() => {}}
        />,
    );
    fireEvent.focus(screen.getByRole('combobox'));
    // queryAll, not getAll: an EMPTY list is a legitimate answer for a side of
    // the boundary whose only member is the source, and getAllByRole throws on
    // zero, which would read as a broken test rather than a correct refusal.
    return screen.queryAllByRole('option').map((o) => o.textContent ?? '');
}

describe('the move dialog offers only the register’s own side of the category boundary', () => {
    it('offers every money account from a bank account — investment included — and never a category', () => {
        const offered = offeredFor('a-bank', 'bank').join(' | ');

        // Money accounts are deliberately wide: a card and a loan are legal
        // targets, because bank → credit card is a correction people really
        // make — and so is a brokerage, for a deposit filed on the wrong side.
        expect(offered).toContain('Savings');
        expect(offered).toContain('Rewards Card');
        expect(offered).toContain('Car Loan');
        expect(offered).toContain('Brokerage');
        // ...and categories are not offered at all.
        expect(offered).not.toContain('Food');
        // The source itself is never a target.
        expect(offered).not.toContain('Checking');
    });

    it('offers only categories from a category’s register', () => {
        // THE CASE THIS CAME FROM. This dialog used to list every money
        // account here, and picking one re-pointed the category leg onto it.
        const offered = offeredFor('c-food', 'category').join(' | ');

        expect(offered).toContain('Fuel');
        expect(offered).not.toContain('Savings');
        expect(offered).not.toContain('Rewards Card');
        expect(offered).not.toContain('Brokerage');
        expect(offered).not.toContain('Food');
    });

    it('offers the bank accounts from a brokerage, and never a category', () => {
        // The reverse direction, which used to offer nothing at all here. A
        // contribution filed against the brokerage that belongs in checking
        // can now be moved back. Which rows may actually go is the server's
        // call — a split, or a trade, is refused there with a reason.
        const offered = offeredFor('a-inv', 'investment').join(' | ');

        expect(offered).toContain('Checking');
        expect(offered).toContain('Savings');
        expect(offered).not.toContain('Food');
        expect(offered).not.toContain('Brokerage');
    });

    it('offers nothing when the only other category is the source', () => {
        // The empty answer, and why sourceAccountType is REQUIRED: defaulted,
        // a category register would fall back to offering money accounts —
        // the exact corruption the boundary exists to stop.
        render(
            <MoveToAccountDialog
                open
                accounts={[ACCOUNTS[0]!, ACCOUNTS[5]!]}
                sourceAccountId="c-food"
                sourceAccountType="category"
                count={1}
                pending={false}
                error={null}
                onConfirm={() => {}}
                onCancel={() => {}}
            />,
        );
        fireEvent.focus(screen.getByRole('combobox'));
        expect(screen.getByText('No matches')).toBeInTheDocument();
        expect(screen.queryAllByRole('option')).toHaveLength(0);
    });

    it('names the target by the domain it is picking from', () => {
        render(
            <MoveToAccountDialog
                open
                accounts={ACCOUNTS}
                sourceAccountId="c-food"
                sourceAccountType="category"
                count={2}
                pending={false}
                error={null}
                onConfirm={() => {}}
                onCancel={() => {}}
            />,
        );
        expect(screen.getByRole('combobox', { name: /Target category for move/i }))
            .toBeInTheDocument();
    });
});
