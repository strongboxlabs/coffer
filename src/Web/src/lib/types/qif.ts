// DTOs for the QIF file-upload ingest endpoints (ADR-0042).
// Mirrors the API contracts in `src/Api/Contracts/QifIngestDtos.cs`
// — keep in sync. Structurally identical to the OFX ingest types;
// kept separate so the QIF surface stays self-contained.

/** The single account block discovered in a parsed QIF file. QIF is
 *  single-account-implicit, so there's always exactly one. */
export interface QifPreviewAccount {
    /** Sentinel provider key ('qif') — QIF carries no account
     *  metadata. The SPA echoes it back to the import endpoint. */
    providerAccountId: string;
    /** 'investment' for `!Type:Invst` files, 'bank' for
     *  `!Type:Bank` / `!Type:CCard`. */
    accountType: 'bank' | 'investment';
    /** Always null — QIF carries no currency. */
    currency: string | null;
    /** Count of supported transactions parsed. */
    transactionCount: number;
}

/** One partial-failure entry from preview or import (e.g. an
 *  unsupported QIF investment action that was skipped). */
export interface QifIngestError {
    code: string;
    message: string;
}

/** Response from `POST /api/ledgers/{lid}/ingest/qif/preview`. */
export interface QifPreviewResponse {
    accounts: QifPreviewAccount[];
    errors: QifIngestError[];
}

/** Response from `POST /api/ledgers/{lid}/ingest/qif/import`.
 *  Mirrors the OFX import / SimpleFIN sync result so the summary
 *  panel renders uniformly across ingest sources. */
export interface QifImportResponse {
    syncRunId: string;
    accountsDiscovered: number;
    transactionsForReview: number;
    alreadyKnown: number;
    errors: QifIngestError[];
}
