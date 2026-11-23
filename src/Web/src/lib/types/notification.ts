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
 * Mirror of API `Coffer.Api.Contracts.LedgerAttentionDto` — one ledger with something
 * outstanding (FU-049).
 *
 * Only ledgers that have something appear, so an install where everything is fine gets
 * an empty array. The reader treats a missing ledger as "nothing to say", which is also
 * what it has before the request finishes — so the quiet case and the loading case
 * render identically and nothing flashes on arrival.
 */
export interface LedgerAttention {
    ledgerId: string;
    /** 'warning' | 'critical' — the highest outstanding on this ledger. */
    severity: string;
    /** Distinct SUBJECTS outstanding, not rows: one standing problem counts once
     *  however many times it has been announced. */
    count: number;
}

/**
 * Mirror of API `OutstandingProblemDto` — one subject still outstanding on a ledger.
 * The same query behind {@link LedgerAttention}, so a ledger lists exactly as many
 * rows as its attention count.
 */
export interface OutstandingProblem {
    /** The subject, with its trailing separator: `backup.`, `feed-sync.`. */
    subjectKey: string;
    topic: string;
    eventKey: string;
    /** The latest problem's: 'warning' | 'critical'. */
    severity: string;
    summary: string;
    latestAt: string;
    /** The earliest problem since the subject's last all-clear. */
    firstUnresolvedAt: string;
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

/**
 * Mirror of API `SystemAttentionSubjectDto` — one deployment-scope subject outstanding on the
 * INSTALLATION (ADR-0102, amended for ADR-0110), as `LedgerAttention` is for a ledger.
 */
export interface SystemAttentionSubject {
    /** The subject without its separator: `backup`, `upgrade`. */
    subject: string;
    /** 'warning' | 'critical' — the latest event's, as for every subject. */
    severity: string;
    since: string;
}

/**
 * Mirror of API `SystemAttentionDto` — what the System-settings gear and the System tabs draw.
 * Admin-only on the wire: a non-admin's request is refused, and the reader never asks.
 */
export interface SystemAttention {
    /** The highest severity beneath the gear, or null for none: one dot per surface. */
    worst: string | null;
    subjects: SystemAttentionSubject[];
    /** The release announced as available while the `upgrade` subject stands. */
    upgradeVersion: string | null;
}
