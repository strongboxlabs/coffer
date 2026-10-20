// Ledger membership types (ADR-0083).

/** Mirror of API `Coffer.Api.Contracts.LedgerMember`. */
export interface LedgerMember {
    userId: string;
    displayName: string;
    username: string | null;
    role: string;
}
