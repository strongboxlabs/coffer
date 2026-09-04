import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    fetchNotificationProviders,
    fetchNotificationSubscribers,
    createNotificationSubscriber,
    deleteNotificationSubscriber,
    fetchSystemEvents,
} from '@/lib/api';
import type { NotificationProvider, NotificationSubscriber } from '@/lib/types';
import { formatLedgerDateTime } from '@/lib/dates';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';

const PROVIDERS_KEY = ['admin-notification-providers'] as const;
const SUBSCRIBERS_KEY = ['admin-notification-subscribers'] as const;
const EVENTS_KEY = ['admin-system-events'] as const;

/**
 * Admin — where deployment-scope notifications go (ADR-0096).
 *
 * The load-bearing element on this screen is the absence-detection warning. A
 * message-only target (Discord, ntfy, Slack) reaches a phone in seconds when
 * something breaks and says nothing at all when the deployment is dead — which is
 * the failure that ran for 68 hours unnoticed. Someone who configures a chat webhook
 * and sees a test message arrive will reasonably believe they are covered, so this
 * panel says plainly when they are not.
 */
export function NotificationsPanel() {
    const queryClient = useQueryClient();
    const providers = useQuery({
        queryKey: PROVIDERS_KEY,
        queryFn: fetchNotificationProviders,
        retry: false,
    });
    const subscribers = useQuery({
        queryKey: SUBSCRIBERS_KEY,
        queryFn: fetchNotificationSubscribers,
        retry: false,
    });
    const events = useQuery({
        queryKey: EVENTS_KEY,
        queryFn: fetchSystemEvents,
        retry: false,
    });

    const invalidate = () => {
        queryClient.invalidateQueries({ queryKey: SUBSCRIBERS_KEY });
        queryClient.invalidateQueries({ queryKey: EVENTS_KEY });
    };

    const [subscriberKey, setSubscriberKey] = useState('');
    const [url, setUrl] = useState('');
    const [minSeverity, setMinSeverity] = useState('warning');
    // The monitor a heartbeat target will watch. The list comes from the API's coverage
    // map rather than being hardcoded here, so a new job appears without a UI change.
    const [monitors, setMonitors] = useState('');

    const coverage = subscribers.data?.monitorCoverage ?? {};
    const uncovered = Object.keys(coverage).filter((m) => !coverage[m]).sort();


    const add = useMutation({
        mutationFn: () =>
            createNotificationSubscriber({
                subscriberKey,
                url,
                // The default, not whatever was last picked while a message provider
                // was selected. The field is hidden for these, and storing a value the
                // operator cannot see and the router never reads is how a row starts
                // disagreeing with the screen that created it.
                minSeverity: chosen?.detectsAbsence ? 'warning' : minSeverity,
                // Sent only for a heartbeat provider; the API rejects it on a message
                // one, because a message target is bound to nothing.
                monitors: chosen?.detectsAbsence ? monitors : undefined,
            }),
        onSuccess: () => {
            // Clear the URL specifically: it is a credential, and it is write-only
            // server-side, so leaving it in the form is the only place it lingers.
            setUrl('');
            invalidate();
        },
    });
    const remove = useMutation({
        mutationFn: (id: string) => deleteNotificationSubscriber(id),
        onSuccess: invalidate,
    });

    const chosen = providers.data?.find((p) => p.subscriberKey === subscriberKey);

    return (
        <div className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Notifications</h2>
                <p className="text-sm text-text-muted">
                    Where this installation reports backups, snapshots and problems.
                </p>
            </header>

            {/* A failed subscribers query used to leave `coverage` empty, so `uncovered`
                was empty and this banner simply vanished — the strongest possible
                claim ("everything is watched") made on the strength of no data at
                all. Say what happened instead.

                Gated on there being no data rather than on isError alone. isError is
                also true when a BACKGROUND refetch fails while `data` still holds the
                last good response, and in that case downgrading a known "nothing is
                watching backups" to "unknown right now" hides a real warning behind a
                transient one. No data is unknown; stale data is still data. */}
            {subscribers.isError && subscribers.data === undefined ? (
                <div
                    role="alert"
                    className="rounded border border-state-danger/40 bg-state-danger-soft p-3 text-sm text-state-danger"
                >
                    <p className="font-medium">Could not load delivery targets.</p>
                    <p className="mt-1 text-xs">
                        Whether anything is watching for silence is unknown right now —
                        this is not a statement that it is covered.
                    </p>
                </div>
            ) : uncovered.length > 0 ? (
                <div
                    role="alert"
                    className="rounded border border-state-warning/40 bg-state-warning-soft p-3 text-sm text-state-warning"
                >
                    <p className="font-medium">
                        Nothing is watching for: {uncovered.join(', ')}.
                    </p>
                    <p className="mt-1 text-xs">
                        Named per job on purpose. One healthchecks URL is one check, so a
                        target watching backups says nothing about anything else — and a
                        message target says nothing at all when this install stops
                        running, which is the failure that goes unnoticed longest. Add a
                        heartbeat target bound to each job you want noticed.
                    </p>
                </div>
            ) : null}

            <Panel>
                <PanelHead>
                    <span className="font-medium">Delivery targets</span>
                </PanelHead>
                <PanelBody className="space-y-3">
                    {subscribers.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(subscribers.error, 'Could not load targets.')}
                        </p>
                    ) : null}
                    {subscribers.data?.subscribers.length === 0 ? (
                        <p className="text-sm text-text-muted">
                            No targets configured — nothing is being told anything.
                        </p>
                    ) : null}
                    {subscribers.data?.subscribers.map((s: NotificationSubscriber) => (
                        <div
                            key={s.id}
                            className="flex items-start justify-between gap-3 border-b border-border pb-2 last:border-0"
                        >
                            <div className="text-sm">
                                <p className="font-medium">
                                    {s.displayName}{' '}
                                    <span className="text-text-muted">({s.subscriberKey})</span>
                                </p>
                                {/* Two different kinds of target, described in their own
                                    terms. A dead-man's switch is not routed by severity
                                    or topic — Wants() matches it on monitor name and
                                    signal and never reads either field — so rendering
                                    "at warning and above · all topics" on one states two
                                    facts that are both untrue of it. */}
                                <p className="text-xs text-text-muted">
                                    {s.monitors
                                        ? `watches ${s.monitors} · pings on success, /fail on failure`
                                        : `at ${s.minSeverity} and above` +
                                          (s.topics && s.topics.length > 0
                                              ? ` · ${s.topics.join(', ')}`
                                              : ' · all topics')}
                                </p>
                                {/* Delivery health, surfaced rather than logged: a target
                                    that quietly stopped working must not look identical
                                    to one that works. */}
                                {s.consecutiveFailures > 0 ? (
                                    <p className="mt-1 text-xs text-state-danger">
                                        {s.consecutiveFailures} consecutive failure
                                        {s.consecutiveFailures === 1 ? '' : 's'}
                                        {s.lastError ? ` — ${s.lastError}` : null}
                                    </p>
                                ) : s.lastSuccessAt ? (
                                    <p className="mt-1 text-xs text-state-success">
                                        delivering
                                    </p>
                                ) : (
                                    <p className="mt-1 text-xs text-text-muted">
                                        nothing delivered yet
                                    </p>
                                )}
                            </div>
                            <Button
                                type="button"
                                variant="secondary"
                                className="shrink-0"
                                onClick={() => remove.mutate(s.id)}
                                disabled={remove.isPending}
                            >
                                Remove
                            </Button>
                        </div>
                    ))}
                </PanelBody>
            </Panel>

            <Panel>
                <PanelHead>
                    <span className="font-medium">Add a target</span>
                </PanelHead>
                <PanelBody className="space-y-3">
                    <label className="block text-sm">
                        <span className="text-text-muted">Provider</span>
                        <select
                            className="mt-1 w-full rounded border border-border bg-surface p-2"
                            value={subscriberKey}
                            onChange={(e) => setSubscriberKey(e.target.value)}
                        >
                            <option value="">Choose…</option>
                            {providers.data?.map((p: NotificationProvider) => (
                                <option key={p.subscriberKey} value={p.subscriberKey}>
                                    {p.displayName}
                                    {p.detectsAbsence ? ' — detects silence' : ''}
                                </option>
                            ))}
                        </select>
                    </label>

                    {/* Said at the point of choosing, not only in a banner afterwards:
                        this is where the decision is actually made. */}
                    {chosen && !chosen.detectsAbsence ? (
                        <p className="text-xs text-state-warning">
                            This target cannot tell you when something fails to happen.
                        </p>
                    ) : null}

                    {/* A dead-man's switch is bound to ONE job, because its URL IS one
                        check. Asked here rather than inferred, since an unbound target
                        would be accepted-looking and receive nothing. */}
                    {chosen?.detectsAbsence ? (
                        <label className="block text-sm">
                            <span className="text-text-muted">Watches</span>
                            <select
                                className="mt-1 w-full rounded border border-border bg-surface p-2"
                                value={monitors}
                                onChange={(e) => setMonitors(e.target.value)}
                            >
                                <option value="">Choose the job…</option>
                                {(chosen?.monitors ?? []).map((m) => (
                                    <option key={m} value={m}>
                                        {m}
                                        {coverage[m] ? ' (already watched)' : ''}
                                    </option>
                                ))}
                            </select>
                        </label>
                    ) : null}

                    <label className="block text-sm">
                        <span className="text-text-muted">
                            URL {chosen?.detectsAbsence ? '(the ping URL)' : '(the webhook)'}
                        </span>
                        <input
                            type="url"
                            className="mt-1 w-full rounded border border-border bg-surface p-2"
                            value={url}
                            onChange={(e) => setUrl(e.target.value)}
                            placeholder="https://…"
                        />
                    </label>
                    <p className="text-xs text-text-muted">
                        Stored encrypted and never shown again — a webhook URL usually
                        carries its own token.
                    </p>

                    {/* Severity is a MESSAGE-target control. NotificationPublisher.Wants
                        returns on monitor name + signal for a heartbeat target and never
                        reaches the severity floor, so offering the choice here would be
                        offering a control that changes nothing — the same
                        looks-configured-does-nothing shape this screen exists to warn
                        about. Same rule the Topics field above already follows.

                        What a healthchecks target does instead is not a severity at all:
                        HealthchecksSubscriber pings the URL on Success and posts to
                        URL + "/fail" on Failure, so a failed job marks the check down at
                        once rather than waiting out its grace period. Said in the hint
                        below, because it is the question the missing dropdown provokes. */}
                    {chosen?.detectsAbsence ? (
                        <p className="text-xs text-text-muted">
                            Reports every run of the job it watches: a success pings the
                            URL, a failure posts to <code>/fail</code> so the check goes
                            red immediately. There is no severity to choose — this URL is
                            one check, and it is either alive or down.
                        </p>
                    ) : (
                        <label className="block text-sm">
                            <span className="text-text-muted">Send at</span>
                            <select
                                className="mt-1 w-full rounded border border-border bg-surface p-2"
                                value={minSeverity}
                                onChange={(e) => setMinSeverity(e.target.value)}
                            >
                                <option value="info">Everything, including routine</option>
                                <option value="warning">Warnings and problems</option>
                                <option value="critical">Only problems needing attention</option>
                            </select>
                        </label>
                    )}

                    {add.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(add.error, 'Could not add the target.')}
                        </p>
                    ) : null}

                    <Button
                        type="button"
                        onClick={() => add.mutate()}
                        disabled={add.isPending || subscriberKey === '' || url.trim() === ''}
                    >
                        {add.isPending ? 'Adding…' : 'Add target'}
                    </Button>
                </PanelBody>
            </Panel>

            <Panel>
                <PanelHead>
                    <span className="font-medium">Recent events</span>
                </PanelHead>
                <PanelBody>
                    {/* Three states, kept apart on purpose. On error `events.data` is
                        undefined, so the old `data?.length === 0` test was false and
                        this fell through to render an EMPTY LIST — a failed query looked
                        exactly like a quiet install. That is the one confusion this
                        subsystem exists to remove, reproduced in the surface that
                        reports it. */}
                    {events.isPending ? (
                        <p className="text-sm text-text-muted">Loading…</p>
                    ) : events.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            Could not load recent events. This is not saying the install
                            has been quiet — it means the list could not be fetched.
                        </p>
                    ) : events.data.length === 0 ? (
                        <p className="text-sm text-text-muted">Nothing recorded yet.</p>
                    ) : (
                        <ul className="space-y-1 text-sm">
                            {events.data.slice(0, 20).map((e) => (
                                <li key={e.id} className="flex gap-2">
                                    {/* Same omission as the ledger list had, and the same
                                        reason it matters: "recent events" with no times
                                        cannot distinguish something happening now from
                                        something that happened last week. */}
                                    <time
                                        dateTime={e.occurredAt}
                                        className="shrink-0 tabular-nums text-text-muted"
                                    >
                                        {formatLedgerDateTime(e.occurredAt)}
                                    </time>
                                    <span
                                        className={
                                            e.severity === 'critical'
                                                ? 'text-state-danger'
                                                : e.severity === 'warning'
                                                  ? 'text-state-warning'
                                                  : 'text-text-muted'
                                        }
                                    >
                                        {e.severity}
                                    </span>
                                    <span className="text-text-muted">{e.topic}</span>
                                    <span>{e.summary}</span>
                                </li>
                            ))}
                        </ul>
                    )}
                </PanelBody>
            </Panel>
        </div>
    );
}
