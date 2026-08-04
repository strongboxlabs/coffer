// Whole-DB backup admin surface (ADR-0060). Mirrors the API
// BackupContracts shapes. The encrypted artifact bytes + the passphrase
// never cross this boundary — only metadata + a "configured" flag.

export interface BackupSummary {
    /** Opaque artifact id (the .cofferbak filename stem) used in download/delete/pin. */
    id: string;
    sizeBytes: number;
    createdAtUtc: string;
    /** "Never delete" pin — excluded from local + Drive retention (ADR-0062). */
    pinned: boolean;
}

/** GFS retention policy (ADR-0074). The single source of truth that governs
 *  local backup pruning AND the Google Drive mirror. Admin-editable. */
export interface BackupRetention {
    retentionDaily: number;
    retentionWeekly: number;
    retentionMonthly: number;
}

/** Pre-flight KEK-compatibility result for a restore (ADR-0074). `compatible` =
 *  the backup was sealed under this install's Master KEK; when false, a restore
 *  still brings data + passkeys but the backup passphrase + Drive connection
 *  won't carry over. `hasFingerprint` is false for an older (v1) backup. */
export interface BackupKekCheck {
    hasFingerprint: boolean;
    compatible: boolean;
}

export interface BackupSchedule {
    enabled: boolean;
    hourLocal: number;
    minuteLocal: number;
    timezone: string | null;
    lastRunAt: string | null;
    nextRunAt: string | null;
    /** Whether an admin has set the backup passphrase. The schedule can't be
     *  enabled (and a backup can't run) until this is true. */
    passphraseConfigured: boolean;
}
