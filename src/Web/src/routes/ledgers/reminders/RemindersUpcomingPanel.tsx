import { useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import { unskipReminder, ApiError, fetchUpcomingReminders } from '@/lib/api';
import type { UpcomingOccurrence } from '@/lib/types';
import { formatSignedAmount } from '@/lib/money';
import { estimateTitle, isEstimating } from '@/lib/reminderEstimate';
import {
    addMonths, monthGridRange, monthLabel, monthMatrix, todayParts,
} from '@/lib/calendar';
import { Button } from '@/components/ui/Button';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { usePendingButtonProps } from '@/components/ui/pending';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';

import { ReminderOccurrenceModal } from './ReminderOccurrenceModal';

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MAX_CHIPS = 3;

/**
 * A chip's key: its occurrence, never its state or its place in the cell. A
 * due chip skipped or restored is the same chip; a posted one is its own.
 */
const chipKey = (o: UpcomingOccurrence) =>
    `${o.reminderId}:${o.date}:${o.kind === 'scheduled' ? 'posted' : 'due'}`;

/**
 * Upcoming view (ADR-0049) — a full-width month calendar whose chips are the
 * occurrences. Clicking an un-fired chip opens the occurrence dialog
 * (`ReminderOccurrenceModal`): one form to edit + Post or Skip. The dialog owns
 * the Post/Skip mutations + the catch-up cascade; it reports a post-action
 * notice back here. `scheduled` (posted) occurrences are read-only chips, and for
 * a viewer every chip is: a viewer neither posts nor skips nor restores.
 */
export function RemindersUpcomingPanel({ ledgerId, canEdit }: {
    ledgerId: string;
    /** Owner or editor: a due chip opens Post or skip, a skipped one restores. */
    canEdit: boolean;
}) {
    const today = useMemo(() => todayParts(), []);
    const [view, setView] = useState<{ year: number; month: number }>(
        { year: today.year, month: today.month });
    const [active, setActive] = useState<UpcomingOccurrence | null>(null);
    const queryClient = useQueryClient();
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

    // A chip's own action can change it (ADR-0023 §L). Skip and restore turn a
    // due chip into a skipped one and back: the chip is keyed by its occurrence,
    // not its state, so it stays the same button and keeps focus. A post turns
    // it into a read-only chip, and the button that held focus hands it to the
    // next chip button drawn (across the month, the chips folded behind
    // "+N more" left out), else the previous one, else the calendar itself,
    // which has no add control.
    const shownIn = (date: string) => {
        const items = byDate.get(date) ?? [];
        return expanded.has(date) ? items : items.slice(0, MAX_CHIPS);
    };
    const drawnChips = grid.flat().flatMap((cell) =>
        shownIn(cell.date).filter((o) => o.kind !== 'scheduled').map(chipKey));
    const calendarRef = useRef<HTMLDivElement | null>(null);
    const chipControl = useRowRemovalFocus(drawnChips, calendarRef);

    const openOccurrence = (o: UpcomingOccurrence) => { setNotice(null); setActive(o); };

    // Restoring a suppressed occurrence. Only the slot clicked is restored — skipping
    // sweeps earlier un-acted occurrences, and un-skip deliberately does not undo that
    // sweep, so a user clears a cascade one chip at a time and keeps the ones they meant.
    const unskip = useMutation({
        mutationFn: (o: UpcomingOccurrence) => unskipReminder(ledgerId, o.reminderId, o.date),
        onSuccess: async (_r, o) => {
            setNotice(`Restored the ${o.date} occurrence — it can be posted again.`);
            // The series cursor may have moved EARLIER, so the whole agenda is stale,
            // not just this cell.
            await queryClient.invalidateQueries({ queryKey: ['reminders'] });
        },
        onError: () => setNotice('Could not restore that occurrence.'),
    });

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
                <Panel
                    ref={calendarRef}
                    role="group"
                    aria-label={`Reminders in ${monthLabel(view.year, view.month)}`}
                    tabIndex={-1}
                    className="focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                    <PanelBody className="p-2">
                        <div className="grid grid-cols-7 text-center text-[0.625rem] uppercase tracking-wider text-text-subtle">
                            {WEEKDAYS.map((d) => <div key={d} className="py-1">{d}</div>)}
                        </div>
                        <div className="grid grid-cols-7 gap-px bg-border">
                            {grid.flat().map((cell) => {
                                const items = byDate.get(cell.date) ?? [];
                                const isToday = cell.date === today.date;
                                const shown = shownIn(cell.date);
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
                                            {shown.map((o) => (
                                                <OccurrenceChip
                                                    key={chipKey(o)}
                                                    chipRef={chipControl(chipKey(o))}
                                                    occ={o}
                                                    canEdit={canEdit}
                                                    onOpen={openOccurrence}
                                                    onUnskip={(x) => unskip.mutate(x)}
                                                    unskipping={unskip.isPending}
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
                            <span className="text-accent">● Reminder</span>{canEdit ? ' (click to edit + post)' : null} ·{' '}
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
function OccurrenceChip({ occ, canEdit, chipRef, onOpen, onUnskip, unskipping }: {
    occ: UpcomingOccurrence;
    /** A viewer's chip reads, whatever its state: it opens and restores nothing. */
    canEdit: boolean;
    /** The ref for the chip's button (due or skipped), for when a post makes
     *  it read-only (useRowRemovalFocus); the read-only chip takes none. */
    chipRef: (el: HTMLElement | null) => void;
    onOpen: (o: UpcomingOccurrence) => void;
    onUnskip: (o: UpcomingOccurrence) => void;
    unskipping: boolean;
}) {
    const pendingProps = usePendingButtonProps();
    const label = occ.payee ?? 'Reminder';
    // In the series' source account's currency. A series with no single source has
    // none, so its chip carries no figure rather than one in a currency it is not in.
    const amount = occ.currencyCode ? formatSignedAmount(occ.amount, occ.currencyCode) : null;

    // Mig 220. An estimating series' amount is prefixed "≈" and the tooltip says what
    // it rests on, because the number alone cannot tell a reader it is not the bill:
    // the estimate, or the amount entered standing in until there is one.
    const estimate = amount !== null && isEstimating(occ) ? estimateTitle(occ.estimate) : null;
    const shownAmount = amount === null ? '' : estimate !== null ? `≈${amount}` : amount;
    const title = amount === null
        ? label
        : estimate !== null
            ? `${label} · ${shownAmount} · ${estimate}`
            : `${label} · ${amount}`;

    // A SKIPPED chip is actionable: three fire errors instruct people to un-skip.
    // A posted (`scheduled`) chip stays
    // read-only — undoing that is deleting a real transaction, which belongs in the
    // register, not behind a calendar chip.
    if (occ.kind === 'skipped' && !canEdit) {
        return (
            <div
                title={`${title} · skipped`}
                className="rounded bg-surface-muted px-1 text-[0.625rem] leading-tight text-text-subtle"
            >
                <span className="flex items-baseline justify-between gap-1">
                    <span className="truncate line-through">⊘ {label}</span>
                    <span className="shrink-0 tabular-nums">{shownAmount}</span>
                </span>
            </div>
        );
    }
    if (occ.kind === 'skipped') {
        return (
            <button
                ref={chipRef}
                type="button"
                title={`${title} · skipped — restore it`}
                {...pendingProps(unskipping, () => onUnskip(occ))}
                className="block w-full rounded bg-surface-muted px-1 text-left text-[0.625rem] leading-tight text-text-subtle hover:bg-surface-muted/60 hover:text-text-muted disabled:opacity-50 aria-disabled:opacity-50"
            >
                <span className="flex items-baseline justify-between gap-1">
                    <span className="truncate line-through">⊘ {label}</span>
                    <span className="shrink-0 tabular-nums">{shownAmount}</span>
                </span>
            </button>
        );
    }

    // Posted: read-only.
    if (occ.kind === 'scheduled') {
        return (
            <div
                title={title}
                className="rounded bg-surface-muted px-1 text-[0.625rem] leading-tight text-text-muted"
            >
                <span className="flex items-baseline justify-between gap-1">
                    <span className="truncate">✓ {label}</span>
                    <span className="shrink-0 tabular-nums">{shownAmount}</span>
                </span>
            </div>
        );
    }

    // Un-fired reminder, for a viewer: read, in the due chip's colours.
    if (!canEdit) {
        return (
            <div title={title} className="rounded bg-accent-soft px-1 text-[0.625rem] leading-tight text-accent">
                <span className="flex items-baseline justify-between gap-1">
                    <span className="truncate">● {label}</span>
                    <span className="shrink-0 tabular-nums">{shownAmount}</span>
                </span>
            </div>
        );
    }

    // Un-fired reminder: actionable (opens the occurrence dialog).
    return (
        <button
            ref={chipRef}
            type="button"
            title={title}
            onClick={() => onOpen(occ)}
            className="block w-full rounded bg-accent-soft px-1 text-left text-[0.625rem] leading-tight text-accent hover:bg-accent-soft/70"
        >
            <span className="flex items-baseline justify-between gap-1">
                <span className="truncate">● {label}</span>
                <span className="shrink-0 tabular-nums">{shownAmount}</span>
            </span>
        </button>
    );
}
