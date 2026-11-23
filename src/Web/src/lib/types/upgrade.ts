// The Upgrade tab (ADR-0110). Mirrors the API's UpgradeDtos.

/** The latest release as the screen shows it. `notes` is plain text: render it escaped, never as HTML. */
export interface UpgradeRelease {
    version: string;
    notes: string;
    url: string;
    publishedAt: string | null;
}

/** The host updater as the mailbox shows it. `stale` is silent for three ticks; `blocked` is an
 *  earlier run that failed after it began changing the install, until `ack` is run on the host;
 *  `unpinned` is an install tracking the latest tag, which the host cannot upgrade. */
export interface UpgradeHost {
    state: 'running' | 'disabled' | 'stale' | 'absent' | 'blocked' | 'unpinned';
    at: string | null;
}

/**
 * The attempt on screen. `changed` is whether a failed attempt got past the first change to this
 * install: false means nothing was touched, true means it needs attention on the host.
 */
export interface UpgradeAttempt {
    from: string;
    to: string;
    phase: 'backing-up' | 'requested' | 'started' | 'done' | 'failed';
    startedAtUtc: string;
    message: string | null;
    changed: boolean;
}

export interface UpgradeStatus {
    installed: string;
    inAppEnabled: boolean;
    autoCheck: boolean;
    privateRepo: boolean;
    tokenStored: boolean;
    repository: string;
    installDir: string;
    hostNote: string;
    host: UpgradeHost;
    latest: UpgradeRelease | null;
    checkedAt: string | null;
    checkError: string | null;
    updateAvailable: boolean;
    canStart: boolean;
    cannotStartReason: string | null;
    attempt: UpgradeAttempt | null;
}

export interface UpgradeSettings {
    inAppEnabled: boolean;
    autoCheck: boolean;
    privateRepo: boolean;
}
