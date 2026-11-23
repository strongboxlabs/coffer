import { Link } from '@tanstack/react-router';

import { ChartFrame, VIEWBOX } from '@/components/chart/ChartFrame';
import { Line, type Point } from '@/components/chart/Series';
import { extent, vertical } from '@/components/chart/scale';
import { Skeleton } from '@/components/ui/Skeleton';
import { cn } from '@/lib/cn';
import { missingLegsByDay } from '@/lib/conversion';
import { formatLedgerDateCompact } from '@/lib/dates';
import { formatCurrency } from '@/lib/money';
import type { BalanceForecast, BalanceForecastLowest, BalanceForecastPoint } from '@/lib/types';

import { useBalanceForecast } from '../cashAhead';
import { useHome, useWidgetFrame } from '../homeContext';
import { ExchangeRatesLink, MissingRateNote } from '@/components/ConversionNote';

import { Money } from '../Money';
import { NoRemindersYet } from '../NoRemindersYet';
import { useReminderList } from '../queries';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

const { width: VB_W, height: VB_H } = VIEWBOX;

/** The name, then Today and Lowest: each figure column as wide as its widest figure,
 *  and never narrower than its longest unbroken piece. */
const COLUMNS = 'grid-cols-[minmax(0,1fr)_minmax(min-content,max-content)_minmax(min-content,max-content)]';
/** A row or row group: the table's full width, on the table's own columns. */
const SUBGRID = 'col-span-full grid grid-cols-subgrid';
/**
 * Below 22.5rem the name and the figures no longer share a line without the name
 * giving up most of its width, so the name takes a line of its own, the full width,
 * and Today and Lowest stay in their columns on the line beneath it.
 */
const ROW_NARROW = { name: '@max-[22.5rem]/panel:col-span-full' } as const;
/**
 * Below 18rem the two figures no longer fit side by side, so each takes a line of
 * its own under the name, with its column's name before it. The column headers stay
 * the cells' headers for assistive technology (visually hidden); the words drawn
 * beside the figures are generated content with no alternative text, so they are
 * not read a second time. Lowest's day goes under its figure when the two do not fit.
 */
const ROW_NARROWEST = {
    // The word at the start of the line, the figure at its end, and on a line of its
    // own (still at the end) when the two do not fit one.
    cell: '@max-2xs/panel:col-span-full @max-2xs/panel:flex @max-2xs/panel:flex-wrap @max-2xs/panel:justify-end @max-2xs/panel:gap-x-3 @max-2xs/panel:pl-0 @max-2xs/panel:before:mr-auto @max-2xs/panel:before:text-text-muted',
    today: "@max-2xs/panel:before:[content:'Today'_/_'']",
    lowest: "@max-2xs/panel:before:[content:'Lowest'_/_'']",
} as const;

/**
 * Where each account a reminder is paid from is heading: today's balance, and the
 * lowest the scheduled items take it to, with the day, for every account in the
 * projection. One step chart, of the accounts combined or of the one account the
 * settings choose; the choice picks the chart, never the rows. Advisory (ADR-0109):
 * it offers no button, and the header never counts it. A projection, not a
 * forecast: only what is scheduled moves it.
 */
export function CashAheadWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const days = Number(frame.settings.days ?? 30);
    const forecast = useBalanceForecast(home.ledgerId, home.today, days);

    const link = (
        <Link to="/ledgers/$ledgerId/reminders" params={{ ledgerId: home.ledgerId }} className="text-xs font-medium text-accent hover:underline">
            Reminders →
        </Link>
    );
    const title = <>Cash ahead <span className="font-normal text-text-muted">· {days} days</span></>;
    // The account setting offers the accounts the projection lists, in its order.
    const choices = { focusAccountId: (forecast.data?.accounts ?? []).map((a) => ({ value: a.accountId, label: a.name })) };

    if (forecast.isError) {
        return <WidgetPanel title={title} link={link}><WidgetError what="cash ahead" error={forecast.error} onRetry={() => void forecast.refetch()} /></WidgetPanel>;
    }
    if (!forecast.data) {
        return (
            <WidgetPanel title={title} link={link}>
                <div aria-busy="true" aria-label="Loading cash ahead" className="space-y-2 p-4">
                    <Skeleton className="h-fixed-128px" />
                    <Skeleton className="h-control-28px" />
                    <Skeleton className="h-control-28px" />
                </div>
            </WidgetPanel>
        );
    }
    return (
        <WidgetPanel title={title} link={link} choices={choices}>
            <Projection forecast={forecast.data} />
        </WidgetPanel>
    );
}

function Projection({ forecast }: { forecast: BalanceForecast }) {
    const home = useHome();
    const frame = useWidgetFrame();
    const reminders = useReminderList(home.ledgerId);

    // Nothing to project: no active reminder at all reads as Bills reads it.
    if (forecast.accounts.length === 0 && reminders.data !== undefined && !reminders.data.some((r) => r.isActive)) {
        return <NoRemindersYet />;
    }

    // What the chart draws: the account chosen, if the projection lists it, else the
    // accounts combined in the reporting currency (ADR-0114 D7). Combined is absent
    // only with no account, or when a rate it needs is missing (ADR-0114 D9).
    const stored = typeof frame.settings.focusAccountId === 'string' ? frame.settings.focusAccountId : null;
    const one = stored === null ? undefined : forecast.accounts.find((a) => a.accountId === stored);
    const subject = one
        ? { label: one.name, currency: one.currencyCode, points: one.points, lowest: one.lowest }
        : forecast.combined
            ? { label: 'Combined', currency: forecast.combined.currencyCode, points: forecast.combined.points, lowest: forecast.combined.lowest }
            : null;

    const caption = ['Projection from scheduled items'];
    if (forecast.includesEstimates) caption.push('includes estimates');
    if (forecast.overdueOnDay1 > 0) caption.push(`includes ${forecast.overdueOnDay1} overdue`);
    const approx = (lowest: BalanceForecastLowest) => (lowest.estimated ? '≈ ' : '');

    return (
        <div className="flex flex-col gap-1 px-4 py-3">
            {subject ? (
                <>
                    <div className="truncate text-xs text-text-muted" title={subject.label}>{subject.label}</div>
                    <StepChart points={subject.points} lowest={subject.lowest} currency={subject.currency}
                        label={`${subject.label} projected from scheduled items`} approx={approx(subject.lowest)} />
                </>
            ) : forecast.accounts.length > 0 ? (
                // A rate the combined line needs is missing, so there is none to draw.
                <MissingRateNote missing={forecast.rateUnavailable} ratesLink={<ExchangeRatesLink ledgerId={home.ledgerId} />} />
            ) : null}
            {forecast.accounts.length > 0 ? (
                // A grid of three columns, each row and row group a subgrid of it so the
                // figures line up in columns: the name takes what the figures leave, so a
                // long name truncates (with its title) and asks them for no width; each
                // figure column is as wide as its widest figure. Laid out by grid, a table
                // keeps its semantics only through explicit roles, so every part names
                // its own. As the widget narrows the row gives way in two steps, so it
                // stays inside the widget at any width (ROW_NARROW, ROW_NARROWEST).
                <table role="table" className={cn('grid w-full border-t border-border text-xs', COLUMNS)}>
                    <thead role="rowgroup" className={cn(SUBGRID, '@max-2xs/panel:sr-only')}>
                        <tr role="row" className={SUBGRID}>
                            <th role="columnheader" className="sr-only">Account</th>
                            <th role="columnheader" className="col-start-2 pt-1.5 pl-3 text-right font-normal text-text-muted">Today</th>
                            <th role="columnheader" className="col-start-3 pt-1.5 pl-3 text-right font-normal text-text-muted">Lowest</th>
                        </tr>
                    </thead>
                    <tbody role="rowgroup" className={SUBGRID}>
                        {forecast.accounts.map((a) => (
                            <tr role="row" key={a.accountId} className={SUBGRID}>
                                <th role="rowheader" scope="row" className={cn('col-start-1 min-w-0 py-0.5 text-left font-normal', ROW_NARROW.name)} data-account-name>
                                    <Link
                                        to="/ledgers/$ledgerId/accounts/$accountId"
                                        params={{ ledgerId: home.ledgerId, accountId: a.accountId }}
                                        className="block truncate hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                                        title={a.name}
                                    >
                                        {a.name}
                                    </Link>
                                </th>
                                <td role="cell" className={cn('col-start-2 py-0.5 pl-3 text-right whitespace-nowrap', ROW_NARROWEST.cell, ROW_NARROWEST.today)}>
                                    <Money amount={a.today} currency={a.currencyCode} tone="fixed" className="font-normal" />
                                </td>
                                <td role="cell" className={cn('col-start-3 py-0.5 pl-3 text-right', ROW_NARROWEST.cell, ROW_NARROWEST.lowest)} data-lowest={a.accountId}>
                                    {/* The figure and its day each hold together; the day goes
                                        under the figure only when the two do not fit side by side. */}
                                    <span>
                                        <span className="whitespace-nowrap">
                                            <span className="font-mono tabular-nums">{approx(a.lowest)}</span>
                                            <Money amount={a.lowest.amount} currency={a.currencyCode} tone="fixed" className="font-normal" />
                                        </span>
                                        {' '}<span className="whitespace-nowrap text-text-muted">{formatLedgerDateCompact(a.lowest.date)}</span>
                                    </span>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            ) : null}
            <p className="text-[0.6875rem] text-text-muted">
                {caption.join(' · ')}; not a forecast of anything unscheduled.
            </p>
            <Excluded forecast={forecast} />
        </div>
    );
}

/** The reminders that would move an account but could not be placed, each named,
 *  so the projection never reads as complete when it is not. For an owner or editor
 *  each opens its own editor; a viewer, whom the editor would refuse, reads the name. */
function Excluded({ forecast }: { forecast: BalanceForecast }) {
    const home = useHome();
    const n = forecast.excluded.length;
    const reminders = useReminderList(home.ledgerId);
    if (n === 0) return null;
    const payeeOf = new Map((reminders.data ?? []).map((r) => [r.id, r.payee]));
    return (
        <p className="text-[0.6875rem] text-text-muted" data-excluded>
            {n === 1 ? '1 reminder' : `${n} reminders`} not projected
            {reminders.data ? ': ' : null}
            {reminders.data ? forecast.excluded.map((x, i) => (
                <span key={x.reminderId}>
                    {i > 0 ? ' · ' : null}
                    {/* Named as the Reminders list names a series. */}
                    {home.canEdit ? (
                        <Link to="/ledgers/$ledgerId/reminders" params={{ ledgerId: home.ledgerId }} search={{ reminder: x.reminderId }}
                            className="font-medium text-accent hover:underline">
                            {payeeOf.get(x.reminderId) ?? 'Untitled reminder'}
                        </Link>
                    ) : payeeOf.get(x.reminderId) ?? 'Untitled reminder'}
                    {/* A series between currencies whose other side needs the latest
                        derived rate names each missing stored leg and its day, as the
                        rate line does (ADR-0114 D13). */}
                    {x.reason === 'rate-unavailable' && x.rateUnavailable && x.rateUnavailable.length > 0
                        ? <span data-excluded-rate> ({missingLegsByDay(x.rateUnavailable).join('; ')})</span>
                        : null}
                </span>
            )) : null}
        </p>
    );
}

/**
 * The balance at the end of each day as steps, with the zero rule and the lowest
 * point ringed. Every value is one of the server's points; nothing here adds two.
 */
function StepChart({ points, lowest, currency, label, approx }: {
    points: readonly BalanceForecastPoint[];
    lowest: BalanceForecastLowest;
    currency: string;
    label: string;
    approx: string;
}) {
    const [lo, hi] = extent(points.map((p) => p.balance));
    const y = vertical([lo, hi], VB_H);
    const x = (i: number) => (points.length <= 1 ? 0 : (i / (points.length - 1)) * VB_W);
    const pct = (v: number) => (y(v) / VB_H) * 100;

    // Each day holds the balance it ended the day before until its own end, then steps.
    const path: Point[] = [];
    points.forEach((p, i) => {
        if (i > 0) path.push({ x: x(i), y: y(points[i - 1]!.balance) });
        path.push({ x: x(i), y: y(p.balance) });
    });
    const lowAt = Math.max(0, points.findIndex((p) => p.date === lowest.date));
    const firstBelow = points.find((p) => p.balance < 0);
    const aria = `${label}: lowest ${approx}${formatCurrency(lowest.amount, currency)} on ${formatLedgerDateCompact(lowest.date)}`
        + (firstBelow ? `, below zero from ${formatLedgerDateCompact(firstBelow.date)}` : ', never below zero');

    return (
        <div>
            <ChartFrame
                heightClass="h-fixed-128px"
                ariaLabel={aria}
                overlay={
                    <>
                        <div aria-hidden data-zero-rule className="pointer-events-none absolute inset-x-0 border-t border-text-subtle" style={{ top: `${pct(0)}%` }} />
                        {/* HTML, not an SVG circle: the frame stretches its viewBox, which
                            would draw a circle as an ellipse of a different shape at every width. */}
                        <span
                            aria-hidden
                            data-lowest-point={lowest.date}
                            className="pointer-events-none absolute size-fixed-6px -translate-x-1/2 -translate-y-1/2 rounded-full border-[1.5px] border-text bg-surface"
                            style={{ left: `${(x(lowAt) / VB_W) * 100}%`, top: `${pct(lowest.amount)}%` }}
                        />
                    </>
                }
            >
                <Line points={path} className="stroke-accent" />
            </ChartFrame>
            <div aria-hidden className="mt-1 flex justify-between text-[0.625rem] text-text-subtle">
                <span>Today</span>
                <span>{points.length > 0 ? formatLedgerDateCompact(points.at(-1)!.date) : null}</span>
            </div>
        </div>
    );
}
