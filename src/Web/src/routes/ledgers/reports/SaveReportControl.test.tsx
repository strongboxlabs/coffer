import { describe, it, expect, vi, afterEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import type { ReportSpec } from '@/types/report';

import { SaveReportControl } from './SaveReportControl';

/**
 * Saving a memorized report (ADR-0104 D3).
 *
 * Mocked at FETCH, not at the api module, so the request body these assertions
 * read is the one that would actually reach the server. Stubbing
 * `createSavedReport` would prove the component calls a function and nothing
 * about what the server receives — and the spec is the entire payload here.
 */
const SPEC: ReportSpec = {
    measure: 'Spending',
    groupBy: 'Tag',
    bucket: 'Month',
    dateMode: 'Sliding',
    windowCount: 12,
    windowUnit: 'Month',
    windowAlign: true,
};

function stubFetch(body: unknown = { id: 'new-id', name: 'Saved', spec: SPEC, visibility: 'private' }) {
    const mock = vi.fn().mockResolvedValue(
        new Response(JSON.stringify(body), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
        }),
    );
    vi.stubGlobal('fetch', mock);
    return mock;
}

function renderControl(props: Partial<Parameters<typeof SaveReportControl>[0]> = {}) {
    const onSaved = vi.fn();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
        <QueryClientProvider client={client}>
            <SaveReportControl ledgerId="led-1" spec={SPEC} onSaved={onSaved} {...props} />
        </QueryClientProvider>,
    );
    return { onSaved };
}

describe('SaveReportControl', () => {
    afterEach(() => vi.unstubAllGlobals());

    it('offers only Save as until a report is loaded', () => {
        renderControl();

        // Nothing to overwrite yet, so an enabled "Save" would have to invent
        // a target.
        expect(screen.getByRole('button', { name: 'Save as…' })).toBeInTheDocument();
        expect(screen.queryByRole('button', { name: 'Save' })).toBeNull();
    });

    it('posts the spec it was given, private by default', async () => {
        const fetchMock = stubFetch();
        const user = userEvent.setup();
        const { onSaved } = renderControl();

        await user.click(screen.getByRole('button', { name: 'Save as…' }));
        await user.type(screen.getByLabelText('Name'), 'Tagged spending');
        await user.click(screen.getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(fetchMock).toHaveBeenCalled());
        const [url, init] = fetchMock.mock.calls[0]!;
        expect(url).toContain('/api/ledgers/led-1/reports/saved');
        expect(init.method).toBe('POST');

        const sent = JSON.parse(init.body as string);
        expect(sent.name).toBe('Tagged spending');
        // Private unless asked — the default is a decision, not an oversight.
        expect(sent.visibility).toBe('private');
        // The whole spec travels, including the dimension and the sliding
        // window. A save that dropped either would reopen as a different
        // report and look like the server lost it.
        expect(sent.spec).toMatchObject({
            groupBy: 'Tag', dateMode: 'Sliding', windowCount: 12, windowAlign: true,
        });
        await waitFor(() => expect(onSaved).toHaveBeenCalled());
    });

    it('shares only when asked', async () => {
        const fetchMock = stubFetch();
        const user = userEvent.setup();
        renderControl();

        await user.click(screen.getByRole('button', { name: 'Save as…' }));
        await user.type(screen.getByLabelText('Name'), 'Household');
        await user.click(screen.getByRole('checkbox', { name: /share with this ledger/i }));
        await user.click(screen.getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(fetchMock).toHaveBeenCalled());
        expect(JSON.parse(fetchMock.mock.calls[0]![1].body as string).visibility).toBe('ledger');
    });

    it('Save overwrites the loaded report rather than creating another', async () => {
        const fetchMock = stubFetch();
        const user = userEvent.setup();
        renderControl({ savedId: 'rep-7', savedName: 'Groceries' });

        await user.click(screen.getByRole('button', { name: 'Save' }));

        await waitFor(() => expect(fetchMock).toHaveBeenCalled());
        const [url, init] = fetchMock.mock.calls[0]!;
        // PATCH on the row, not a POST — otherwise every save of an open
        // report silently multiplies it.
        expect(url).toContain('/reports/saved/rep-7');
        expect(init.method).toBe('PATCH');
    });

    // Enter in the name submits the form. While that save is in flight, Save is
    // pending (pending.ts): a second Enter in the field clicks it and is refused,
    // and so are a press and Enter or Space on it. One request, whatever is
    // pressed, and after the refusal focus is still where it was. jsdom leaves
    // focus on a control that becomes disabled, where a browser drops it to the
    // page, so that Save is never natively disabled is asserted beside it.
    it('sends one request for every way of submitting again while the first is in flight', async () => {
        let refuse!: () => void;
        const held = new Promise<Response>((resolve) => {
            refuse = () => resolve(new Response(
                JSON.stringify({ title: 'Refused', status: 422, detail: 'A report with that name exists.' }),
                { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
            ));
        });
        const fetchMock = vi.fn(() => held);
        vi.stubGlobal('fetch', fetchMock);
        const user = userEvent.setup();
        renderControl();

        await user.click(screen.getByRole('button', { name: 'Save as…' }));
        const name = screen.getByLabelText('Name');
        await user.type(name, 'Household{Enter}');
        const saving = await screen.findByRole('button', { name: 'Saving…' });
        expect(name).toHaveFocus();

        await user.type(name, '{Enter}');
        await user.click(saving);
        await user.keyboard('{Enter}');
        await user.keyboard(' ');
        expect(fetchMock).toHaveBeenCalledTimes(1);
        expect(saving).toBeEnabled();
        expect(saving).toHaveFocus();

        refuse();
        expect(await screen.findByRole('alert')).toHaveTextContent('A report with that name exists.');
        expect(await screen.findByRole('button', { name: 'Save' })).toBe(saving);
        expect(saving).toHaveFocus();
        expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    // ADR-0023 §L: the save sends the name and Share as they were at the press, and
    // its success resets both, so while it is in flight neither takes a change.
    it('refuses typing in the name and a tick of Share while the save is in flight', async () => {
        vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})));
        const user = userEvent.setup();
        renderControl();

        await user.click(screen.getByRole('button', { name: 'Save as…' }));
        const name = screen.getByLabelText('Name');
        const share = screen.getByRole('checkbox', { name: /share with this ledger/i }) as HTMLInputElement;
        await user.type(name, 'Household{Enter}');
        await screen.findByRole('button', { name: 'Saving…' });

        await user.type(name, ' more');
        await user.click(share);

        expect(name).toHaveValue('Household');
        expect(share.checked).toBe(false);
        expect(name).toHaveAttribute('aria-disabled', 'true');
        expect(share).toHaveAttribute('aria-disabled', 'true');
    });

    // The form goes when its save lands or it is cancelled, taking the control
    // that held focus with it; focus goes to Save as…, never to the page. Each
    // assertion waits for the save to land and the form to be gone first.
    describe('focus when the form goes', () => {
        const saveAs = () => screen.getByRole('button', { name: 'Save as…' });

        it('after Enter in the name field saves', async () => {
            stubFetch();
            const user = userEvent.setup();
            const { onSaved } = renderControl();

            await user.click(saveAs());
            await user.type(screen.getByLabelText('Name'), 'Household{Enter}');

            await waitFor(() => expect(onSaved).toHaveBeenCalled());
            await waitFor(() => expect(screen.queryByLabelText('Name')).toBeNull());
            expect(saveAs()).toHaveFocus();
        });

        it('after the form\'s Save saves', async () => {
            stubFetch();
            const user = userEvent.setup();
            const { onSaved } = renderControl();

            await user.click(saveAs());
            await user.type(screen.getByLabelText('Name'), 'Household');
            await user.click(screen.getByRole('button', { name: 'Save' }));

            await waitFor(() => expect(onSaved).toHaveBeenCalled());
            await waitFor(() => expect(screen.queryByLabelText('Name')).toBeNull());
            expect(saveAs()).toHaveFocus();
        });

        it('after the save lands with focus on the Share checkbox', async () => {
            let land!: () => void;
            const held = new Promise<Response>((resolve) => {
                land = () => resolve(new Response(
                    JSON.stringify({ id: 'new-id', name: 'Household', spec: SPEC, visibility: 'private' }),
                    { status: 200, headers: { 'Content-Type': 'application/json' } },
                ));
            });
            vi.stubGlobal('fetch', vi.fn(() => held));
            const user = userEvent.setup();
            const { onSaved } = renderControl();

            await user.click(saveAs());
            await user.type(screen.getByLabelText('Name'), 'Household{Enter}');
            await screen.findByRole('button', { name: 'Saving…' });
            await user.tab();
            const share = screen.getByRole('checkbox', { name: /share with this ledger/i });
            expect(share).toHaveFocus();

            land();
            await waitFor(() => expect(onSaved).toHaveBeenCalled());
            await waitFor(() => expect(screen.queryByLabelText('Name')).toBeNull());
            expect(saveAs()).toHaveFocus();
        });

        it('after Cancel', async () => {
            const user = userEvent.setup();
            renderControl();

            await user.click(saveAs());
            await user.click(screen.getByRole('button', { name: 'Cancel' }));

            expect(screen.queryByLabelText('Name')).toBeNull();
            expect(saveAs()).toHaveFocus();
        });
    });

    it('will not create a report with no name', async () => {
        const fetchMock = stubFetch();
        const user = userEvent.setup();
        renderControl();

        await user.click(screen.getByRole('button', { name: 'Save as…' }));
        // Name left empty: the confirm is disabled, so nothing is sent and the
        // 400 the server would return never has to be explained to anyone.
        expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
        expect(fetchMock).not.toHaveBeenCalled();
    });
});
