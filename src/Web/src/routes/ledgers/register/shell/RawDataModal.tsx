import { Modal } from '@/components/ui/Modal';
import type { RegisterRow } from '@/lib/types/register';

/**
 * The diagnostic "Show raw data" modal, shared by both registers.
 *
 * It lived inside `InvestmentRegisterPage` and was therefore investment-only —
 * not by any decision, but because it was typed on `InvestmentRow` and declared
 * in that file. Nothing in it is investment-specific: it reads
 * `providerRawPayload`, which sits on `RegisterRowBase`, and otherwise dumps
 * whatever row and legs it is handed. A bank row synced from SimpleFIN has
 * exactly the same payload to inspect, and "why did this row import like this?"
 * is asked of bank rows more often, since that is where most feed rows land.
 *
 * Typed on `RegisterRow` (the union's base) so neither register can be the only
 * one that has it again.
 */
export function RawDataModal({
    target,
    legs,
    onClose,
}: {
    target: RegisterRow;
    legs: readonly RegisterRow[];
    onClose: () => void;
}) {
    // The PROVIDER's verbatim JSON is the headline payload — the user
    // asked for "raw SimpleFIN data" and that's what they get. Pretty-
    // printed so the formatting is scannable. NULL when the row was
    // synced before storage existed OR is manual/MD-imported; in that
    // case we fall back to the SPA-side row view (still useful for
    // debugging the rest of the pipeline).
    const provider = target.providerRawPayload;
    const providerPretty = provider
        ? safePrettyJson(provider)
        : null;
    const fallback = JSON.stringify({ row: target, legs }, null, 2);
    const json = providerPretty ?? fallback;
    const hasProvider = providerPretty !== null;

    return (
        <Modal open onClose={onClose} titleId="raw-data-title" className="max-w-3xl">
            <div className="flex max-h-[80vh] flex-col gap-3 p-4">
                <div className="flex items-center justify-between gap-2">
                    <h2 id="raw-data-title" className="text-sm font-semibold text-text">
                        {hasProvider ? 'Raw provider data' : 'Raw row data'}
                        {!hasProvider ? (
                            <span className="ml-2 text-[0.6875rem] font-normal text-text-muted">
                                (provider payload not captured — synced before storage existed; re-sync to backfill)
                            </span>
                        ) : null}
                    </h2>
                    <button
                        type="button"
                        onClick={onClose}
                        className="rounded px-2 py-1 text-xs text-text-muted hover:bg-surface-hover hover:text-text"
                        aria-label="Close"
                    >
                        Close
                    </button>
                </div>
                <pre className="flex-1 overflow-auto rounded bg-surface-muted p-3 text-[0.6875rem] font-mono leading-tight text-text">
                    {json}
                </pre>
                <div className="flex justify-end">
                    <button
                        type="button"
                        onClick={() => {
                            void navigator.clipboard.writeText(json);
                        }}
                        className="rounded border border-border px-2 py-1 text-xs text-text-muted hover:bg-surface-hover hover:text-text"
                    >
                        Copy JSON
                    </button>
                </div>
            </div>
        </Modal>
    );
}

function safePrettyJson(raw: string): string {
    try {
        return JSON.stringify(JSON.parse(raw), null, 2);
    } catch {
        // Malformed JSON: show the original string so we can still
        // debug. Shouldn't happen in practice — the server stores
        // the value as JSONB which validates on write.
        return raw;
    }
}
