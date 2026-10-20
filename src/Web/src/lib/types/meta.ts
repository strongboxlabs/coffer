// Installation-wide version metadata (ADR-0044). Payload of
// GET /api/meta/version — the two server-side axes the SPA can't know
// on its own. The UI axis comes from build-time constants instead.

export interface ApiVersion {
    /** Semver release handle, e.g. "0.1.0". */
    version: string;
    /** Git commit count — the monotonic build number. */
    build: number;
    /** Git short SHA, e.g. "68a34b7". */
    commit: string;
    /** Commit date "yyyy-MM-dd", or "" when built outside a checkout. */
    commitDate: string;
}

export interface DbVersion {
    /** Latest applied migration number, the DB's own progression. */
    schemaVersion: number;
    /** Latest migration name, e.g. "118_recompute_holdings_...". */
    script: string;
}

export interface VersionResponse {
    api: ApiVersion;
    db: DbVersion;
}
