import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { WelcomePanel } from './WelcomePanel';

// Post-setup welcome screen (ADR-0095, amending ADR-0092 D2). The master key lives
// here rather than inside the setup ceremony.

const KEY = 'Zm9vYmFyYmF6cXV1eGZvb2JhcmJhenF1dXhmb28xMjM=';

describe('WelcomePanel', () => {
    afterEach(() => {
        vi.restoreAllMocks();
    });

    it('shows the key and continues without gating on an acknowledgement', async () => {
        // The point of ADR-0095: this is advice, not a ceremony. A checkbox here would
        // rank a re-viewable key alongside the one-time recovery codes, and gate the
        // hub behind filing away a secret that currently protects nothing.
        const onContinue = vi.fn();
        const user = userEvent.setup();
        render(<WelcomePanel keyBase64={KEY} onContinue={onContinue} />);

        expect(screen.getByText(KEY)).toBeInTheDocument();
        expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();

        const go = screen.getByRole('button', { name: 'Get started' });
        expect(go).toBeEnabled();
        await user.click(go);
        expect(onContinue).toHaveBeenCalledOnce();
    });

    it('points at backups as the thing that actually protects the data', () => {
        render(<WelcomePanel keyBase64={KEY} onContinue={vi.fn()} />);

        expect(screen.getByText(/then set up backups/i)).toBeInTheDocument();
        // The claim that makes the key's lower severity legible: a restore needs the
        // artifact and its passphrase, not this key.
        expect(screen.getByText(/plus its passphrase is all a restore needs/i))
            .toBeInTheDocument();
    });

    it('says the key can be seen again — not a false last chance', () => {
        render(<WelcomePanel keyBase64={KEY} onContinue={vi.fn()} />);

        expect(screen.getByText(/not a last chance/i)).toBeInTheDocument();
        expect(screen.getByText(/System → Encryption/i)).toBeInTheDocument();
    });

    // Setup creates no ledger (ADR-0115 D2), so there is one next step, with every
    // way to a first ledger the hub offers: create, a Demo, or an import.
    it('names creating, a Demo and an import as the next step, in one copy', () => {
        render(<WelcomePanel keyBase64={KEY} onContinue={vi.fn()} />);

        expect(screen.getByRole('heading', { name: 'Add your first ledger' })).toBeInTheDocument();
        expect(screen.getByText(
            'Create an empty ledger, add a Demo ledger to look around first, or import a '
            + 'Moneydance export to bring your history across with accounts, splits, '
            + 'investment lots and cost basis intact.',
        )).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Get started' })).toBeInTheDocument();
        // The old seeded-Demo branch is gone.
        expect(screen.queryByText(/have a look around/i)).not.toBeInTheDocument();
        expect(screen.queryByRole('button', { name: /go to my ledgers/i })).not.toBeInTheDocument();
    });

    it('downloads via an in-document anchor and outlives the click', async () => {
        // Both details are load-bearing and easy to "simplify" away: Firefox ignores a
        // click on an anchor that isn't in the document, and Safari cancels the download
        // if the object URL is revoked before the handler resolves. Getting either wrong
        // yields a button that silently does nothing on those browsers.
        const createObjectURL = vi.fn(() => 'blob:mock');
        const revokeObjectURL = vi.fn();
        vi.stubGlobal('URL', { ...URL, createObjectURL, revokeObjectURL });

        let anchorInDocumentAtClick = false;
        const clickSpy = vi
            .spyOn(HTMLAnchorElement.prototype, 'click')
            .mockImplementation(function (this: HTMLAnchorElement) {
                anchorInDocumentAtClick = document.body.contains(this);
            });

        render(<WelcomePanel keyBase64={KEY} onContinue={vi.fn()} />);

        // fireEvent, not userEvent, and that is the whole point. handleDownload is
        // synchronous, so a synchronous dispatch runs it to completion with no
        // event-loop turn in between — which makes the "not yet revoked" assertion
        // below a statement about BEHAVIOUR.
        //
        // `await user.click()` made it a statement about ELAPSED TIME instead: it
        // yields to the event loop, jsdom implements requestAnimationFrame as a ~16ms
        // timer, and on a loaded machine (a parallel test run) the deferred revoke
        // fired during the click's own internal awaits. The test then failed on a
        // component that was behaving perfectly.
        //
        // Two rejected alternatives, for the next person who wants to "improve" this.
        // Fake timers deadlock userEvent even with `advanceTimers` wired up. And
        // recording "was it revoked before click() fired?" inside the spy is
        // deterministic but weaker — it would miss a revoke issued synchronously just
        // AFTER click(), which is exactly the Safari-cancelling bug this test catches.
        fireEvent.click(screen.getByRole('button', { name: /download/i }));

        expect(clickSpy).toHaveBeenCalledOnce();
        expect(anchorInDocumentAtClick).toBe(true);
        // Not revoked synchronously — deferred to the next frame.
        expect(revokeObjectURL).not.toHaveBeenCalled();

        await new Promise((resolve) => requestAnimationFrame(() => resolve(null)));
        expect(revokeObjectURL).toHaveBeenCalledWith('blob:mock');
        // And the anchor is cleaned up rather than left in the DOM.
        expect(document.querySelectorAll('a[download]')).toHaveLength(0);
    });
});
