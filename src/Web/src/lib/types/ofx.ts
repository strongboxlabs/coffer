// DTOs for the OFX/QFX file-upload ingest endpoints
// (ADR-0031 Phase 4). Mirrors the API contracts in
// `src/Api/Contracts/OfxIngestDtos.cs` — keep in sync.

/** One account block discovered in a parsed OFX/QFX file. */
export interface OfxPreviewAccount {
    /** Composite provider-stable key for this statement block. The
     *  SPA echoes this back to the import endpoint so the
     *  orchestrator dispatches the right transactions to the right
     *  Coffer account. Opaque format. */
    providerAccountId: string;
    /** Coarse type from the OFX wire shape: 'bank', 'credit_card',
     *  or 'investment'. Slice 1 supports bank + credit_card only;
     *  investment blocks surface in preview but reject at import. */
    accountType: 'bank' | 'credit_card' | 'investment';
    /** ISO-4217 currency reported on the statement. Null when the
     *  file omitted it. */
    currency: string | null;
    /** Transactions parsed for this account block. */
    transactionCount: number;
}

/** One partial-failure entry from preview or import. */
export interface OfxIngestError {
    code: string;
    message: string;
}

/** Response from `POST /api/ledgers/{lid}/ingest/ofx/preview`. */
export interface OfxPreviewResponse {
    accounts: OfxPreviewAccount[];
    errors: OfxIngestError[];
}

/** Response from `POST /api/ledgers/{lid}/ingest/ofx/import`.
 *  Mirrors the SimpleFIN sync result so the summary panel renders
 *  uniformly across ingest sources. */
export interface OfxImportResponse {
    syncRunId: string;
    accountsDiscovered: number;
    transactionsForReview: number;
    alreadyKnown: number;
    errors: OfxIngestError[];
}
