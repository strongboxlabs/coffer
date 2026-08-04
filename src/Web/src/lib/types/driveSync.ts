// Google Drive backup sync admin surface (ADR-0062). Mirrors the API
// DriveSyncDtos shapes. The sealed OAuth material + the client secret never
// cross this boundary — only connection status + display metadata.

export interface DriveSyncStatus {
    /** Whether push-on-backup is turned on (requires a connected account). */
    enabled: boolean;
    /** Whether a Google account is connected (a sealed token exists). */
    connected: boolean;
    /** The connected account's email, for display. */
    connectedEmail: string | null;
    /** The Coffer-owned Drive folder name (includes the install id). */
    folderName: string | null;
    /** Stable opaque per-install id, embedded in the folder name so installs
     *  sharing one OAuth client + account stay in distinct folders. */
    installId: string | null;
    lastSyncAt: string | null;
    /** `ok` | `error` | null (never synced). */
    lastSyncStatus: string | null;
    lastSyncError: string | null;
}

/** Response from `connect/start` — the Google consent URL to redirect the
 *  browser to. Google redirects back to Coffer's callback, which completes the
 *  connect server-side and returns the user to System → Backups. */
export interface DriveConnectStart {
    authorizationUrl: string;
}
