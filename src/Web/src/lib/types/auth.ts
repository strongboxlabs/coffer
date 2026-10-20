// Auth-related API response types.

/** Mirror of API `AuthEndpoints.CurrentUserResponse`. */
export interface CurrentUser {
    id: string;
    username: string;
    displayName: string;
    /** Global operator/admin flag (ADR-0060). Gates admin-only surfaces. */
    isAdmin: boolean;
}
