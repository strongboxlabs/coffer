import { useCallback, useMemo, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';

import { Button } from '@/components/ui/Button';
import { ContextMenu, type ContextMenuAnchor } from '@/components/ui/ContextMenu';
import { RowActionsButton } from '@/components/ui/RowActionsButton';
import { useFocusHandOff, useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';
import { SkeletonRows } from '@/components/ui/Skeleton';
import { unskipReminder } from '@/lib/api';
import { cn } from '@/lib/cn';
import { useMutation } from '@/lib/mutation';
import { formatLedgerDateCompact, shiftDateInputValue } from '@/lib/dates';
import { estimateTitle, isEstimating } from '@/lib/reminderEstimate';
import type { ReminderSummary, UpcomingOccurrence } from '@/lib/types';

import { ReminderEditorDialog } from '../../reminders/ReminderEditorDialog';
import { ReminderOccurrenceModal } from '../../reminders/ReminderOccurrenceModal';
import { HEADER_HORIZON_DAYS, overdueBySeries, willAutoPost, type OverdueSeries } from '../attention';
import { useHome, useWidgetFrame, widgetDomId } from '../homeContext';
import { Money } from '../Money';
import { NoRemindersYet } from '../NoRemindersYet';
import { useAgenda, useReminderList, useSchedules } from '../queries';
import { ROW_ACTION } from '../rows';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

// A row is one line from @xs/panel up. Below it, two: the date, payee and amount,
// then the account under the payee with the actions, so the payee keeps its width.
// The order classes move the pieces between the two layouts.
const ROW = 'group flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-1 text-sm';
const ROW_PAYEE = 'order-1 min-w-0 flex-1 truncate';
const ROW_AMOUNT = 'order-2 @xs/panel:order-4';
const ROW_BREAK = 'order-3 h-0 basis-full @xs/panel:hidden';
const ROW_INDENT = 'order-4 w-fixed-48px shrink-0 @xs/panel:hidden';
const ROW_ACCOUNT = 'order-4 min-w-0 flex-1 truncate text-xs text-text-muted @xs/panel:order-3 @xs/panel:max-w-fixed-160px @xs/panel:flex-none';
const ROW_ACTIONS = 'order-5';
const ROW_MENU = 'order-6';

type Row =
    | { kind: 'overdue'; series: OverdueSeries }
    | { kind: 'occurrence'; occ: UpcomingOccurrence; auto: boolean };

/**
 * Reminders due: overdue (one row per series), today, the next seven days, and later
 * to the horizon. Post or skip… opens the Reminders page's own occurrence dialog, so
 * nothing changes in one click. An occurrence the auto-post job will post reads
 * "auto-posts" on a muted row; any other is a person's to post.
 */
export function BillsWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const agenda = useAgenda(home.ledgerId, home.today);
    const reminders = useReminderList(home.ledgerId);
    const schedules = useSchedules(home.ledgerId);
    const [acting, setActing] = useState<UpcomingOccurrence | null>(null);
    const [editing, setEditing] = useState<string | null>(null);

    const horizon = Number(frame.settings.horizonDays ?? 14);
    const rowLimit = Number(frame.settings.rows ?? 8);
    const includeIncome = frame.settings.includeIncome !== false;
    const showPosted = frame.settings.showPosted === true;

    const link = (
        <Link to="/ledgers/$ledgerId/reminders" params={{ ledgerId: home.ledgerId }} className="text-xs font-medium text-accent hover:underline">
            Calendar →
        </Link>
    );
    const title = <>Bills &amp; income <span className="font-normal text-text-muted">· overdue + {horizon} days</span></>;

    if (agenda.isError || reminders.isError) {
        return (
            <WidgetPanel title={title} link={link}>
                <WidgetError
                    what="bills"
                    error={agenda.error ?? reminders.error}
                    onRetry={() => { void agenda.refetch(); void reminders.refetch(); }}
                />
            </WidgetPanel>
        );
    }
    if (agenda.isPending || reminders.isPending || schedules.isPending) {
        return <WidgetPanel title={title} link={link}><SkeletonRows rows={rowLimit} label="Loading bills" /></WidgetPanel>;
    }

    const autoPostEnabled = schedules.data?.['reminder-auto-post']?.enabled === true;
    const keep = (amount: number) => includeIncome || amount <= 0;
    const amountOf = (s: OverdueSeries) =>
        s.earliest?.amount ?? reminders.data.find((r) => r.id === s.reminderId)?.amount ?? 0;

    const overdue = overdueBySeries(agenda.data, reminders.data, home.today).filter((s) => keep(amountOf(s)));
    const last = shiftDateInputValue(home.today, horizon);
    const weekEnd = shiftDateInputValue(home.today, HEADER_HORIZON_DAYS);
    const ahead = agenda.data
        .filter((o) => o.date >= home.today && o.date <= last && keep(o.amount))
        .filter((o) => o.kind === 'reminder' || showPosted)
        .sort((a, b) => a.date.localeCompare(b.date) || (a.payee ?? '').localeCompare(b.payee ?? ''));

    const groups: { label: string; rows: Row[] }[] = [
        { label: 'Overdue', rows: overdue.map((series) => ({ kind: 'overdue' as const, series })) },
        { label: 'Today', rows: ahead.filter((o) => o.date === home.today).map(toRow) },
        { label: `Next ${HEADER_HORIZON_DAYS} days`, rows: ahead.filter((o) => o.date > home.today && o.date <= weekEnd).map(toRow) },
        {
            label: `Later · to ${formatLedgerDateCompact(last)}`,
            rows: ahead.filter((o) => o.date > weekEnd).map(toRow),
        },
    ];
    function toRow(occ: UpcomingOccurrence): Row {
        return { kind: 'occurrence', occ, auto: willAutoPost(occ, home.today, autoPostEnabled) };
    }

    // The row limit runs across the groups in order, so overdue is never the part cut.
    let budget = rowLimit;
    const shown = groups.map((g) => {
        const rows = g.rows.slice(0, Math.max(0, budget));
        budget -= rows.length;
        return { ...g, rows };
    }).filter((g) => g.rows.length > 0);
    const total = groups.reduce((n, g) => n + g.rows.length, 0);
    const hidden = total - shown.reduce((n, g) => n + g.rows.length, 0);

    return (
        <WidgetPanel title={title} link={link}>
            {total === 0 ? (
                <EmptyBills reminders={reminders.data} last={last} />
            ) : (
                <div>
                    <BillsGroups
                        shown={shown}
                        amountOf={amountOf}
                        reminders={reminders.data}
                        onAct={setActing}
                        onEdit={setEditing}
                    />
                    {hidden > 0 ? (
                        <p className="border-t border-border/60 px-4 py-2 text-xs text-text-muted">
                            +{hidden} more ·{' '}
                            <Link to="/ledgers/$ledgerId/reminders" params={{ ledgerId: home.ledgerId }} className="font-medium text-accent hover:underline">
                                Reminders →
                            </Link>
                        </p>
                    ) : null}
                </div>
            )}
            {!home.canEdit ? (
                <p className="border-t border-border/60 px-4 py-2 text-xs text-text-muted">View only: an owner or editor posts reminders.</p>
            ) : null}
            {acting ? (
                <ReminderOccurrenceModal ledgerId={home.ledgerId} occ={acting} onClose={() => setActing(null)} onActed={() => setActing(null)} />
            ) : null}
            {editing ? (
                <ReminderEditorDialog ledgerId={home.ledgerId} reminderId={editing} onClose={() => setEditing(null)} onSaved={() => setEditing(null)} />
            ) : null}
        </WidgetPanel>
    );
}

/**
 * A row's key: one per overdue series, one per occurrence. A posted occurrence
 * is its own row; a skip and its undo keep the row, whose Post or skip… and
 * Undo take each other's place (OccurrenceRow).
 */
function rowKey(row: Row): string {
    return row.kind === 'overdue'
        ? row.series.reminderId
        : `${row.occ.reminderId}_${row.occ.date}_${row.occ.kind === 'scheduled' ? 'posted' : 'due'}`;
}

/**
 * The groups of rows. A row posted or skipped leaves its group (an overdue series
 * caught up, an occurrence no longer due), and its Post or skip… and kebab with it:
 * focus goes to the same control on the next row, else the previous row, else the
 * widget's panel, as To review's does (ADR-0023 §L).
 */
function BillsGroups({ shown, amountOf, reminders, onAct, onEdit }: {
    shown: readonly { label: string; rows: Row[] }[];
    amountOf: (s: OverdueSeries) => number;
    reminders: readonly ReminderSummary[];
    onAct: (occ: UpcomingOccurrence) => void;
    onEdit: (reminderId: string) => void;
}) {
    const frame = useWidgetFrame();
    const drawn = shown.flatMap((g) => g.rows.map(rowKey));
    const panelId = widgetDomId(frame.widget.id);
    const panel = useMemo(() => ({ get current() { return document.getElementById(panelId); } }), [panelId]);
    const actControl = useRowRemovalFocus(drawn, panel);
    const menuControl = useRowRemovalFocus(drawn, panel);
    return (
        <>
            {shown.map((g) => (
                <section key={g.label} aria-label={g.label}>
                    <h3 className="px-4 pt-2 text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle">{g.label}</h3>
                    <ul>
                        {g.rows.map((row) => {
                            const key = rowKey(row);
                            return row.kind === 'overdue'
                                ? <OverdueRow key={key} series={row.series} amount={amountOf(row.series)} reminders={reminders}
                                    onAct={onAct} onEdit={onEdit} actRef={actControl(key)} menuRef={menuControl(key)} />
                                : <OccurrenceRow key={key} occ={row.occ} auto={row.auto} onAct={onAct} onEdit={onEdit}
                                    actRef={actControl(key)} menuRef={menuControl(key)} />;
                        })}
                    </ul>
                </section>
            ))}
        </>
    );
}

function EmptyBills({ reminders, last }: { reminders: readonly ReminderSummary[]; last: string }) {
    if (reminders.some((r) => r.isActive)) {
        return <p className="p-4 text-sm text-text-muted">Nothing due through {formatLedgerDateCompact(last)}.</p>;
    }
    return <NoRemindersYet />;
}

function OverdueRow({ series, amount, reminders, onAct, onEdit, actRef, menuRef }: {
    series: OverdueSeries;
    amount: number;
    reminders: readonly ReminderSummary[];
    onAct: (occ: UpcomingOccurrence) => void;
    onEdit: (reminderId: string) => void;
    /** Post or skip…'s and the kebab's refs, for when the row leaves (useRowRemovalFocus). */
    actRef: (el: HTMLElement | null) => void;
    menuRef: (el: HTMLElement | null) => void;
}) {
    const home = useHome();
    const navigate = useNavigate();
    const [menu, setMenu] = useState<ContextMenuAnchor | null>(null);
    const name = series.payee ?? 'Untitled reminder';
    const earliest = series.earliest;
    // The series' source account's currency, from its earliest occurrence or, for a
    // series overdue past the agenda's window, from the series itself. None for a
    // series with no single source: its amount is then a dash, never dollars.
    const summary = reminders.find((r) => r.id === series.reminderId);
    const currency = earliest?.currencyCode ?? summary?.currencyCode ?? null;
    // The amount is the earliest occurrence's, else the series' own (amountOf), so
    // whether it is an estimate is read from the same one.
    const estimateSource = earliest ?? summary;
    const estimate = currency !== null && isEstimating(estimateSource) ? estimateTitle(estimateSource.estimate) : null;
    const since = series.overdueSince;
    const items = [
        ...(home.canEdit && earliest ? [{ id: 'act', label: 'Post or skip…', onSelect: () => onAct(earliest) }] : []),
        ...(home.canEdit && series.latest && series.count > 1
            ? [{ id: 'latest', label: `Post the latest (skips ${series.count - 1} earlier)…`, onSelect: () => onAct(series.latest!) }]
            : []),
        ...(home.canEdit && reminders.some((r) => r.id === series.reminderId)
            ? [{ id: 'edit', label: 'Edit series…', onSelect: () => onEdit(series.reminderId) }]
            : []),
        { id: 'open', label: 'Open in Reminders', onSelect: () => void navigate({ to: '/ledgers/$ledgerId/reminders', params: { ledgerId: home.ledgerId } }) },
    ];
    return (
        <li
            className={cn(ROW, 'bg-state-warning-soft/40 shadow-[inset_3px_0_0_var(--color-state-warning)]')}
            onContextMenu={(e) => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY }); }}
        >
            <span className="w-fixed-48px shrink-0 font-mono text-xs tabular-nums text-text-subtle">
                {earliest ? formatLedgerDateCompact(earliest.date) : formatLedgerDateCompact(since!)}
            </span>
            <span className={ROW_PAYEE} title={name}>
                {name}
                <span className="text-text-muted">
                    {' · '}
                    {since ? <>overdue since {formatLedgerDateCompact(since)}</> : <span className="text-state-warning">{series.count} overdue</span>}
                </span>
            </span>
            <span aria-hidden className={ROW_BREAK} />
            <span aria-hidden className={ROW_INDENT} />
            <span className={ROW_ACCOUNT} title={earliest?.sourceAccountName ?? undefined}>
                {earliest?.sourceAccountName ?? '—'}
            </span>
            <span className={cn(ROW_AMOUNT, 'font-mono tabular-nums text-text-muted')}>
                {estimate !== null ? <span title={estimate}>≈ </span> : null}
                <Money amount={amount} currency={currency} tone="muted" className="font-normal" />
            </span>
            {home.canEdit && earliest ? (
                <Button ref={actRef} variant="link" size="xs" className={cn(ROW_ACTIONS, ROW_ACTION)} onClick={() => onAct(earliest)}>Post or skip…</Button>
            ) : null}
            <span className={ROW_MENU}><RowActionsButton ref={menuRef} label={`Actions for ${name}`} onOpen={setMenu} /></span>
            {menu ? <ContextMenu anchor={menu} items={items} onClose={() => setMenu(null)} /> : null}
        </li>
    );
}

function OccurrenceRow({ occ, auto, onAct, onEdit, actRef, menuRef }: {
    occ: UpcomingOccurrence;
    auto: boolean;
    onAct: (occ: UpcomingOccurrence) => void;
    onEdit: (reminderId: string) => void;
    /** Post or skip…'s and the kebab's refs, for when the row leaves (useRowRemovalFocus). */
    actRef: (el: HTMLElement | null) => void;
    menuRef: (el: HTMLElement | null) => void;
}) {
    const home = useHome();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const [menu, setMenu] = useState<ContextMenuAnchor | null>(null);
    const name = occ.payee ?? 'Untitled reminder';
    // The series' source account's currency; none for a series with no single source.
    const currency = occ.currencyCode ?? null;
    const estimate = currency !== null && isEstimating(occ) ? estimateTitle(occ.estimate) : null;
    const manual = occ.kind === 'reminder' && !auto;
    const items = [
        ...(home.canEdit && manual ? [{ id: 'act', label: 'Post or skip…', onSelect: () => onAct(occ) }] : []),
        ...(home.canEdit ? [{ id: 'edit', label: 'Edit series…', onSelect: () => onEdit(occ.reminderId) }] : []),
        { id: 'open', label: 'Open in Reminders', onSelect: () => void navigate({ to: '/ledgers/$ledgerId/reminders', params: { ledgerId: home.ledgerId } }) },
    ];

    // Undo is pending until the restore and the refetch it starts have landed:
    // a second press sends nothing (ADR-0023 §L), and a refusal is shown.
    const unskip = useMutation({
        mutationFn: () => unskipReminder(home.ledgerId, occ.reminderId, occ.date),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['reminders'] }),
    });

    // A skip that lands replaces Post or skip… with Undo, and an undo that
    // lands puts Post or skip… back (or "auto-posts", and then the row's
    // kebab is next): focus goes to the control that takes the place of the
    // one that held it (ADR-0023 §L).
    const actEl = useRef<HTMLElement | null>(null);
    const undoEl = useRef<HTMLElement | null>(null);
    const menuEl = useRef<HTMLElement | null>(null);
    const fromAct = useFocusHandOff(() => undoEl.current ?? menuEl.current);
    const fromUndo = useFocusHandOff(() => actEl.current ?? menuEl.current);
    const actRefs = useCallback((el: HTMLElement | null) => {
        actEl.current = el;
        actRef(el);
        fromAct(el);
    }, [actRef, fromAct]);
    const undoRefs = useCallback((el: HTMLElement | null) => {
        undoEl.current = el;
        fromUndo(el);
    }, [fromUndo]);
    const menuRefs = useCallback((el: HTMLElement | null) => {
        menuEl.current = el;
        menuRef(el);
    }, [menuRef]);

    return (
        <li
            className={cn(ROW, auto && 'text-text-subtle')}
            onContextMenu={(e) => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY }); }}
        >
            <span className="w-fixed-48px shrink-0 font-mono text-xs tabular-nums text-text-subtle">{formatLedgerDateCompact(occ.date)}</span>
            <span className={ROW_PAYEE} title={name}>{name}</span>
            <span aria-hidden className={ROW_BREAK} />
            <span aria-hidden className={ROW_INDENT} />
            <span className={ROW_ACCOUNT} title={occ.sourceAccountName ?? undefined}>
                {occ.sourceAccountName ?? '—'}
            </span>
            <span className={cn(ROW_AMOUNT, 'font-mono tabular-nums', auto ? 'text-text-subtle' : 'text-text-muted')}>
                {estimate !== null ? <span title={estimate}>≈ </span> : null}
                <Money amount={occ.amount} currency={currency} tone="muted" className={cn('font-normal', auto && 'text-text-subtle')} />
            </span>
            {occ.kind === 'scheduled' ? (
                <span className={cn(ROW_ACTIONS, 'text-xs text-text-muted')}>
                    Posted
                    {occ.headerId && occ.sourceAccountId ? (
                        <>
                            {' · '}
                            <Link
                                to="/ledgers/$ledgerId/accounts/$accountId"
                                params={{ ledgerId: home.ledgerId, accountId: occ.sourceAccountId }}
                                search={{ focus: occ.headerId }}
                                className="font-medium text-accent hover:underline"
                            >
                                View in register →
                            </Link>
                        </>
                    ) : null}
                </span>
            ) : occ.kind === 'skipped' ? (
                <span className={cn(ROW_ACTIONS, 'text-xs text-text-muted')}>
                    Skipped
                    {home.canEdit ? (
                        <>
                            {' · '}
                            <Button ref={undoRefs} variant="link" size="xs" pending={unskip.isPending} onClick={() => unskip.mutate()}>Undo</Button>
                            {unskip.isError ? (
                                <span role="alert" className="ml-2 text-state-danger">Could not restore that occurrence.</span>
                            ) : null}
                        </>
                    ) : null}
                </span>
            ) : auto ? (
                <span className={cn(ROW_ACTIONS, 'text-xs')}>auto-posts</span>
            ) : home.canEdit ? (
                <Button ref={actRefs} variant="link" size="xs" className={cn(ROW_ACTIONS, ROW_ACTION)} onClick={() => onAct(occ)}>Post or skip…</Button>
            ) : null}
            <span className={ROW_MENU}><RowActionsButton ref={menuRefs} label={`Actions for ${name}`} onOpen={setMenu} /></span>
            {menu ? <ContextMenu anchor={menu} items={items} onClose={() => setMenu(null)} /> : null}
        </li>
    );
}
