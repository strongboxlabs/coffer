/** Mirror of API `Coffer.Api.Contracts.NotificationProviderDto`. */
export interface NotificationProvider {
    subscriberKey: string;
    displayName: string;
    /** Whether it can report that something did NOT happen. */
    detectsAbsence: boolean;
    /** Jobs this provider can be bound to; empty when it cannot detect absence. */
    monitors: string[];
}

/** Mirror of API `Coffer.Api.Contracts.NotificationSubscriberDto`. */
export interface NotificationSubscriber {
    id: string;
    subscriberKey: string;
    displayName: string;
    isEnabled: boolean;
    minSeverity: string;
    topics: string[] | null;
    lastSuccessAt: string | null;
    lastFailureAt: string | null;
    lastError: string | null;
    consecutiveFailures: number;
    /** For a heartbeat target: the one monitor this URL watches. Null for a message one. */
    monitors: string | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.NotificationSubscribersResponse`.
 *
 * `monitorCoverage` maps every known monitor to whether a dead-man's switch is bound to
 * it. A map rather than a boolean because one healthchecks URL is one check: "a heartbeat
 * target exists" was rendered as "absence detection is covered" while a target bound to
 * backups said nothing about any other job.
 */
export interface NotificationSubscribersResponse {
    subscribers: NotificationSubscriber[];
    monitorCoverage: Record<string, boolean>;
}

/** Mirror of API `Coffer.Api.Contracts.SystemEventDto`. */
export interface SystemEvent {
    id: string;
    occurredAt: string;
    severity: string;
    topic: string;
    eventKey: string;
    summary: string;
    /** When a later event said this subject is fine again, or null while it is still a
     *  live problem. Derived server-side from the surrounding rows — the event log stays
     *  append-only. */
    resolvedAt: string | null;
}

/**
 * A ledger's notification settings: its own delivery targets, and nothing else.
 * There is no mode — migration 214 retired 'inherit'. An empty list means this
 * ledger reports nowhere, which the panel has to say out loud.
 */
export interface LedgerNotificationSettings {
    subscribers: NotificationSubscriber[];
    /** This ledger's ENABLED scheduled jobs, and whether each has a switch watching it.
     *  Keyed on the ledger's own jobs, so a ledger with snapshots off is not warned
     *  about snapshots. */
    monitorCoverage: Record<string, boolean>;
    /** Monitors a switch IS bound to whose job is not enabled here — checks that can only
     *  ever read "Never". `monitorCoverage` cannot express these: it is keyed on the jobs
     *  that run, so a switch on a job that does not run matches no key at all. */
    monitorsWatchingNothing: string[];
}
