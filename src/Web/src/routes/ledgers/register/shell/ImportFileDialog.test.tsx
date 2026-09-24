import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import * as apiModule from '@/lib/api';
import type { OfxImportResponse } from '@/lib/types';

import { ImportFileDialog } from './ImportFileDialog';

const OPERATION_ID = '11111111-1111-1111-1111-111111111111';

const IMPORTED: OfxImportResponse = {
    syncRunId: OPERATION_ID,
    accountsDiscovered: 1,
    transactionsForReview: 47,
    alreadyKnown: 0,
    errors: [],
};

/**
 * Drives the dialog to its RESULT step by importing a file for real (through
 * mocked api functions), because that is the only place the undo affordance
 * exists — the moment the mistake is noticed.
 */
async function atResultStep(result: OfxImportResponse = IMPORTED) {
    const onUndone = vi.fn();
    vi.spyOn(apiModule, 'previewQif').mockResolvedValue({
        accounts: [{
            providerAccountId: 'qif',
            accountType: 'bank',
            currency: null,
            transactionCount: 47,
            accountName: null,
        }],
        errors: [],
    } as never);
    vi.spyOn(apiModule, 'importQif').mockResolvedValue(result as never);

    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    render(
        <QueryClientProvider client={queryClient}>
            <ImportFileDialog
                ledgerId="led-1"
                accountId="acc-1"
                accountName="Checking"
                accountKind="bank"
                onClose={vi.fn()}
                onImported={vi.fn()}
                onUndone={onUndone}
            />
        </QueryClientProvider>,
    );

    const user = userEvent.setup();
    // Exact string, not a regex: the modal itself is labelled "Import statement
    // file", so a loose match finds two elements.
    const input = screen.getByLabelText('Statement file');
    await user.upload(
        input,
        new File(['!Type:Bank'], 'export.qif', { type: 'application/octet-stream' }),
    );
    await user.click(screen.getByRole('button', { name: /upload/i }));
    // The button reads "Import 47 txns →", not "Import".
    await user.click(await screen.findByRole('button', { name: /^import \d+ txn/i }));

    // Anchor on the result copy, so everything below runs on a settled step.
    await screen.findByText(/imported/i);
    return { user, onUndone };
}

describe('ImportFileDialog undo', () => {
    // Without this the spies accumulate across tests: call counts carry over and a
    // later mockResolvedValue never takes effect, so one test's fixture silently
    // answers the next one's assertions.
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('counts before it removes anything', async () => {
        // Two-phase on purpose: the first click only asks how many rows would go, so
        // the confirm can state a real number. A single click that deletes money is
        // not an affordance worth having.
        const undo = vi.spyOn(apiModule, 'undoImport').mockResolvedValue({
            found: 47, edited: 0, deleted: 0, tooLarge: false,
        });

        const { user } = await atResultStep();
        await user.click(screen.getByRole('button', { name: /undo this import/i }));

        await waitFor(() => expect(undo).toHaveBeenCalled());
        expect(undo.mock.calls[0]?.[2]).toMatchObject({ dryRun: true });
        // textContent, not findByText: the count sits in a <strong>, and the default
        // matcher only sees an element's own direct text nodes.
        const confirm = await screen.findByRole('alert');
        expect(confirm.textContent).toMatch(/remove 47 transactions from checking/i);
    });

    it('lets you back out after asking what undo would do', async () => {
        // Asking is not agreeing. The dry run is the only way to learn how many rows
        // are at stake, and it used to permanently replace the safe button with a red
        // "Confirm undo" sitting beside "Import another" and "Done" — one mis-click
        // from deleting the rows, with no way back but closing the dialog.
        const undo = vi.spyOn(apiModule, 'undoImport').mockResolvedValue({
            found: 47, edited: 0, deleted: 0, tooLarge: false,
        });

        const { user } = await atResultStep();
        await user.click(screen.getByRole('button', { name: /undo this import/i }));
        await screen.findByRole('alert');

        await user.click(screen.getByRole('button', { name: /keep them/i }));

        // The danger button and its warning are gone, and the safe one is back.
        expect(screen.queryByRole('button', { name: /confirm undo/i })).toBeNull();
        expect(screen.queryByRole('alert')).toBeNull();
        expect(screen.getByRole('button', { name: /undo this import/i })).toBeTruthy();
        // Backing out deleted nothing: the only call was the dry run.
        expect(undo).toHaveBeenCalledTimes(1);
        expect(undo.mock.calls[0]?.[2]).toMatchObject({ dryRun: true });
    });

    it('says how many of the rows have been edited since', async () => {
        // An undo that silently discards someone's edits is the kind of helpful
        // delete nobody forgives.
        vi.spyOn(apiModule, 'undoImport').mockResolvedValue({
            found: 47, edited: 3, deleted: 0, tooLarge: false,
        });

        const { user } = await atResultStep();
        await user.click(screen.getByRole('button', { name: /undo this import/i }));

        const confirm = await screen.findByRole('alert');
        // The consequence, not just the count: undo hard-deletes, so those edits go.
        expect(confirm.textContent)
            .toMatch(/3 of them have been edited since .+ those edits will be lost/i);
    });

    it('does not warn about edits when there are none', async () => {
        // Keeps the test above from passing on a component that always shows the
        // warning. Anchored on the confirm copy, which only appears once the dry run
        // has resolved, so the absence below is judged on a settled frame.
        vi.spyOn(apiModule, 'undoImport').mockResolvedValue({
            found: 47, edited: 0, deleted: 0, tooLarge: false,
        });

        const { user } = await atResultStep();
        await user.click(screen.getByRole('button', { name: /undo this import/i }));

        const confirm = await screen.findByRole('alert');
        expect(confirm.textContent).toMatch(/remove 47 transactions/i);
        expect(confirm.textContent).not.toMatch(/edited since/i);
    });

    it('removes the rows only after the confirm, and names the real recovery', async () => {
        const undo = vi.spyOn(apiModule, 'undoImport')
            .mockResolvedValueOnce({ found: 47, edited: 0, deleted: 0, tooLarge: false })
            .mockResolvedValueOnce({ found: 47, edited: 0, deleted: 47, tooLarge: false });

        const { user } = await atResultStep();
        await user.click(screen.getByRole('button', { name: /undo this import/i }));
        await screen.findByRole('alert');

        // Still a dry run only — nothing has been removed at this point.
        expect(undo).toHaveBeenCalledTimes(1);

        await user.click(screen.getByRole('button', { name: /confirm undo/i }));

        await waitFor(() => expect(undo).toHaveBeenCalledTimes(2));
        // The second call is the real one.
        expect(undo.mock.calls[1]?.[2]).toBeUndefined();
        // The recovery is re-importing the file, NOT un-hiding: a hidden row keeps
        // its external_id and would make this same file un-importable.
        const done = await screen.findByText(/import the file again/i);
        expect(done.parentElement?.textContent)
            .toMatch(/47 transactions removed from checking/i);
    });

    it('tells the page to refresh, or the rows stay on screen', async () => {
        // THE BUG THIS FILE MISSED. The first version invalidated
        // ['transactions', ledgerId] — a key nothing registers — so nothing refreshed
        // and every undone transaction stayed visible until a manual reload. The
        // windowed register (ADR-0079) re-reads on register.refresh(), which only the
        // page can call, so the dialog has to hand the refresh back.
        //
        // Asserting the API call and the copy was not enough: both were right while
        // the visible consequence was wrong.
        vi.spyOn(apiModule, 'undoImport')
            .mockResolvedValueOnce({ found: 47, edited: 0, deleted: 0, tooLarge: false })
            .mockResolvedValueOnce({ found: 47, edited: 0, deleted: 47, tooLarge: false });

        const { user, onUndone } = await atResultStep();
        await user.click(screen.getByRole('button', { name: /undo this import/i }));
        await screen.findByRole('alert');

        // Not on the dry run — nothing has moved yet, so nothing should refresh.
        expect(onUndone).not.toHaveBeenCalled();

        await user.click(screen.getByRole('button', { name: /confirm undo/i }));
        await waitFor(() => expect(onUndone).toHaveBeenCalledTimes(1));
    });

    it('refuses an import too large to undo without pretending to try', async () => {
        vi.spyOn(apiModule, 'undoImport').mockResolvedValue({
            found: 20000, edited: 0, deleted: 0, tooLarge: true,
        });

        const { user } = await atResultStep();
        await user.click(screen.getByRole('button', { name: /undo this import/i }));

        const refusal = await screen.findByRole('alert');
        expect(refusal.textContent).toMatch(/too large to undo/i);
        // And offers no confirm, because there is nothing it could safely do.
        expect(screen.queryByRole('button', { name: /confirm undo/i })).toBeNull();
    });

    it('offers no undo for an import that added nothing', async () => {
        // Nothing was written, so there is nothing to take back. Anchored on the
        // result copy first so this absence is judged after the step settles.
        const { user } = await atResultStep({
            ...IMPORTED, transactionsForReview: 0, alreadyKnown: 0,
        });
        await screen.findByText(/imported/i);
        void user;

        expect(screen.getByRole('button', { name: /undo this import/i }))
            .toBeDisabled();
    });
});

/**
 * The delimited path, which is the only one with a wizard in front of the preview —
 * and therefore the only one with answers that a step change can destroy.
 *
 * Synthetic data throughout: invented merchants, tab-separated under a .csv name
 * because that is the shape a real export turned out to have.
 */
describe('ImportFileDialog, delimited path', () => {
    const FOUR_COLUMN = [
        '09/02/2026\t$41.18\tSAMPLE.COM   ANYTOWN ST\tpurchase',
        '09/03/2026\t$7.99\tSAMPLE STORE ANYTOWN ST\tpurchase',
    ].join('\n');

    const THREE_COLUMN = [
        '2026-09-02,SHOP,-23.50',
        '2026-09-03,CAFE,-4.20',
    ].join('\n');

    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchCsvMappings').mockResolvedValue([]);
        vi.spyOn(apiModule, 'previewCsv').mockResolvedValue({
            accounts: [{
                providerAccountId: 'csv',
                accountType: 'bank',
                currency: null,
                transactionCount: 2,
                accountName: null,
            }],
            errors: [],
        } as never);
    });

    function renderDialog() {
        const queryClient = new QueryClient({
            defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
        });
        render(
            <QueryClientProvider client={queryClient}>
                <ImportFileDialog
                    ledgerId="led-1"
                    accountId="acc-1"
                    accountName="Checking"
                    accountKind="bank"
                    onClose={vi.fn()}
                    onImported={vi.fn()}
                    onUndone={vi.fn()}
                />
            </QueryClientProvider>,
        );
        return userEvent.setup();
    }

    async function upload(user: ReturnType<typeof userEvent.setup>, body: string, name = 'statement.csv') {
        await user.upload(
            screen.getByLabelText('Statement file'),
            new File([body], name, { type: 'text/csv' }),
        );
        await user.click(screen.getByRole('button', { name: /upload/i }));
    }

    it('comes back to the wizard from the preview, with the answers intact', async () => {
        // The worst of the confirmed findings. Back from a bad preview landed on the
        // FILE PICKER — which still showed the filename, so nothing said anything was
        // lost — and re-entering remounted the wizard from scratch. A bad preview is a
        // mapping problem, and there was no route from it back to the mapping.
        const user = renderDialog();
        await upload(user, FOUR_COLUMN);

        const payee = await screen.findByLabelText(/payee column/i);
        await user.selectOptions(payee, '4');
        await user.click(screen.getByRole('button', { name: /continue/i }));

        // On the preview now.
        await screen.findByRole('button', { name: /^import \d+ txn/i });

        await user.click(screen.getByRole('button', { name: /back/i }));

        // Back in the WIZARD, and it still knows what we told it.
        const again = await screen.findByLabelText(/payee column/i);
        expect((again as HTMLSelectElement).value).toBe('4');
        expect(screen.getByRole('table')).toBeTruthy();
    });

    it("drops the previous file's mapping when a different file is chosen", async () => {
        // A mapping describes ONE file's shape. It survived a file change, so the next
        // file was previewed AND imported under the previous file's document —
        // plausible-looking garbage, with nothing on screen naming what produced it.
        const user = renderDialog();
        await upload(user, FOUR_COLUMN);

        await user.selectOptions(await screen.findByLabelText(/payee column/i), '4');
        await user.click(screen.getByRole('button', { name: /back/i }));

        await upload(user, THREE_COLUMN, 'other.csv');

        // Re-guessed for the new file (date 1, payee 2, amount 3), not carried over.
        const payee = await screen.findByLabelText(/payee column/i);
        await waitFor(() => expect((payee as HTMLSelectElement).value).toBe('2'));
    });

    it('clears a failed preview once the mapping changes', async () => {
        // The error sat there through every edit that fixed it, because only a new
        // attempt cleared it — so the wizard read as broken while the document was
        // already correct, and the obvious next move was to give up.
        const user = renderDialog();
        vi.spyOn(apiModule, 'previewCsv').mockRejectedValue(new Error('could not read that'));
        await upload(user, FOUR_COLUMN);

        await screen.findByLabelText(/payee column/i);
        await user.click(screen.getByRole('button', { name: /continue/i }));
        expect((await screen.findByRole('alert')).textContent).toContain('could not read that');

        await user.selectOptions(screen.getByLabelText(/payee column/i), '4');

        await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
    });

    it("does not show the previous file's import failure over a new file's preview", async () => {
        // Only the PREVIEW mutation was reset on Back and on picking a file, so a failed
        // import outlived both. The next file previewed cleanly and rendered the last
        // file's error underneath it, before Import had been pressed at all — a message
        // about a file no longer on screen, clearing only on the next attempt.
        const user = renderDialog();
        vi.spyOn(apiModule, 'importCsv').mockRejectedValue(new Error('server said no'));
        await upload(user, FOUR_COLUMN, 'statement-a.csv');

        await screen.findByLabelText(/payee column/i);
        await user.click(screen.getByRole('button', { name: /continue/i }));
        await user.click(await screen.findByRole('button', { name: /import/i }));
        expect((await screen.findByRole('alert')).textContent).toContain('server said no');

        // Back to the mapping, back again to the picker, then a different file entirely.
        // Both hops matter: the error survived each of them independently.
        await user.click(screen.getByRole('button', { name: /back/i }));
        await screen.findByLabelText(/payee column/i);
        await user.click(screen.getByRole('button', { name: /back/i }));
        await upload(user, THREE_COLUMN, 'statement-b.csv');
        await user.click(await screen.findByRole('button', { name: /continue/i }));

        // Anchored on the new preview having actually arrived — without this the
        // assertion would pass on the frame before anything rendered.
        await screen.findByRole('button', { name: /import/i });
        expect(screen.queryByText(/server said no/i)).toBeNull();
    });

    it('moves focus to each new step and names it', async () => {
        // Advancing unmounted the focused button and left activeElement on <body>,
        // which the modal's focus trap does not redirect — so the next Tab walked into
        // the register behind the backdrop, where rows and menus are still clickable.
        const user = renderDialog();
        expect(screen.getByRole('heading', { level: 2 }).textContent)
            .toMatch(/import statement file/i);

        await upload(user, FOUR_COLUMN);

        const heading = await screen.findByRole('heading', { level: 2 });
        // The step has a name of its own, which is also the dialog's accessible name —
        // so the transition is announced rather than silent.
        expect(heading.textContent).toMatch(/describe this file/i);
        await waitFor(() => expect(document.activeElement).toBe(heading));
    });
});

/**
 * Investment accounts import a BROKERAGE's own CSV; bank accounts describe a delimited
 * file with a mapping. The two were one flow, which is how the generic bank mapping came
 * to be offered on an investment account — where it cannot express shares, price or
 * action, so it would not fail, it would post a share purchase as a cash withdrawal.
 */
describe('ImportFileDialog, split by account kind', () => {
    const FIDELITY = [
        '',
        '',
        'Run Date,Action,Symbol,Description,Type,Price ($),Quantity,Commission ($),'
        + 'Fees ($),Accrued Interest ($),Amount ($),Cash Balance ($),Settlement Date',
        '09/02/2026,YOU BOUGHT SAMPLE FUND (SMPL),SMPL,SAMPLE FUND,Cash,10.00,10,,,,'
        + '-100.00,900.00,',
    ].join('\n');

    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchCsvMappings').mockResolvedValue([]);
    });

    function renderFor(kind: 'bank' | 'investment', importProviderKey?: string | null) {
        const queryClient = new QueryClient({
            defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
        });
        render(
            <QueryClientProvider client={queryClient}>
                <ImportFileDialog
                    ledgerId="led-1"
                    accountId="acc-1"
                    accountName="Brokerage"
                    accountKind={kind}
                    importProviderKey={importProviderKey}
                    onClose={vi.fn()}
                    onImported={vi.fn()}
                    onUndone={vi.fn()}
                />
            </QueryClientProvider>,
        );
        return userEvent.setup();
    }

    async function upload(user: ReturnType<typeof userEvent.setup>, body: string) {
        await user.upload(
            screen.getByLabelText('Statement file'),
            new File([body], 'statement.csv', { type: 'text/csv' }),
        );
        await user.click(screen.getByRole('button', { name: /upload/i }));
    }

    /**
     * Pick a file WITHOUT uploading. The brokerage question is asked after the file,
     * so most of these tests need the file on screen before there is anything to
     * assert about brokerages at all.
     */
    async function pick(
        user: ReturnType<typeof userEvent.setup>, name: string, body = FIDELITY,
    ) {
        await user.upload(
            screen.getByLabelText('Statement file'),
            new File([body], name, { type: 'text/csv' }),
        );
        // A settled anchor: the picked file is echoed under the input, so waiting on
        // it means a later queryByRole(...)  -> null is judging the rendered frame
        // rather than passing on the one before React processed the pick.
        await screen.findByText(name, { exact: false });
    }

    it('offers the supported brokerages once a CSV is picked, and says where the edge is', () => {
        // The list is also how someone learns what is supported. Sniffing the file could
        // never answer that: a Schwab holder would see only "could not read this file".
        return (async () => {
            const user = renderFor('investment');
            await pick(user, 'statement.csv');

            expect(screen.getByRole('radio', { name: /fidelity/i })).toBeTruthy();
            expect(screen.getByText(/other brokerages aren.t supported yet/i)).toBeTruthy();
        })();
    });

    it('puts the brokerage question after the file, not before it', () => {
        // The ordering IS the requirement, not a side effect of the conditional: the
        // question only makes sense once the file it is about has been chosen. Asserting
        // document position because both elements being present says nothing about
        // which one the user meets first.
        return (async () => {
            const user = renderFor('investment');
            await pick(user, 'statement.csv');

            const input = screen.getByLabelText('Statement file');
            const radio = screen.getByRole('radio', { name: /fidelity/i });
            // DOCUMENT_POSITION_FOLLOWING = 4: the radio comes after the input.
            expect(input.compareDocumentPosition(radio)
                & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
        })();
    });

    it('asks nothing about brokerages before a file is picked', () => {
        // The question is CSV-only, so asking it above the control that decides whether
        // it applies put it to everyone — including someone about to pick a QFX.
        renderFor('investment');

        // Settled anchor: the picker is mounted and idle, so the absence below is a
        // real absence and not a frame that has yet to render.
        expect(screen.getByLabelText('Statement file')).toBeTruthy();
        expect(screen.queryByRole('radio', { name: /fidelity/i })).toBeNull();
    });

    it('asks nothing about brokerages for a self-describing file', () => {
        // OFX, QFX and QIF name their own format. The list would be a question with no
        // bearing on the answer.
        return (async () => {
            const user = renderFor('investment');
            await pick(user, 'export.qif', '!Type:Invst');

            expect(screen.queryByRole('radio', { name: /fidelity/i })).toBeNull();
        })();
    });

    it('offers no brokerage list on a bank account, even for a CSV', () => {
        // A bank CSV goes to the generic mapping step instead; the brokerage providers
        // are investment-only (they are the half of the split that can express shares).
        return (async () => {
            const user = renderFor('bank');
            await pick(user, 'statement.csv');

            expect(screen.queryByRole('radio', { name: /fidelity/i })).toBeNull();
        })();
    });

    it('preselects the brokerage this account last imported with', () => {
        // Saying "this account is at Fidelity" should be a once-only act; the server
        // remembers it as a side effect of the import that said so.
        return (async () => {
            const user = renderFor('investment', 'csv-fidelity');
            await pick(user, 'statement.csv');

            expect((screen.getByRole('radio', { name: /fidelity/i }) as HTMLInputElement).checked)
                .toBe(true);
            // ...and the preselection is a real answer, not just a checked dot: Upload
            // is live without touching the radio.
            expect((screen.getByRole('button', { name: /upload/i }) as HTMLButtonElement).disabled)
                .toBe(false);
        })();
    });

    it('ignores a remembered provider it no longer recognises', () => {
        // The key is written by whichever provider last ran. One later renamed or removed
        // should leave the picker unselected rather than break the dialog.
        return (async () => {
            const user = renderFor('investment', 'csv-some-departed-brokerage');
            await pick(user, 'statement.csv');

            expect((screen.getByRole('radio', { name: /fidelity/i }) as HTMLInputElement).checked)
                .toBe(false);
        })();
    });

    it('sends an investment CSV to the brokerage provider, with no mapping step', () => {
        // The brokerage's provider already knows the format. Asking the user to describe
        // a file it can read would be asking a question with a known answer.
        const preview = vi.spyOn(apiModule, 'previewFidelity').mockResolvedValue({
            accounts: [{
                providerAccountId: 'fidelity',
                accountType: 'investment',
                currency: null,
                transactionCount: 1,
                accountName: null,
            }],
            errors: [],
        } as never);

        return (async () => {
            const user = renderFor('investment', 'csv-fidelity');
            await upload(user, FIDELITY);

            await waitFor(() => expect(preview).toHaveBeenCalled());
            // Straight to the preview: no mapping wizard in between.
            expect(screen.queryByLabelText(/^layout$/i)).toBeNull();
            expect(screen.queryByLabelText(/date column/i)).toBeNull();
        })();
    });

    it('imports through the chosen brokerage, not just previews through it', () => {
        // Preview and import are separate calls, and only one of them was covered —
        // so sending the import down the generic CSV path instead changed nothing any
        // test could see.
        vi.spyOn(apiModule, 'previewFidelity').mockResolvedValue({
            accounts: [{
                providerAccountId: 'fidelity',
                accountType: 'investment',
                currency: null,
                transactionCount: 1,
                accountName: null,
            }],
            errors: [],
        } as never);
        const doImport = vi.spyOn(apiModule, 'importFidelity').mockResolvedValue({
            syncRunId: '11111111-1111-1111-1111-111111111111',
            accountsDiscovered: 1,
            transactionsForReview: 1,
            alreadyKnown: 0,
            errors: [],
        } as never);
        const generic = vi.spyOn(apiModule, 'importCsv');

        return (async () => {
            const user = renderFor('investment', 'csv-fidelity');
            await upload(user, FIDELITY);

            await user.click(await screen.findByRole('button', { name: /^import \d+ txn/i }));

            await waitFor(() => expect(doImport).toHaveBeenCalled());
            expect(generic).not.toHaveBeenCalled();
            expect(doImport.mock.calls[0]![3]).toBe('fidelity');
        })();
    });

    it('still sends a bank CSV through the mapping wizard', () => {
        // The bank side is untouched by the split.
        return (async () => {
            const user = renderFor('bank');
            await upload(user, 'a,b\n1,2\n');

            expect(await screen.findByLabelText(/^layout$/i)).toBeTruthy();
        })();
    });

    it('will not upload an investment CSV until a brokerage is chosen', () => {
        // Refused on the pick step, not at preview time: uploading only to be told
        // "choose the brokerage" is a round trip whose answer was already on screen.
        return (async () => {
            const user = renderFor('investment');
            await user.upload(
                screen.getByLabelText('Statement file'),
                new File([FIDELITY], 'statement.csv', { type: 'text/csv' }),
            );

            const button = screen.getByRole('button', { name: /upload/i }) as HTMLButtonElement;
            expect(button.disabled).toBe(true);
            // ...and says why, rather than leaving a dead button unexplained.
            expect((await screen.findByRole('alert')).textContent)
                .toMatch(/choose the brokerage/i);

            await user.click(screen.getByRole('radio', { name: /fidelity/i }));
            expect((screen.getByRole('button', { name: /upload/i }) as HTMLButtonElement).disabled)
                .toBe(false);
        })();
    });

    it('does not demand a brokerage for a self-describing format', () => {
        // OFX, QFX and QIF say what they are. Requiring a brokerage for them would be
        // asking a question with no bearing on the answer.
        return (async () => {
            const user = renderFor('investment');
            await user.upload(
                screen.getByLabelText('Statement file'),
                new File(['!Type:Invst'], 'export.qif', { type: 'application/octet-stream' }),
            );

            expect((screen.getByRole('button', { name: /upload/i }) as HTMLButtonElement).disabled)
                .toBe(false);
            expect(screen.queryByText(/choose the brokerage/i)).toBeNull();
            // Not merely unnagged — unasked. The fieldset itself must not be there.
            expect(screen.queryByRole('radio', { name: /fidelity/i })).toBeNull();
        })();
    });
});
