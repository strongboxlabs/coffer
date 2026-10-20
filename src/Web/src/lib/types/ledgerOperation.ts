// Provider-run activity types (ADR-0055 slice C). Mirror of the API
// LedgerOperationSummaryDto — one run in the ledger-wide activity timeline.

export interface LedgerOperationSummary {
    id: string;
    /** 'ingest' | 'quote'. */
    family: string;
    /** simplefin | ofx | qif | file | quote-refresh | … */
    providerKey: string;
    /** manual | file-upload | post-sync | scheduled. */
    triggeredVia: string;
    /** running | completed | partial | failed | needs_reauth. */
    status: string;
    /** ISO-8601 UTC timestamp string. */
    startedAt: string;
    completedAt: string | null;
    /** Real user, or the system user (…0001) for scheduled runs. */
    triggeredByUserId: string | null;
    /** Provider-specific counts (from ledger_operations.details) — e.g.
     *  { txns_inserted, txns_already_known } or { prices_inserted, … }. */
    details: Record<string, number>;
    errorCount: number;
}
