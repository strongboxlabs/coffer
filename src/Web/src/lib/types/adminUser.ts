// Admin user-management types (ADR-0083).

/** Mirror of API `Coffer.Api.Contracts.AdminUserSummary`. */
export interface AdminUser {
    id: string;
    displayName: string;
    username: string | null;
    isAdmin: boolean;
    isDisabled: boolean;
    ledgerCount: number;
}
