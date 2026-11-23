import type { MutableRefObject, ReactNode } from 'react';
import { useNavigate } from '@tanstack/react-router';

import { AttentionDot } from '@/components/AttentionDot';
import { LedgerConversionNote } from '@/components/LedgerConversionNote';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { formatRelative } from '@/lib/ledgerOperationDisplay';
import { useLedgerAttention } from '@/lib/useLedgerAttention';
import { cn } from '@/lib/cn';
import { shiftDateInputValue } from '@/lib/dates';

import { checkedPhrase, dueWithin, HEADER_HORIZON_DAYS, overdueBySeries, reviewByAccount } from './attention';
import type { WidgetKey } from './dashboardCatalog';
import { useHome, widgetDomId } from './homeContext';
import { useAgenda, useConsistencyState, useFeedConnections, useRailAccounts, useReminderList, useSchedules } from './queries';

/** "Thu, Oct 29" for a `YYYY-MM-DD` calendar date. */
function longDay(iso: string): string {
    const [y, m, d] = iso.split('-').map(Number);
    return new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric', timeZone: 'UTC' })
        .format(new Date(Date.UTC(y!, m! - 1, d!)));
}

/**
 * The page header: "Overview", the reader's role when it limits them, Customise, and
 * the attention sentence — the day, then what is broken and what is waiting, each
 * phrase a link to the widget that lists it (or, when that widget is hidden, the page
 * that does). Its horizons are fixed, so editing a widget never changes it.
 */
export function HomeHeader({ visible, onCustomise, customiseRef, customiseDisabled, customisePending, showCustomise }: {
    /** The widgets currently on screen, so a phrase can move focus to its list. */
    visible: ReadonlySet<WidgetKey>;
    onCustomise: () => void;
    /** Customise, which takes focus when the edit it starts ends. */
    customiseRef: MutableRefObject<HTMLButtonElement | null>;
    /** The stored layout failed to load, so nothing may be written over it. */
    customiseDisabled: boolean;
    /** The stored layout is still being read: Customise waits on it. */
    customisePending: boolean;
    showCustomise: boolean;
}) {
    const home = useHome();
    return (
        <header className="space-y-1">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <h1 className="text-xl font-semibold tracking-tight">Overview</h1>
                <div className="flex items-center gap-2">
                    {home.canEdit ? null : (
                        <span className="rounded-full bg-chip-neutral px-2 py-0.5 text-xs font-medium text-chip-neutral-text">View only</span>
                    )}
                    {showCustomise ? (
                        <Button ref={customiseRef} variant="secondary" size="sm" onClick={onCustomise} disabled={customiseDisabled} pending={customisePending}>Customise</Button>
                    ) : null}
                </div>
            </div>
            <AttentionSentence visible={visible} />
            {/* The one ledger-level note on converted totals (ADR-0114 D7). */}
            <LedgerConversionNote ledgerId={home.ledgerId} />
        </header>
    );
}

interface Phrase {
    key: string;
    content: ReactNode;
    /** The widget that lists it, focused when shown. */
    widget: WidgetKey;
    /** Where to go when that widget is hidden. */
    go: () => void;
    tone?: string;
}

function AttentionSentence({ visible }: { visible: ReadonlySet<WidgetKey> }) {
    const home = useHome();
    const navigate = useNavigate();
    const attention = useLedgerAttention();
    const agenda = useAgenda(home.ledgerId, home.today);
    const reminders = useReminderList(home.ledgerId);
    const schedules = useSchedules(home.ledgerId);
    const accounts = useRailAccounts(home.ledgerId);
    const feeds = useFeedConnections(home.ledgerId);
    const consistency = useConsistencyState(home.ledgerId);

    const pending = attention.isPending || agenda.isPending || reminders.isPending || schedules.isPending || accounts.isPending;
    if (pending) {
        return (
            <div className="flex items-center gap-2 text-sm text-text-muted">
                <span>{longDay(home.today)}</span>
                <Skeleton className="h-control-20px w-fixed-320px max-w-full" />
            </div>
        );
    }

    const focusOr = (widget: WidgetKey, go: () => void) => () => {
        if (!visible.has(widget)) { go(); return; }
        const el = document.getElementById(widgetDomId(widget));
        el?.scrollIntoView({ block: 'start', behavior: 'smooth' });
        el?.focus({ preventScroll: true });
    };

    const phrases: Phrase[] = [];
    const failures: { key: string; what: string; retry: () => void }[] = [];

    // Problems: the rail's own count, so the two cannot disagree.
    const severity = attention.severityFor(home.ledgerId);
    const problems = attention.countFor(home.ledgerId) ?? 0;
    if (attention.isError) {
        failures.push({ key: 'problems', what: 'for problems', retry: attention.refetch });
    } else if (problems > 0) {
        phrases.push({
            key: 'problems',
            widget: 'problems',
            tone: severity === 'critical' ? 'text-state-danger' : 'text-state-warning',
            content: (
                <>
                    <AttentionDot severity={severity} count={problems} context="This ledger" className="mr-1" />
                    {problems} problem{problems === 1 ? '' : 's'}
                </>
            ),
            go: () => void navigate({ to: '/ledgers/$ledgerId/settings', params: { ledgerId: home.ledgerId }, search: { tab: 'notifications' } }),
        });
    }

    // To review: the rail's accounts read, real accounts only.
    if (accounts.isError) {
        failures.push({ key: 'review', what: 'the review queue', retry: () => void accounts.refetch() });
    } else {
        const review = reviewByAccount(accounts.data);
        if (review.total > 0) {
            const busiest = review.accounts[0]!;
            phrases.push({
                key: 'review',
                widget: 'review',
                content: <>{review.total} to review</>,
                go: () => void navigate({
                    to: '/ledgers/$ledgerId/accounts/$accountId',
                    params: { ledgerId: home.ledgerId, accountId: busiest.id },
                    search: { status: 'needs_review' },
                }),
            });
        }
    }

    // Bills: overdue per series, and what a person has to post within the week.
    if (agenda.isError || reminders.isError || schedules.isError) {
        failures.push({
            key: 'bills', what: 'bills',
            retry: () => { void agenda.refetch(); void reminders.refetch(); void schedules.refetch(); },
        });
    } else {
        const toReminders = () => void navigate({ to: '/ledgers/$ledgerId/reminders', params: { ledgerId: home.ledgerId } });
        const overdue = overdueBySeries(agenda.data, reminders.data, home.today).length;
        const autoPostEnabled = schedules.data['reminder-auto-post']?.enabled === true;
        const due = dueWithin(agenda.data, home.today, HEADER_HORIZON_DAYS, autoPostEnabled).length;
        if (overdue > 0) phrases.push({ key: 'overdue', widget: 'bills', content: <>{overdue} overdue</>, go: toReminders });
        if (due > 0) {
            phrases.push({
                key: 'due',
                widget: 'bills',
                content: <>{due} to post by {longDay(shiftDateInputValue(home.today, HEADER_HORIZON_DAYS))}</>,
                go: toReminders,
            });
        }
    }

    const phraseClass = (tone?: string) => cn(
        'rounded-sm underline-offset-2 hover:underline hover:decoration-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
        tone ?? 'text-text',
    );

    const quiet = phrases.length === 0 && failures.length === 0;
    const oldestSync = (feeds.data ?? [])
        .map((f) => f.lastSyncedAt)
        .filter((t): t is string => t !== null)
        .sort()[0];

    return (
        <>
            {/* Wide: one sentence. */}
            <p className="hidden flex-wrap items-center gap-x-2 gap-y-1 text-sm text-text-muted @2xl/page:flex">
                <span>{longDay(home.today)}</span>
                {phrases.map((p) => (
                    <span key={p.key} className="inline-flex items-center gap-2">
                        <span aria-hidden>·</span>
                        <button type="button" onClick={focusOr(p.widget, p.go)} className={phraseClass(p.tone)}>{p.content}</button>
                    </span>
                ))}
                {failures.map((f) => (
                    <span key={f.key} className="inline-flex items-center gap-2">
                        <span aria-hidden>·</span>
                        <span>Couldn&apos;t check {f.what}</span>
                        <Button variant="link" size="xs" onClick={f.retry}>Retry</Button>
                    </span>
                ))}
                {quiet ? (
                    <span>
                        · Nothing needs you today. Checked: {oldestSync ? `oldest feed sync ${formatRelative(oldestSync)} · ` : ''}
                        {checkedPhrase(consistency.data)}
                    </span>
                ) : null}
            </p>
            {/* Narrow: a list of rows big enough to tap. */}
            <div className="flex flex-col text-sm @2xl/page:hidden">
                <span className="text-text-muted">{longDay(home.today)}</span>
                {phrases.map((p) => (
                    <button
                        key={p.key}
                        type="button"
                        onClick={focusOr(p.widget, p.go)}
                        className={cn('flex h-control-44px items-center justify-between', phraseClass(p.tone))}
                    >
                        <span className="inline-flex items-center">{p.content}</span>
                        <span aria-hidden className="text-accent">→</span>
                    </button>
                ))}
                {failures.map((f) => (
                    <span key={f.key} className="flex h-control-44px items-center justify-between text-text-muted">
                        Couldn&apos;t check {f.what}
                        <Button variant="link" size="xs" onClick={f.retry}>Retry</Button>
                    </span>
                ))}
                {quiet ? <span className="text-text-muted">Nothing needs you today. {checkedPhrase(consistency.data)}.</span> : null}
            </div>
        </>
    );
}
