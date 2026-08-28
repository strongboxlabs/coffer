import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    fetchLedgerNotificationProviders,
    fetchLedgerEvents,
    fetchLedgerNotifications,
    createLedgerNotificationSubscriber,
    deleteLedgerNotificationSubscriber,
} from '@/lib/api';
import { formatLedgerDateTime } from '@/lib/dates';
import type { NotificationProvider, NotificationSubscriber } from '@/lib/types';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';

/**
 * Per-ledger notifications (ADR-0096): adopt the deployment's targets, or name this
 * ledger's own.
 *
 * Ledger-scoped and grant-gated, unlike the System → Notifications tab. A ledger
 * holder decides where THEIR events go without being an admin and without seeing the
 * deployment's targets — which is the practical payoff of the scope split.
 *
 * "Own" with nothing configured is silence, deliberately. There is no third "none"
 * mode because that state already exists, and two ways to say one thing is one too
 * many.
 */
export function NotificationsPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const key = ['ledger-notifications', ledgerId] as const;

    const settings = useQuery({
        queryKey: key,
        queryFn: () => fetchLedgerNotifications(ledgerId),
        retry: false,
    });
    const providers = useQuery({
        queryKey: ['ledger-notification-providers', ledgerId],
        queryFn: () => fetchLedgerNotificationProviders(ledgerId),
        retry: false,
    });
    const invalidate = () => queryClient.invalidateQueries({ queryKey: key });


    const [subscriberKey, setSubscriberKey] = useState('');
    const [displayName, setDisplayName] = useState('');
    const [url, setUrl] = useState('');
    const [minSeverity, setMinSeverity] = useState('warning');
    // Which job a dead-man's switch on THIS ledger watches. Same rule as the
    // deployment surface: its URL is one check, so it must name one.
    const [monitors, setMonitors] = useState('');

    const add = useMutation({
        mutationFn: () =>
            createLedgerNotificationSubscriber(ledgerId, {
                subscriberKey,
                // Sent at last. Without it the API fell back to the subscriber key, so
                // every target a ledger owner created was called "webhook" — and with
                // one selectable provider that made the list useless for the one
                // operation it offers, deleting the right row.
                displayName: displayName.trim() || undefined,
                url,
                minSeverity,
                monitors: chosen?.detectsAbsence ? monitors : undefined,
            }),
        onSuccess: () => {
            setUrl('');
            setDisplayName('');
            invalidate();
        },
    });
    const remove = useMutation({
        mutationFn: (id: string) => deleteLedgerNotificationSubscriber(ledgerId, id),
        onSuccess: invalidate,
    });

    // What this ledger has actually been told about lately. Read-only over rows the
    // publisher already wrote — no new delivery, no new subscriber.
    const events = useQuery({
        queryKey: ['ledger-events', ledgerId],
        queryFn: () => fetchLedgerEvents(ledgerId),
    });

    const coverage = settings.data?.monitorCoverage ?? {};
    const uncovered = Object.keys(coverage)
        .filter((m) => !coverage[m])
        .sort();

    const chosen = providers.data?.find((p) => p.subscriberKey === subscriberKey);

    return (
        <div className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Notifications</h2>
                <p className="text-sm text-text-muted">
                    Where this ledger&apos;s activity and problems are reported.
                </p>
            </header>

            {settings.isError ? (
                <p role="alert" className="text-sm text-state-danger">
                    {errorMessage(settings.error, 'Could not load notification settings.')}
                </p>
            ) : null}

            <Panel>
                <PanelHead>
                    <span className="font-medium">Where events go</span>
                </PanelHead>
                <PanelBody className="space-y-3">
                    {/* Not a choice any more. This used to offer "use the installation's
                        targets" vs "use only my own", and the first of those was the
                        DEFAULT — so on a shared install every ledger's activity went to
                        whoever watched the deployment channel until someone opted out.
                        Migration 214 moved each ledger's inherited targets into the
                        ledger and retired the mode.

                        One destination for several ledgers is still available, and is
                        now explicit rather than implicit: paste the same URL into each
                        ledger. The delivered payload names the ledger it is about, so
                        the messages stay tellable apart. */}
                    <p className="text-sm text-text-muted">
                        This ledger reports to its own targets only. To send several
                        ledgers to one place, add the same URL to each — every message
                        names the ledger it came from.
                    </p>

                    {/* Named per job, for the same reason the deployment panel does it:
                        one healthchecks URL is one check, so a switch on the quote
                        refresh says nothing whatever about snapshots.

                        Keyed on the jobs this ledger has ENABLED — a ledger with
                        snapshots switched off is not warned about snapshots, because a
                        warning about a job that is not supposed to run is how a channel
                        earns being muted.

                        Gated on having data, not on isError alone: React Query sets
                        isError on a failed background refetch while data still holds the
                        last good response, and downgrading a known "nothing is watching"
                        to "unknown" hides a real warning behind a transient one. */}
                    {settings.data !== undefined && uncovered.length > 0 ? (
                        <p
                            role="alert"
                            className="rounded border border-state-warning/40 bg-state-warning-soft p-2 text-xs text-state-warning"
                        >
                            Nothing is watching for: {uncovered.join(', ')}. If one of
                            these stops running, nobody will be told. Add a heartbeat
                            target bound to each job you want noticed.
                        </p>
                    ) : null}

                    {/* The state someone lands in by adding nothing. Distinguished from a
                        failed load above: no targets is a fact, a failed request is not.
                        Saying nothing here would be the strongest possible claim
                        ("you're covered") made on the strength of an empty list. */}
                    {!settings.isError && settings.data?.subscribers.length === 0 ? (
                        <p
                            role="alert"
                            className="rounded border border-state-warning/40 bg-state-warning-soft p-2 text-xs text-state-warning"
                        >
                            This ledger currently reports nothing anywhere. Nobody will be
                            told if its scheduled jobs stop running or its projections
                            drift.
                        </p>
                    ) : null}
                </PanelBody>
            </Panel>

                <Panel>
                    <PanelHead>
                        <span className="font-medium">My targets</span>
                    </PanelHead>
                    <PanelBody className="space-y-3">
                        {settings.data?.subscribers.length === 0 ? (
                            <p className="text-sm text-text-muted">None yet.</p>
                        ) : null}
                        {settings.data?.subscribers.map((s: NotificationSubscriber) => (
                            <div
                                key={s.id}
                                className="flex items-start justify-between gap-3 border-b border-border-subtle pb-2 last:border-0"
                            >
                                <div className="text-sm">
                                    <p className="font-medium">
                                        {s.displayName}{' '}
                                        <span className="text-text-muted">
                                            ({s.subscriberKey})
                                        </span>
                                    </p>
                                    <p className="text-xs text-text-muted">
                                        at {s.minSeverity} and above
                                    </p>
                                    {s.consecutiveFailures > 0 ? (
                                        <p className="mt-1 text-xs text-state-danger">
                                            {s.consecutiveFailures} consecutive failure
                                            {s.consecutiveFailures === 1 ? '' : 's'}
                                            {s.lastError ? ` — ${s.lastError}` : null}
                                        </p>
                                    ) : null}
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
                                className="mt-1 w-full rounded border border-border-subtle bg-surface p-2"
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

                        {chosen && !chosen.detectsAbsence ? (
                            <p className="text-xs text-state-warning">
                                This target cannot tell you when something fails to
                                happen. Add a heartbeat target as well if you want to be
                                told when a scheduled job stops running.
                            </p>
                        ) : null}

                        {chosen?.detectsAbsence ? (
                            <label className="block text-sm">
                                <span className="text-text-muted">Watches</span>
                                <select
                                    className="mt-1 w-full rounded border border-border-subtle bg-surface p-2"
                                    value={monitors}
                                    onChange={(e) => setMonitors(e.target.value)}
                                >
                                    <option value="">Choose the job…</option>
                                    {(chosen?.monitors ?? []).map((m) => (
                                        <option key={m} value={m}>
                                            {m}
                                        </option>
                                    ))}
                                </select>
                                <span className="mt-1 block text-xs text-text-muted">
                                    One check per job, and per ledger. Re-using a ping URL
                                    across ledgers or jobs means whichever one runs holds
                                    the check green and hides the other going quiet.
                                </span>
                            </label>
                        ) : null}

                        {/* Named by the operator, because the API's fallback is the
                            subscriber key and there is only one selectable provider
                            at ledger scope — so every target came out called
                            "webhook" and the list could not be used to pick which
                            one to delete. */}
                        <label className="block text-sm">
                            <span className="text-text-muted">Name</span>
                            <input
                                type="text"
                                className="mt-1 w-full rounded border border-border-subtle bg-surface p-2"
                                value={displayName}
                                onChange={(e) => setDisplayName(e.target.value)}
                                placeholder="e.g. Discord — household"
                            />
                        </label>

                        <label className="block text-sm">
                            <span className="text-text-muted">URL</span>
                            <input
                                type="url"
                                className="mt-1 w-full rounded border border-border-subtle bg-surface p-2"
                                value={url}
                                onChange={(e) => setUrl(e.target.value)}
                                placeholder="https://…"
                            />
                        </label>
                        <p className="text-xs text-text-muted">
                            Stored encrypted and never shown again.
                        </p>

                        {chosen?.detectsAbsence ? (
                            <p className="text-xs text-text-muted">
                                Reports every run of the job it watches: a success pings
                                the URL, a failure posts to <code>/fail</code> so the
                                check goes red immediately. There is no severity to
                                choose — this URL is one check, and it is either alive or
                                down.
                            </p>
                        ) : (
                            <label className="block text-sm">
                                <span className="text-text-muted">Send at</span>
                                <select
                                    className="mt-1 w-full rounded border border-border-subtle bg-surface p-2"
                                    value={minSeverity}
                                    onChange={(e) => setMinSeverity(e.target.value)}
                                >
                                    <option value="info">Everything, including routine</option>
                                    <option value="warning">Warnings and problems</option>
                                    <option value="critical">
                                        Only problems needing attention
                                    </option>
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
                            disabled={
                                add.isPending || subscriberKey === '' || url.trim() === ''
                            }
                        >
                            {add.isPending ? 'Adding…' : 'Add target'}
                        </Button>
                    </PanelBody>
                </Panel>

            <Panel>
                <PanelHead>
                    <span className="font-medium">Recent problems</span>
                </PanelHead>
                <PanelBody>
                    {/* Warnings and problems only, and the copy says so — otherwise an
                        empty list reads as "nothing has happened" when it means "nothing
                        has gone wrong", and those are very different claims.

                        This is the surface ADR-0096 D5 promised for ledger scope. It is
                        deliberately NOT presented as coverage: D5's own alternatives
                        section rejected "in-app notifications only" because that is the
                        design which produced 68 hours of silence. A page you have to
                        remember to open cannot tell you the app stopped running; that is
                        what the heartbeat target above is for. */}
                    {events.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            Could not load recent problems. This is not saying there have
                            been none — it means the list could not be fetched.
                        </p>
                    ) : events.data === undefined ? (
                        <p className="text-sm text-text-muted">Loading…</p>
                    ) : events.data.length === 0 ? (
                        <p className="text-sm text-text-muted">
                            No warnings or problems recorded. Routine successes are not
                            listed here.
                        </p>
                    ) : (
                        <ul className="space-y-1 text-sm">
                            {events.data.map((e) => (
                                <li
                                    key={e.id}
                                    /* A resolved problem is still shown — vanishing is its
                                       own kind of lie — but it must not read as current.
                                       Muted, not hidden. */
                                    className={
                                        e.resolvedAt
                                            ? 'flex gap-2 text-text-muted line-through decoration-1'
                                            : 'flex gap-2'
                                    }
                                >
                                    {/* WHEN, first. A list headed "recent problems" with no
                                        times reads as a list of CURRENT problems, and the
                                        two are not the same thing: the first real row this
                                        panel ever showed was a six-day-old drift warning
                                        that had already been resolved, indistinguishable
                                        from one raised a minute ago. Recording that
                                        something went wrong without recording when is
                                        half a fact. */}
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
                                                : 'text-state-warning'
                                        }
                                    >
                                        {e.severity}
                                    </span>
                                    <span className="text-text-muted">{e.topic}</span>
                                    <span>{e.summary}</span>
                                    {e.resolvedAt ? (
                                        <span className="shrink-0 text-xs text-state-success">
                                            resolved
                                        </span>
                                    ) : null}
                                </li>
                            ))}
                        </ul>
                    )}
                    <p className="mt-2 text-xs text-text-muted">
                        A list you have to open cannot tell you the app stopped running.
                        That is what a heartbeat target is for.
                    </p>
                </PanelBody>
            </Panel>
        </div>
    );
}
