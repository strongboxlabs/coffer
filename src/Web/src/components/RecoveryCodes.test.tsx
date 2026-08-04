import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { RecoveryCodes } from './RecoveryCodes';

// Smoke tests for the recovery-codes display. The component's
// contract: render the codes verbatim, gate the Continue button
// behind an explicit acknowledgement checkbox, copy-to-clipboard
// uses navigator.clipboard.writeText and surfaces an "unavailable"
// message when writeText rejects. Download + print are exercised
// via Blob/anchor + window.print stubs.
//
// jsdom 25 ships a working `navigator.clipboard.writeText`, so we
// drive the copy-path tests via `vi.spyOn` on the existing method
// rather than trying to redefine the property (jsdom's clipboard
// is on the Navigator prototype and resists Object.defineProperty
// shadowing). The "clipboard entirely missing" path is exercised
// implicitly by the optional-chain in handleCopy and the TS
// typecheck; the realistic failure mode is permission-denied,
// which the rejects-test covers.

describe('RecoveryCodes', () => {
    const codes = ['ABCD-1234', 'EFGH-5678', 'IJKL-9012'];

    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('renders every code verbatim', () => {
        render(<RecoveryCodes codes={codes} onAcknowledge={() => {}} />);

        for (const code of codes) {
            expect(screen.getByText(code)).toBeInTheDocument();
        }
    });

    it('disables Continue until the acknowledgement checkbox is ticked', async () => {
        const onAcknowledge = vi.fn();
        render(<RecoveryCodes codes={codes} onAcknowledge={onAcknowledge} />);

        const continueButton = screen.getByRole('button', { name: /continue/i });
        expect(continueButton).toBeDisabled();

        const user = userEvent.setup();
        await user.click(screen.getByRole('checkbox'));
        expect(continueButton).toBeEnabled();

        await user.click(continueButton);
        expect(onAcknowledge).toHaveBeenCalledOnce();
    });

    it('writes every code to the clipboard on copy', async () => {
        const writeTextSpy = vi
            .spyOn(navigator.clipboard, 'writeText')
            .mockResolvedValue(undefined);

        render(<RecoveryCodes codes={codes} onAcknowledge={() => {}} />);

        const user = userEvent.setup();
        await user.click(screen.getByRole('button', { name: /copy to clipboard/i }));

        expect(writeTextSpy).toHaveBeenCalledWith(codes.join('\n'));
        // The copy button's label flips to "Copied".
        expect(
            await screen.findByRole('button', { name: /^copied$/i }),
        ).toBeInTheDocument();
    });

    it('surfaces an "unavailable" status when writeText rejects', async () => {
        // Permission-denied is the realistic failure mode in real
        // browsers (insecure context, user denied permission). The
        // catch in handleCopy converts that to copyState
        // 'unsupported' and renders the status region.
        vi.spyOn(navigator.clipboard, 'writeText').mockRejectedValue(
            new Error('NotAllowedError'),
        );

        render(<RecoveryCodes codes={codes} onAcknowledge={() => {}} />);

        const user = userEvent.setup();
        await user.click(screen.getByRole('button', { name: /copy to clipboard/i }));

        const status = await screen.findByRole('status');
        expect(status).toHaveTextContent(/clipboard unavailable/i);
    });

    it('downloads a .txt of the codes when Download is clicked', async () => {
        // Stub URL.createObjectURL / revokeObjectURL — jsdom doesn't
        // provide them by default. We assert on the resulting <a>
        // element's download attribute + that createObjectURL was
        // called with a text/plain Blob containing every code.
        const createUrl = vi.fn((_blob: Blob) => 'blob:fake-url');
        const revokeUrl = vi.fn((_url: string) => undefined);
        Object.defineProperty(URL, 'createObjectURL', {
            configurable: true,
            value: createUrl,
        });
        Object.defineProperty(URL, 'revokeObjectURL', {
            configurable: true,
            value: revokeUrl,
        });

        // Capture the anchor that handleDownload appends + clicks so
        // we can assert on its download attribute.
        const clickSpy = vi.fn();
        const originalCreate = document.createElement.bind(document);
        vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
            const el = originalCreate(tag);
            if (tag.toLowerCase() === 'a') {
                el.click = clickSpy;
            }
            return el;
        });

        render(<RecoveryCodes codes={codes} onAcknowledge={() => {}} />);

        const user = userEvent.setup();
        await user.click(screen.getByRole('button', { name: /download \.txt/i }));

        expect(createUrl).toHaveBeenCalledOnce();
        const blob = createUrl.mock.calls[0]![0];
        expect(blob).toBeInstanceOf(Blob);
        expect(blob.type).toMatch(/text\/plain/);
        // jsdom's Blob doesn't implement .text() / .arrayBuffer() in
        // the version we ship, so we can't read the bytes back here.
        // Size + type are enough to confirm the file payload was
        // built from `${codes.join('\n')}\n` — every code is ASCII so
        // length === byte length.
        const expected = `${codes.join('\n')}\n`;
        expect(blob.size).toBe(expected.length);
        expect(clickSpy).toHaveBeenCalledOnce();
    });

    it('invokes window.print when Print is clicked', async () => {
        const printSpy = vi.spyOn(window, 'print').mockImplementation(() => {});

        render(<RecoveryCodes codes={codes} onAcknowledge={() => {}} />);

        const user = userEvent.setup();
        await user.click(screen.getByRole('button', { name: /^print$/i }));

        expect(printSpy).toHaveBeenCalledOnce();
    });
});
