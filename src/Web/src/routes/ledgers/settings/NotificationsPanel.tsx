import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

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
 * `NotificationTopics` (src/Api/Notifications/NotificationEvent.cs:40-51) in English.
 *
 * The API's vocabulary is a contract, not prose: a subscriber filters on `consistency`
 * and `consistency.drift` keys the repair link in the events list, so the translation
 * happens here at the point of reading rather than at the API, where renaming would
 * break both. An unrecognised topic renders exactly as the API sent it — a subject this
 * build has not been taught should look unfamiliar, not invisible.
 */
const SUBJECT_WORDS: Record<string, string> = {
    backup: 'Backups',
    snapshot: 'Snapshots',
    sync: 'Account sync',
    quotes: 'Price quotes',
    consistency: 'Data consistency',
    scheduler: 'Scheduled jobs',
};

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
                // The default, not whatever was last picked while a message provider was
                // selected. The field is hidden for a heartbeat, and storing a value the
                // owner cannot see and the router never reads is how a row starts
                // disagreeing with the screen that created it. Mirrors the deployment
                // panel, which already did this.
                minSeverity: chosen?.detectsAbsence ? 'warning' : minSeverity,
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
        // Same as the two queries above, and for a reason this one felt: without it a
        // failed fetch spends the whole retry window showing "Loading…", so the
        // role="alert" branch that distinguishes a broken request from a quiet ledger
        // is correct and effectively delayed.
        retry: false,
    });

    // "Is anything wrong RIGHT NOW" is the question this panel is opened to ask, and a
    // flat chronological list cannot answer it — a drift fixed last week and one raised
    // a minute ago are the same row shape. Counted here so the panel head can answer it
    // before the reader starts reading rows.
    const openProblems = (events.data ?? []).filter((e) => e.resolvedAt === null);
    const openCount = openProblems.length;
    // One critical among four warnings must not be reported in amber.
    const openIsCritical = openProblems.some((e) => e.severity === 'critical');

    const coverage = settings.data?.monitorCoverage ?? {};
    const uncovered = Object.keys(coverage)
        .filter((m) => !coverage[m])
        .sort();

    // The inverse of `uncovered`, and not derivable from it. Coverage is keyed on the
    // jobs this ledger RUNS, so a switch bound to a job that is switched off matches no
    // key and appears nowhere above. That is the state a real "Coffer Bank Feed" check
    // sat in: bound, never pinged, with nothing on this page saying why.
    const watchingNothing = settings.data?.monitorsWatchingNothing ?? [];

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

                    {/* Gated the same way as the warning above, for the same reason: a
                        failed background refetch must not downgrade a known problem to
                        silence. This one is the opposite complaint — a switch exists and
                        watches a job that is not scheduled, so it can never be pinged and
                        a heartbeat service will eventually call it down. */}
                    {settings.data !== undefined && watchingNothing.length > 0 ? (
                        <p
                            role="alert"
                            className="rounded border border-state-warning/40 bg-state-warning-soft p-2 text-xs text-state-warning"
                        >
                            Watching a job that is not scheduled:{' '}
                            {watchingNothing.join(', ')}. These checks will never be
                            pinged, so they stay at &quot;never&quot; and a heartbeat
                            service will eventually report them as down. Either turn the
                            job on or remove the switch.
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
                                className="flex items-start justify-between gap-3 border-b border-border pb-2 last:border-0"
                            >
                                <div className="text-sm">
                                    <p className="font-medium">
                                        {s.displayName}{' '}
                                        <span className="text-text-muted">
                                            ({s.subscriberKey})
                                        </span>
                                    </p>
                                    {/* Two kinds of target, described in their own
                                        terms. A dead-man's switch is not routed by
                                        severity or topic: Wants() branches on capability
                                        FIRST and the heartbeat arm returns on monitor
                                        name and signal without ever reading MinSeverity,
                                        so "at warning and above" stated a filter that
                                        does not exist on this row.
                                        
                                        It was not merely decorative, it was backwards. A
                                        successful run publishes at info, which is BELOW
                                        a warning floor — if that line were true every
                                        success ping would be dropped and the check would
                                        go red from inactivity. It reads "warning" only
                                        because the create form posts its hidden default.
                                        
                                        The deployment panel fixed this already; this was
                                        the un-updated copy of the same markup. */}
                                    <p className="text-xs text-text-muted">
                                        {s.monitors
                                            ? `watches ${s.monitors} · pings on success, /fail on failure`
                                            : `at ${s.minSeverity} and above` +
                                              (s.topics && s.topics.length > 0
                                                  ? ` · ${s.topics.join(', ')}`
                                                  : ' · all topics')}
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
                                    className="mt-1 w-full rounded border border-border bg-surface p-2"
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
                                className="mt-1 w-full rounded border border-border bg-surface p-2"
                                value={displayName}
                                onChange={(e) => setDisplayName(e.target.value)}
                                placeholder="e.g. Discord — household"
                            />
                        </label>

                        <label className="block text-sm">
                            <span className="text-text-muted">URL</span>
                            <input
                                type="url"
                                className="mt-1 w-full rounded border border-border bg-surface p-2"
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
                                    className="mt-1 w-full rounded border border-border bg-surface p-2"
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
                    {/* The glance-level answer, where the eye lands before it
                        reaches the list. Said only once the fetch has actually
                        returned rows: a count is a claim, and a pending or failed
                        query has no standing to make one — which is also what keeps
                        the error and empty branches below unchanged. */}
                    {!events.isError &&
                    events.data !== undefined &&
                    events.data.length > 0 ? (
                        openCount > 0 ? (
                            <span
                                className={
                                    openIsCritical
                                        ? 'rounded border border-state-danger/40 bg-state-danger-soft px-1.5 py-0.5 text-xs font-medium text-state-danger'
                                        : 'rounded border border-state-warning/40 bg-state-warning-soft px-1.5 py-0.5 text-xs font-medium text-state-warning'
                                }
                            >
                                {openCount} open
                            </span>
                        ) : (
                            /* Deliberately not the same sentence as the empty
                               state below, because it is not the same fact:
                               things went wrong here and were fixed. */
                            <span className="text-xs text-text-muted">Nothing open</span>
                        )
                    ) : null}
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
                        <ul className="divide-y divide-border">
                            {events.data.map((e) => {
                                const settled = e.resolvedAt !== null;
                                /* Colour drains on settle rather than striking the row
                                   out. text-decoration propagates to descendants that
                                   cannot opt out, which is how the green "resolved"
                                   badge ended up crossed out by the resolution it was
                                   announcing. Draining does the same job better:
                                   scanning the severity column, colour means STILL
                                   OPEN, so settling a problem removes it from the scan
                                   without removing it from the list.

                                   The third branch is not decoration. The endpoint
                                   filters `info` out today, so it is unreachable — but
                                   the two-branch version this replaces painted an info
                                   row amber the day that filter is loosened to show
                                   all-clears. The sibling system panel already has all
                                   three; this one had drifted. */
                                const tone = settled
                                    ? 'text-text-subtle'
                                    : e.severity === 'critical'
                                      ? 'text-state-danger'
                                      : e.severity === 'warning'
                                        ? 'text-state-warning'
                                        : 'text-text-muted';
                                return (
                                    <li
                                        key={e.id}
                                        /* Fixed gutter, not shrink-to-fit: every summary
                                           starts at the same x whatever the severity
                                           word is, so the sentences read as one column
                                           and the classification as another. 7rem is
                                           measured against the longest subject word
                                           ("Data consistency") at text-xs, not derived;
                                           a longer one wraps inside the gutter rather
                                           than pushing the summaries right, which is the
                                           correct failure mode. minmax(0,1fr) is
                                           load-bearing — without it a three-sentence
                                           scheduler critical refuses to wrap. */
                                        className="grid grid-cols-[7rem_minmax(0,1fr)] items-baseline gap-x-3 gap-y-1 py-2.5 first:pt-0 last:pb-0"
                                    >
                                        {/* Gutter, line 1: how bad. The house
                                            micro-label recipe (FieldLabel.tsx:17).
                                            `uppercase` is CSS only — the DOM text stays
                                            the raw DTO value, so no severity word is
                                            invented and every text query still matches
                                            what the API sent. */}
                                        <span
                                            className={`text-[0.625rem] font-semibold uppercase tracking-wider ${tone}`}
                                        >
                                            {e.severity}
                                        </span>

                                        {/* Content, line 1: what happened. The primary
                                            line, the only one at body size, and the only
                                            one that gets to wrap. */}
                                        <p
                                            className={
                                                settled
                                                    ? 'text-sm leading-snug text-text-muted'
                                                    : 'text-sm leading-snug text-text'
                                            }
                                        >
                                            {e.summary}
                                        </p>

                                        {/* Gutter, line 2: which area, in words. `topic`
                                            is a filter key in the API contract, not
                                            English — a subscriber filters on
                                            `consistency` and `consistency.drift` keys
                                            the repair link below, so the translation
                                            belongs here at the point of reading rather
                                            than at the API where renaming would break
                                            both. An unrecognised topic renders as sent:
                                            a subject this build has not been taught
                                            should look unfamiliar, not invisible. */}
                                        <span className="text-xs text-text-subtle">
                                            {SUBJECT_WORDS[e.topic] ?? e.topic}
                                        </span>

                                        {/* Content, line 2: when, whether settled, and
                                            the fix. Demoted by size and position, not by
                                            grey alone. */}
                                        <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 text-xs text-text-muted">
                                            {/* WHEN, kept. A list headed "recent
                                                problems" with no times reads as a list of
                                                CURRENT problems, and the two are not the
                                                same thing: the first real row this panel
                                                ever showed was a six-day-old drift
                                                warning that had already been resolved,
                                                indistinguishable from one raised a minute
                                                ago. */}
                                            <time
                                                dateTime={e.occurredAt}
                                                className="tabular-nums"
                                            >
                                                {formatLedgerDateTime(e.occurredAt)}
                                            </time>

                                            {e.resolvedAt ? (
                                                /* resolvedAt is a real server-derived
                                                   instant (the first matching all-clear
                                                   after the problem) that the UI was
                                                   throwing away to print a bare word.
                                                   "Resolved" with no when is half a
                                                   fact — the same failure the occurredAt
                                                   note above exists to prevent.

                                                   The word stays alone in its own
                                                   element: the test anchors on
                                                   /^resolved$/i and that is worth keeping
                                                   exact, so the instant goes in a sibling
                                                   time element rather than into the
                                                   span. */
                                                <span className="inline-flex items-baseline gap-1 text-state-success">
                                                    <span>resolved</span>
                                                    <time
                                                        dateTime={e.resolvedAt}
                                                        className="tabular-nums text-text-muted"
                                                    >
                                                        {formatLedgerDateTime(e.resolvedAt)}
                                                    </time>
                                                </span>
                                            ) : null}

                                            {/* An unresolved drift finding carries its
                                                own fix. The scheduled monitor already
                                                found the problem; without this the reader
                                                is told their projections drifted and then
                                                has to re-run the check by hand on another
                                                tab to see what and repair it. `check`
                                                makes General run it on arrival.

                                                Only while unresolved: offering to
                                                re-check something the report has since
                                                called healthy invites a walk over every
                                                position for nothing. */}
                                            {e.eventKey === 'consistency.drift' &&
                                            !e.resolvedAt ? (
                                                <Link
                                                    to="/ledgers/$ledgerId/settings"
                                                    params={{ ledgerId }}
                                                    search={{ check: true } as never}
                                                    className="font-medium text-accent underline underline-offset-2 hover:text-accent-hover"
                                                >
                                                    Check and repair
                                                </Link>
                                            ) : null}
                                        </div>
                                    </li>
                                );
                            })}
                        </ul>
                    )}
                    {/* Ruled off the list. Without the rule it reads as a final row —
                        which is the one reading this caption must not have, since it is
                        the sentence saying the list above is not coverage. */}
                    <p className="mt-3 border-t border-border pt-2 text-xs text-text-muted">
                        A list you have to open cannot tell you the app stopped running.
                        That is what a heartbeat target is for.
                    </p>
                </PanelBody>
            </Panel>
        </div>
    );
}
