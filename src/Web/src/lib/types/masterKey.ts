// Wire types for the admin master-KEK surface (ADR-0092 D2/D4). Mirrors
// MasterKeyContracts on the API side.

/**
 * Metadata about the install's master KEK. No key material — safe to hold in
 * query cache and render on load.
 */
export interface MasterKeyStatus {
    /** Id stamped on new wraps (`ledgers.lek_kek_id`), e.g. `v1`. */
    kekId: string;
    /** Where the key file lives, so an operator can find or back it up. */
    path: string;
    /**
     * Hex fingerprint (16 bytes) of the current key. One-way: lets an operator
     * confirm which key an install runs, and match it against a backup's, without
     * seeing the key.
     */
    fingerprint: string;
}

/**
 * The key itself. Returned only from a POST carrying a verified fresh assertion,
 * with `Cache-Control: no-store`.
 *
 * Never put this in the query cache, localStorage, or a URL — hold it in
 * component state for as long as it is on screen and drop it after.
 */
export interface MasterKeyReveal {
    keyBase64: string;
    kekId: string;
}

// No rotate-preview type: rotation runs the dry run itself as its first step and
// refuses before touching anything, so a separate preview only produced a list that
// didn't change the decision (ADR-0092 D4).

/**
 * Result of a committed rotation, carrying the NEW key. Same handling rules as
 * {@link MasterKeyReveal}.
 */
export interface MasterKeyRotation {
    keyBase64: string;
    kekId: string;
    ledgersRotated: number;
    backupPassphraseRotated: boolean;
    driveTokenRotated: boolean;
    /** Where the previous key file was moved, so a mistaken rotation is
     *  reversible. Null when there was no prior file. */
    previousKeyArchivedAt: string | null;
    /** Always true: the server restarts to load the new key. The UI should warn
     *  about a brief reconnect rather than reporting the next failed request as
     *  an error. */
    restartPending: boolean;
}
