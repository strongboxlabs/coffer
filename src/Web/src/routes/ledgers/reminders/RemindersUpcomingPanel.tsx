import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';

import { ApiError, fetchUpcomingReminders } from '@/lib/api';
import type { UpcomingOccurrence } from '@/lib/types';
import { formatSignedAmount } from '@/lib/money';
import {
    addMonths, monthGridRange, monthLabel, monthMatrix, todayParts,
} from '@/lib/calendar';
import { Button } from '@/components/ui/Button';
import { Panel, PanelBody } from '@/components/ui/Panel';

import { ReminderOccurrenceModal } from './ReminderOccurrenceModal';

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MAX_CHIPS = 3;

/**
 * Upcoming view (ADR-0049) — a full-width month calendar whose chips are the
 * occurrences. Clicking an un-fired chip opens the occurrence dialog
 * (`ReminderOccurrenceModal`): one form to edit + Post or Skip. The dialog owns
 * the Post/Skip mutations + the catch-up cascade; it reports a post-action
 * notice back here. `scheduled` (posted) occurrences are read-only chips.
 */
export function RemindersUpcomingPanel({ ledgerId }: { ledgerId: string }) {
    const today = useMemo(() => todayParts(), []);
    const [view, setView] = useState<{ year: number; month: number }>(
        { year: today.year, month: today.month });
    const [active, setActive] = useState<UpcomingOccurrence | null>(null);
    const [notice, setNotice] = useState<string | null>(null);
    const [expanded, setExpanded] = useState<Set<string>>(() => new Set());

    const { from, to } = monthGridRange(view.year, view.month);
    const query = useQuery({
        queryKey: ['reminders', 'upcoming', ledgerId, from, to],
        queryFn: () => fetchUpcomingReminders(ledgerId, from, to),
    });

    const goMonth = (next: { year: number; month: number }) => {
        setNotice(null);
        setExpanded(new Set());
        setView(next);
    };

    const occurrences = useMemo<UpcomingOccurrence[]>(() => query.data ?? [], [query.data]);
    const byDate = useMemo(() => {
        const map = new Map<string, UpcomingOccurrence[]>();
        for (const o of occurrences) {
            const list = map.get(o.date);
            if (list) list.push(o); else map.set(o.date, [o]);
        }
        return map;
    }, [occurrences]);
    const grid = useMemo(() => monthMatrix(view.year, view.month), [view]);

    const openOccurrence = (o: UpcomingOccurrence) => { setNotice(null); setActive(o); };

    return (
        <div className="space-y-3">
            <div className="flex items-center justify-between">
                <h2 className="flex items-center gap-2 text-base font-semibold">
                    {monthLabel(view.year, view.month)}
                    {query.isFetching ? (
                        <span className="text-xs font-normal text-text-subtle">Loading…</span>
                    ) : null}
                </h2>
                <div className="flex items-center gap-1.5">
                    <Button type="button" variant="ghost" size="sm"
                        onClick={() => goMonth(addMonths(view.year, view.month, -1))}>‹ Prev</Button>
                    <Button type="button" variant="ghost" size="sm"
                        onClick={() => goMonth({ year: today.year, month: today.month })}>Today</Button>
                    <Button type="button" variant="ghost" size="sm"
                        onClick={() => goMonth(addMonths(view.year, view.month, 1))}>Next ›</Button>
                </div>
            </div>

            {notice !== null ? (
                <p role="status" className="text-xs text-text-muted">{notice}</p>
            ) : null}

            {query.isError ? (
                <Panel className="border-state-danger/40 bg-state-danger-soft">
                    <PanelBody>
                        <p role="alert" className="text-sm text-state-danger">
                            {query.error instanceof ApiError ? query.error.detail : 'Could not load the calendar.'}
                        </p>
                    </PanelBody>
                </Panel>
            ) : (
                <Panel>
                    <PanelBody className="p-2">
                        <div className="grid grid-cols-7 text-center text-[0.625rem] uppercase tracking-wider text-text-subtle">
                            {WEEKDAYS.map((d) => <div key={d} className="py-1">{d}</div>)}
                        </div>
                        <div className="grid grid-cols-7 gap-px bg-border">
                            {grid.flat().map((cell) => {
                                const items = byDate.get(cell.date) ?? [];
                                const isToday = cell.date === today.date;
                                const isExpanded = expanded.has(cell.date);
                                const shown = isExpanded ? items : items.slice(0, MAX_CHIPS);
                                const hidden = items.length - shown.length;
                                return (
                                    <div
                                        key={cell.date}
                                        className={`min-h-[6.5rem] bg-surface p-1 ${cell.inMonth ? '' : 'opacity-40'}`}
                                    >
                                        <div className={`text-right text-xs ${isToday
                                            ? 'font-bold text-accent' : 'text-text-subtle'}`}>
                                            {Number(cell.date.slice(8, 10))}
                                        </div>
                                        <div className="mt-0.5 space-y-0.5">
                                            {shown.map((o, i) => (
                                                <OccurrenceChip
                                                    key={`${o.reminderId}-${o.kind}-${i}`}
                                                    occ={o}
                                                    onOpen={openOccurrence}
                                                />
                                            ))}
                                            {hidden > 0 ? (
                                                <button
                                                    type="button"
                                                    onClick={() => setExpanded((s) => new Set(s).add(cell.date))}
                                                    className="w-full rounded px-1 text-left text-[0.625rem] text-text-subtle hover:text-text"
                                                >
                                                    +{hidden} more
                                                </button>
                                            ) : null}
                                        </div>
                                    </div>
                                );
                            })}
                        </div>
                        <p className="mt-2 px-1 text-[0.625rem] text-text-subtle">
                            <span className="text-accent">● Reminder</span> (click to edit + post) ·{' '}
                            <span className="text-text-muted">✓ Scheduled</span> (posted) ·{' '}
                            <span className="text-text-subtle line-through">⊘ Skipped</span>
                        </p>
                    </PanelBody>
                </Panel>
            )}

            {active !== null ? (
                <ReminderOccurrenceModal
                    ledgerId={ledgerId}
                    occ={active}
                    onClose={() => setActive(null)}
                    onActed={(n) => setNotice(n)}
                />
            ) : null}
        </div>
    );
}

/**
 * One occurrence in a calendar cell. An un-fired `reminder` is a button (opens
 * the occurrence dialog); a `scheduled` (posted) or `skipped` occurrence is a
 * read-only chip — `skipped` rendered struck-through so a catch-up cascade
 * leaves a visible trail rather than a gap.
 */
function OccurrenceChip({ occ, onOpen }: {
    occ: UpcomingOccurrence;
    onOpen: (o: UpcomingOccurrence) => void;
}) {
    const label = occ.payee ?? 'Reminder';
    const amount = formatSignedAmount(occ.amount);
    const title = `${label} · ${amount}`;

    // Read-only chips: posted (scheduled) and skipped.
    if (occ.kind === 'scheduled' || occ.kind === 'skipped') {
        const skipped = occ.kind === 'skipped';
        return (
            <div
                title={skipped ? `${title} · skipped` : title}
                className={`rounded bg-surface-muted px-1 text-[0.625rem] leading-tight ${
                    skipped ? 'text-text-subtle' : 'text-text-muted'}`}
            >
                <span className="flex items-baseline justify-between gap-1">
                    <span className={`truncate ${skipped ? 'line-through' : ''}`}>
                        {skipped ? '⊘' : '✓'} {label}
                    </span>
                    <span className="shrink-0 tabular-nums">{amount}</span>
                </span>
            </div>
        );
    }

    // Un-fired reminder: actionable (opens the occurrence dialog).
    return (
        <button
            type="button"
            title={title}
            onClick={() => onOpen(occ)}
            className="block w-full rounded bg-accent-soft px-1 text-left text-[0.625rem] leading-tight text-accent hover:bg-accent-soft/70"
        >
            <span className="flex items-baseline justify-between gap-1">
                <span className="truncate">● {label}</span>
                <span className="shrink-0 tabular-nums">{amount}</span>
            </span>
        </button>
    );
}
