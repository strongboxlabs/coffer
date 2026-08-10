import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { SetupMasterKey } from './SetupMasterKey';

// First-run master-key display (ADR-0092 D2).

const KEY = 'Zm9vYmFyYmF6cXV1eGZvb2JhcmJhenF1dXhmb28xMjM=';

describe('SetupMasterKey', () => {
    afterEach(() => {
        vi.restoreAllMocks();
    });

    it('gates Finish on the acknowledgement', async () => {
        const onAcknowledge = vi.fn();
        const user = userEvent.setup();
        render(<SetupMasterKey keyBase64={KEY} onAcknowledge={onAcknowledge} />);

        expect(screen.getByText(KEY)).toBeInTheDocument();
        const finish = screen.getByRole('button', { name: /finish setup/i });
        expect(finish).toBeDisabled();

        await user.click(screen.getByRole('checkbox'));
        expect(finish).toBeEnabled();

        await user.click(finish);
        expect(onAcknowledge).toHaveBeenCalledOnce();
    });

    it('says the key can be seen again — not a false last chance', () => {
        render(<SetupMasterKey keyBase64={KEY} onAcknowledge={vi.fn()} />);

        expect(screen.getByText(/see this again later/i)).toBeInTheDocument();
    });

    it('downloads via an in-document anchor and outlives the click', async () => {
        // Both details are load-bearing and easy to "simplify" away: Firefox ignores a
        // click on an anchor that isn't in the document, and Safari cancels the download
        // if the object URL is revoked before the handler resolves. Getting either wrong
        // yields a button that silently does nothing on those browsers — which is
        // exactly what the first version of this component did.
        const createObjectURL = vi.fn(() => 'blob:mock');
        const revokeObjectURL = vi.fn();
        vi.stubGlobal('URL', { ...URL, createObjectURL, revokeObjectURL });

        let anchorInDocumentAtClick = false;
        const clickSpy = vi
            .spyOn(HTMLAnchorElement.prototype, 'click')
            .mockImplementation(function (this: HTMLAnchorElement) {
                anchorInDocumentAtClick = document.body.contains(this);
            });

        render(<SetupMasterKey keyBase64={KEY} onAcknowledge={vi.fn()} />);

        // fireEvent, not userEvent, and that is the whole point. handleDownload is
        // synchronous, so a synchronous dispatch runs it to completion with no
        // event-loop turn in between — which makes the "not yet revoked"
        // assertion below a statement about BEHAVIOUR.
        //
        // `await user.click()` made it a statement about ELAPSED TIME instead: it
        // yields to the event loop, jsdom implements requestAnimationFrame as a
        // ~16ms timer, and on a loaded machine (the parallel preflight) the
        // deferred revoke fired during the click's own internal awaits. The test
        // then failed on a component that was behaving perfectly.
        //
        // Two rejected alternatives, for the next person who wants to "improve"
        // this. Fake timers deadlock userEvent even with `advanceTimers` wired up.
        // And recording "was it revoked before click() fired?" inside the spy is
        // deterministic but weaker — it would miss a revoke issued synchronously
        // just AFTER click(), which is exactly the Safari-cancelling bug this
        // test exists to catch.
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
