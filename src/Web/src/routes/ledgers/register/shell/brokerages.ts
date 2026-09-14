import { importFidelity, previewFidelity } from '@/lib/api';
import type { OfxImportResponse, OfxPreviewResponse } from '@/lib/types';

/**
 * The brokerage CSV exports Coffer can read (ADR-0031 Phase 6).
 *
 * <b>A list, not a sniffer.</b> Detecting the brokerage from the file's header was the
 * obvious alternative and it is worse in the way that matters: sniffing cannot answer
 * "what do you support?". Someone holding a Schwab export learns only that their file
 * could not be read, which is indistinguishable from a bug. A list says "Schwab is not
 * here yet", which is a fact they can act on. It also degrades badly as the list grows
 * and headers converge — a wrong guess is invisible, where a wrong selection is not.
 *
 * <b>Investment accounts only.</b> The generic Phase 5 mapping describes a bank shape —
 * date, payee, one signed amount — so pointing it at a brokerage export does not fail,
 * it silently imports a share purchase as a cash withdrawal. That option is not offered
 * here at all, which is the more valuable half of splitting the two.
 */
export interface Brokerage {
    /** The server's IFileProvider.ProviderKey, and what gets remembered on the account. */
    key: string;
    label: string;
    /** Which export of theirs, in the brokerage's own words, so it can be found. */
    exportName: string;
    preview: (ledgerId: string, file: Blob) => Promise<OfxPreviewResponse>;
    import: (
        ledgerId: string,
        file: Blob,
        accountId: string,
        providerAccountId: string,
    ) => Promise<OfxImportResponse>;
}

export const BROKERAGES: readonly Brokerage[] = [
    {
        key: 'csv-fidelity',
        label: 'Fidelity',
        exportName: 'Activity & Orders',
        // Called through, not captured. Storing the function VALUES here binds whatever
        // the module held at load time, which makes the client unmockable and quietly
        // decides that this list owns the reference. An arrow resolves the live binding
        // at call time instead.
        preview: (ledgerId, file) => previewFidelity(ledgerId, file),
        import: (ledgerId, file, accountId, providerAccountId) =>
            importFidelity(ledgerId, file, accountId, providerAccountId),
    },
];

/**
 * The brokerage for a remembered provider key, or null.
 *
 * Null for an unrecognised key rather than throwing: the key is written by whichever
 * provider last imported, and a provider that is later renamed or removed should leave
 * the picker unselected, not break the dialog.
 */
export function brokerageFor(providerKey: string | null | undefined): Brokerage | null {
    if (!providerKey) return null;
    return BROKERAGES.find((b) => b.key === providerKey) ?? null;
}
