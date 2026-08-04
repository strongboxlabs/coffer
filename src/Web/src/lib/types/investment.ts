// Investment-domain transaction types — A4.c.3 editor + endpoint
// surface (ADR-0029). Universal register types live in
// [./register.ts]; bank-domain editor types in [./bank.ts].

/**
 * One of the investment actions: the 9 ADR-0027 catalog values plus
 * `transfer_shares` (in-kind share move between two investment
 * accounts — ADR-0065). The editor's action picker
 * (`InvestmentTxnRowEdit`) exposes these as user-facing entries;
 * direction for `misc` and `transfer` is sign-discriminated (no
 * separate picker entries per ADR-0029).
 */
export type LedgerInvestmentAction =
    | 'buy' | 'buyx' | 'sell' | 'sellx'
    | 'dividend_cash' | 'dividend_reinvest' | 'divx'
    | 'transfer' | 'misc'
    // In-kind share move between two investment accounts (ADR-0065):
    // moves FIFO lots + cost basis, zero realized gain.
    | 'transfer_shares';

/**
 * Mirror of API `Coffer.Api.Contracts.CreateInvestmentTransactionRequest`
 * (ADR-0029). One investment txn — multi-posting under the hood, but
 * the wire shape speaks the user-facing field set (action × required
 * fields). Server validates against the action × field matrix.
 */
export interface CreateInvestmentTransactionRequest {
    brokerageAccountId: string;
    /** ISO-8601 UTC timestamp string. */
    postedAt: string;
    action: LedgerInvestmentAction;
    payee?: string | null;
    memo?: string | null;
    checkNumber?: string | null;
    /** ISO-8601 UTC timestamp string. */
    transactedAt?: string | null;
    securityId?: string | null;
    shares?: number | null;
    price?: number | null;
    amount?: number | null;
    categoryAccountId?: string | null;
    transferAccountId?: string | null;
    feeAccountId?: string | null;
    feeAmount?: number | null;
    /** ADR-0031 Phase 3d.2 — mirrors PATCH; see PatchInvestmentTransactionRequest. */
    providerSecurityHint?: ProviderSecurityHint | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.PatchInvestmentTransactionRequest`.
 * Same shape; PATCH semantics per ADR-0025 are "supplied set IS the
 * new state" — null on a field means null in the saved state, not
 * "leave alone."
 */
export interface PatchInvestmentTransactionRequest {
    brokerageAccountId?: string | null;
    postedAt?: string | null;
    action?: LedgerInvestmentAction | null;
    payee?: string | null;
    memo?: string | null;
    checkNumber?: string | null;
    transactedAt?: string | null;
    securityId?: string | null;
    shares?: number | null;
    price?: number | null;
    amount?: number | null;
    categoryAccountId?: string | null;
    transferAccountId?: string | null;
    feeAccountId?: string | null;
    feeAmount?: number | null;
    /**
     * ADR-0031 Phase 3d.2: optional provider-mapping hint. Mirror of
     * `Coffer.Api.Contracts.ProviderSecurityHint`. When supplied
     * alongside a resolved `securityId`, the server records
     * `(ledger, providerKey, providerSecurityId) → securityId` in
     * `provider_security_mappings` so the next sync of the same
     * ticker auto-resolves without prompting.
     */
    providerSecurityHint?: ProviderSecurityHint | null;
    /**
     * Investment merge (mirrors bank `mergeFromHeaderId`). When set, the
     * PATCHed row (the edited row) is the LOSER and folds into this
     * candidate (the surviving winner). Sent as a merge-only PATCH — no
     * other fields are read. See `InvestmentMergeCandidate`.
     */
    mergeFromHeaderId?: string | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.InvestmentMergeCandidateDto`. One
 * "possible match" surfaced in the editor's merge panel; picking one folds
 * the edited (fresh, needs-review) row into it. Shaped for a one-line chip
 * (date · action · ticker · shares · amount).
 */
export interface InvestmentMergeCandidate {
    headerId: string;
    /** ISO-8601 UTC timestamp string (effective, override-aware). */
    postedAt: string;
    /** Signed day offset from the edited row (chip subtitle). */
    dayDelta: number;
    action: LedgerInvestmentAction | null;
    securityTicker: string | null;
    shares: number | null;
    unitPrice: number | null;
    amount: number;
    payee: string | null;
}

/** Mirror of API `Coffer.Api.Contracts.ProviderSecurityHint`. */
export interface ProviderSecurityHint {
    providerKey: string;
    providerSecurityId: string;
}

/** Mirror of API `CreateInvestmentTransactionResponse`. */
export interface CreateInvestmentTransactionResponse {
    headerId: string;
}

/**
 * Mirror of API `InvestmentLotDto` — one open lot for the editor's
 * FIFO consumption preview (ADR-0029). Used by A4.c.4 (preview
 * popover); included here so the API client signature is complete.
 */
export interface InvestmentLotDto {
    lotId: string;
    /** ISO-8601 UTC timestamp string. */
    acquiredAt: string;
    quantity: number;
    unitCost: number;
}
