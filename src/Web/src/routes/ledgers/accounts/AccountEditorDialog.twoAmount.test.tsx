import { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { stubFetch, type FetchStub } from '@/lib/annotations/fetchStub';
import type { AccountDetail, AccountSummary } from '@/lib/types';

import { AccountEditorDialog } from './AccountEditorDialog';

/**
 * A currency change that leaves mirrored transfers across currencies, so each now keeps
 * two amounts (ADR-0114 D13). The PATCH answers how many (`becameTwoAmount`). Above 0,
 * the editor stays open with the line and a single Done in place of its form, Done and
 * Escape close it, and the save's own work is done at save time (decided by the owner,
 * 2026-11-18). At 0 it closes, as every save did. Mocked at `fetch`; every account is
 * invented.
 */

const LEDGER = '00000000-0000-0000-0000-000000000010';
const ACCOUNT = '00000000-0000-0000-0000-000000000020';
const ONE = new RegExp(`/accounts/${ACCOUNT}$`);
const SINGULAR = "1 transfer now keeps two amounts, one in each account's currency.";
const PLURAL = "3 transfers now keep two amounts, one in each account's currency.";

const summary: AccountSummary = {
    id: ACCOUNT, ledgerId: LEDGER, parentId: null, name: 'Account A', accountType: 'bank',
    categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false,
    feedConnectionId: null, needsReviewCount: 0, holdingsAccountId: null,
    isTradeCommission: false, institutionName: null,
};
const detail: AccountDetail = {
    id: ACCOUNT, ledgerId: LEDGER, parentId: null, name: 'Account A', accountType: 'bank',
    categoryKind: null, currencyCode: 'USD', isActive: true, isSystem: false,
    institutionName: null, accountNumber: null, routingNumber: null,
    accountUrl: null, notes: null, openingBalance: 0, openedOn: null, taxStatus: null,
    loanTerms: null, managedReminder: null,
};

let stub: FetchStub;
beforeEach(() => {
    stub = stubFetch();
    stub.on('GET', ONE, detail);
});
afterEach(() => {
    vi.unstubAllGlobals();
});

/** The editor under a host that unmounts it on close, as the Accounts tab does. */
function renderEditor() {
    const onClose = vi.fn();
    const onSaved = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidated = vi.spyOn(queryClient, 'invalidateQueries');
    function Host() {
        const [open, setOpen] = useState(true);
        return open ? (
            <AccountEditorDialog
                ledgerId={LEDGER}
                account={summary}
                onClose={() => { onClose(); setOpen(false); }}
                onSaved={onSaved}
            />
        ) : null;
    }
    render(
        <QueryClientProvider client={queryClient}>
            <Host />
        </QueryClientProvider>,
    );
    const keys = () => invalidated.mock.calls.map((c) => c[0]?.queryKey);
    return { onClose, onSaved, keys };
}

/** Change the account's currency to EUR and save, which the stubbed PATCH answers. */
async function saveInEur(answer: unknown) {
    stub.on('PATCH', ONE, answer);
    const user = userEvent.setup();
    const rendered = renderEditor();
    // The details are read before Save is offered.
    await waitFor(() => expect(stub.callsTo('GET', ONE).length).toBe(1));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled());
    await user.selectOptions(screen.getByLabelText('Currency'), 'EUR');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(stub.callsTo('PATCH', ONE).length).toBe(1));
    return { user, ...rendered };
}

describe('the account editor after a currency change that left transfers keeping two amounts', () => {
    it.each([
        ['none did', { becameTwoAmount: 0 }],
        ['the count is absent', {}],
    ])('closes as before when %s', async (_label, answer) => {
        const { onClose, onSaved } = await saveInEur(answer);

        await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
        expect(onSaved).toHaveBeenCalledTimes(1);
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
        expect(screen.queryByText(/now keeps? two amounts/)).not.toBeInTheDocument();
    });

    it('stays open on the line for one transfer and Done, with the save\'s work already done', async () => {
        const { onClose, onSaved, keys } = await saveInEur({ becameTwoAmount: 1 });

        const status = await screen.findByRole('status');
        expect(status).toHaveTextContent(SINGULAR);
        const dialog = screen.getByRole('dialog');
        // The title is as it was; the form and its buttons are gone, Done alone in their place.
        expect(screen.getByRole('heading', { name: 'Edit Account A' })).toBeInTheDocument();
        expect(screen.queryByLabelText('Currency')).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument();
        const done = screen.getByRole('button', { name: 'Done' });
        expect(dialog.querySelectorAll('button')).toHaveLength(1);
        expect(done).toHaveFocus();
        // Refreshed at save time, not on Done.
        expect(onSaved).toHaveBeenCalledTimes(1);
        expect(keys()).toContainEqual(['account', LEDGER, ACCOUNT]);
        expect(keys()).toContainEqual(['accounts', LEDGER]);
        expect(onClose).not.toHaveBeenCalled();
    });

    it('says how many for more than one', async () => {
        await saveInEur({ becameTwoAmount: 3 });

        expect(await screen.findByRole('status')).toHaveTextContent(PLURAL);
    });

    it('closes on Done', async () => {
        const { user, onClose } = await saveInEur({ becameTwoAmount: 1 });

        await user.click(await screen.findByRole('button', { name: 'Done' }));

        expect(onClose).toHaveBeenCalledTimes(1);
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });

    it('closes on Escape', async () => {
        const { user, onClose } = await saveInEur({ becameTwoAmount: 1 });
        await waitFor(() => expect(screen.getByRole('button', { name: 'Done' })).toHaveFocus());

        await user.keyboard('{Escape}');

        expect(onClose).toHaveBeenCalledTimes(1);
        expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
});
