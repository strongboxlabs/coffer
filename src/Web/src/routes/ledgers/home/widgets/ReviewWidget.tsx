import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate } from '@tanstack/react-router';

import { ProvenanceIcon } from '@/components/register/ProvenanceIcon';
import { TagColorsProvider } from '@/components/tags/TagColorsContext';
import { Button } from '@/components/ui/Button';
import { ContextMenu, type ContextMenuAnchor } from '@/components/ui/ContextMenu';
import { RowActionsButton } from '@/components/ui/RowActionsButton';
import { SkeletonRows } from '@/components/ui/Skeleton';
import { buildAccountPathMap } from '@/lib/accountPath';
import { systemCategoryIds } from '@/lib/categoryChip';
import { cn } from '@/lib/cn';
import { formatLedgerDateCompact } from '@/lib/dates';
import type { LedgerEntry } from '@/lib/types';

import { reviewByAccount, reviewRemainders } from '../attention';
import { EntryAmount, EntryCategory } from '../EntryCells';
import { useHome, useWidgetFrame, widgetDomId } from '../homeContext';
import { useRailAccounts, useReviewEntries } from '../queries';
import { ReviewDialog } from '../ReviewDialog';
import { reviewNote } from '../roundedNote';
import {
    ENTRY_ACCOUNT,
    ENTRY_AMOUNT,
    ENTRY_BREAK,
    ENTRY_CATEGORY_BESIDE_ACTIONS,
    ENTRY_INDENT,
    ENTRY_LEAD,
    ENTRY_PAYEE,
    entryKey,
    ROW_ACTION,
    shownAmount,
    shownRow,
} from '../rows';
import { WidgetError, WidgetPanel } from '../WidgetPanel';

// The entry row's columns (../rows), led by the provenance mark, then the row's
// actions: Review… (or, for a viewer, Open →) and the kebab. On two lines they sit at
// the end of the second.
const ROW = 'group flex min-h-control-36px flex-wrap items-center gap-x-3 gap-y-1 px-4 py-1 text-sm';
const ROW_ACTIONS = 'order-6 ml-auto @xs/panel:ml-0';
const ROW_MENU = 'order-7';
/**
 * The rounded-at-import note's line (ADR-0114 D15, board RV-1a): a line of its own after
 * the actions, ending under the amount. An invisible copy of the actions, as wide as
 * they are, holds it there; only this row grows, by the one line.
 */
const ROUNDED_LINE = 'order-8 flex basis-full items-center justify-end gap-x-3';
const ROUNDED_NOTE = 'text-right text-[0.6875rem] text-text-muted';
const ROUNDED_SPACER = 'invisible flex h-0 items-center gap-x-3 overflow-hidden';
/** Open → for a viewer, drawn as the xs link button Review… is. */
const OPEN_LINK = 'inline-flex h-control-28px items-center rounded-md px-1 text-xs font-medium text-accent hover:underline underline-offset-2 '
    + 'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

/** What opened the dialog in its row: its Review…, or Review… in its menu. */
type Opener = 'review' | 'menu';

/** Where focus goes once the dialog closes: the row it was opened from, by key and
 *  by its place in the list then, and the control it went back to. */
interface ReturnTarget {
    key: string;
    index: number;
    via: Opener;
    focused: HTMLElement | null;
}

/** The rows the widget draws, in reading order (grouped or not). */
function rowsOf(panelId: string): HTMLElement[] {
    const panel = document.getElementById(panelId);
    return panel ? Array.from(panel.querySelectorAll<HTMLElement>('li[data-review-row]')) : [];
}

/** The control of a row that opened its dialog: Review…, or the kebab whose menu did
 *  (the menu is gone once its item is chosen). */
function openerIn(row: HTMLElement, via: Opener): HTMLElement | null {
    return via === 'menu'
        ? row.querySelector<HTMLElement>('button[aria-haspopup="menu"]')
        : row.querySelector<HTMLElement>('[data-review-open]');
}

/**
 * Imported rows waiting for review. The head and the per-account counts come from the
 * rail's own accounts query, so they cost no request; each count links into that
 * account's register on its Needs review view. Below them, the newest rows awaiting
 * review across the ledger, each drawn as its register draws it, with Review… for an
 * owner or editor (the register's own editor in a dialog, ADR-0109) and Open → into
 * the register for a viewer. What the list leaves out is a link per account in the
 * footer.
 */
export function ReviewWidget() {
    const home = useHome();
    const frame = useWidgetFrame();
    const rows = Number(frame.settings.rows ?? 5);
    const grouped = frame.settings.groupByAccount === true;
    const accounts = useRailAccounts(home.ledgerId);
    const review = useMemo(() => (accounts.data ? reviewByAccount(accounts.data) : null), [accounts.data]);
    const entries = useReviewEntries(home.ledgerId, rows, (review?.total ?? 0) > 0);
    const names = useMemo(() => new Map((accounts.data ?? []).map((a) => [a.id, a.name])), [accounts.data]);
    const paths = useMemo(() => buildAccountPathMap(accounts.data ?? []), [accounts.data]);
    // The ledger's Uncategorized, whose chip is always neutral, as in the register.
    const systemIds = useMemo(() => systemCategoryIds(accounts.data), [accounts.data]);
    // One dialog at a time, here rather than in a row: a row leaves the list when its
    // review saves, and the dialog must outlive it to close itself.
    const [reviewing, setReviewing] = useState<LedgerEntry | null>(null);
    const closeReview = useCallback(() => setReviewing(null), []);
    const panelId = widgetDomId(frame.widget.id);
    const returnTo = useRef<ReturnTarget | null>(null);
    const openReview = useCallback((item: LedgerEntry, via: Opener) => {
        const key = entryKey(item);
        returnTo.current = { key, via, index: rowsOf(panelId).findIndex((r) => r.dataset.reviewRow === key), focused: null };
        setReviewing(item);
    }, [panelId]);

    // Focus goes back to what opened the dialog once it closes. Opened from the menu,
    // the dialog found nothing focused to return to (the menu had gone), so it is the
    // row's kebab. A review that saved takes its row out of the list a moment after
    // the dialog closes, with focus on it: focus then moves to the same control of the
    // row now in its place, else of the last row, else to the panel, so the next
    // review is one key away. Focus the reader has moved elsewhere is left there.
    useEffect(() => {
        const target = returnTo.current;
        if (reviewing !== null || target === null) return;
        const drawn = rowsOf(panelId);
        if (target.focused === null) {
            const row = drawn.find((r) => r.dataset.reviewRow === target.key);
            const opener = row ? openerIn(row, target.via) : null;
            if (opener) {
                opener.focus();
                target.focused = opener;
                return;
            }
        } else {
            const active = document.activeElement;
            const lost = !target.focused.isConnected && (active === null || active === document.body);
            if (!lost) {
                if (active !== target.focused) returnTo.current = null;
                return;
            }
        }
        const next = drawn[Math.max(0, Math.min(target.index, drawn.length - 1))];
        ((next ? openerIn(next, target.via) : null) ?? document.getElementById(panelId))?.focus();
        returnTo.current = null;
    });

    const link = (
        <Link to="/ledgers/$ledgerId/accounts" params={{ ledgerId: home.ledgerId }} className="text-xs font-medium text-accent hover:underline">
            Registers →
        </Link>
    );

    // Rendered whatever the body shows: a refresh behind an open dialog may change it.
    const dialog = reviewing ? <ReviewDialog ledgerId={home.ledgerId} item={reviewing} onClose={closeReview} /> : null;

    if (accounts.isError) {
        return (
            <WidgetPanel title="To review" link={link}>
                <WidgetError what="the review queue" error={accounts.error} onRetry={() => void accounts.refetch()} />
                {dialog}
            </WidgetPanel>
        );
    }
    if (review === null) {
        return <WidgetPanel title="To review" link={link}><SkeletonRows rows={rows} label="Loading the review queue" /></WidgetPanel>;
    }

    const n = review.accounts.length;
    const title = review.total === 0
        ? 'To review'
        : <>To review <span className="font-normal text-text-muted">· {review.total} in {n} account{n === 1 ? '' : 's'}</span></>;

    if (review.total === 0) {
        return (
            <WidgetPanel title={title} link={link}>
                <p className="p-4 text-sm text-text-muted">Nothing to review · imports land here when a feed brings new rows.</p>
                {dialog}
            </WidgetPanel>
        );
    }

    const row = (item: LedgerEntry) => (
        <ReviewRow
            key={entryKey(item)}
            item={item}
            accountName={names.get(item.accountId)}
            paths={paths}
            systemIds={systemIds}
            onReview={openReview}
        />
    );

    return (
        <WidgetPanel title={title} link={link}>
            <ul className="flex flex-wrap gap-x-4 gap-y-1 p-4 text-sm" aria-label="Rows to review by account">
                {review.accounts.map((a) => (
                    <li key={a.id}>
                        <Link
                            to="/ledgers/$ledgerId/accounts/$accountId"
                            params={{ ledgerId: home.ledgerId, accountId: a.id }}
                            search={{ status: 'needs_review' }}
                            className="hover:underline"
                        >
                            {a.name} <span className="font-mono font-semibold tabular-nums">{a.count}</span>{' '}
                            <span className="text-accent">→</span>
                        </Link>
                    </li>
                ))}
            </ul>
            {entries.isError ? (
                <WidgetError what="the rows to review" error={entries.error} onRetry={() => void entries.refetch()} />
            ) : !entries.data ? (
                <SkeletonRows rows={rows} label="Loading the rows to review" />
            ) : (
                <TagColorsProvider ledgerId={home.ledgerId}>
                    {grouped ? (
                        <div>
                            {byAccount(entries.data.entries).map((g) => {
                                const name = names.get(g.accountId) ?? '—';
                                return (
                                    <section key={g.accountId} aria-label={name}>
                                        <h3 className="px-4 pt-2 text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle">{name}</h3>
                                        <ul>{g.entries.map(row)}</ul>
                                    </section>
                                );
                            })}
                        </div>
                    ) : (
                        <ul aria-label="Rows to review">{entries.data.entries.map(row)}</ul>
                    )}
                    <Remainders accounts={reviewRemainders(review.accounts, entries.data.entries).accounts} />
                </TagColorsProvider>
            )}
            {dialog}
        </WidgetPanel>
    );
}

/** The list cut into one group per account, newest row first in each, the groups in
 *  the order of their newest rows (the list's own order is newest first). */
function byAccount(entries: readonly LedgerEntry[]): { accountId: string; entries: LedgerEntry[] }[] {
    const groups = new Map<string, LedgerEntry[]>();
    for (const e of entries) {
        const group = groups.get(e.accountId);
        if (group) group.push(e);
        else groups.set(e.accountId, [e]);
    }
    return [...groups].map(([accountId, list]) => ({ accountId, entries: list }));
}

/** "+N more: <account> N → · …": what the list leaves out, per account, each a link
 *  to that register's Needs review view. Nothing when the list shows every row. */
function Remainders({ accounts }: { accounts: readonly { id: string; name: string; count: number }[] }) {
    const home = useHome();
    if (accounts.length === 0) return null;
    const total = accounts.reduce((sum, a) => sum + a.count, 0);
    return (
        <p className="border-t border-border/60 px-4 py-2 text-xs text-text-muted">
            +{total} more:{' '}
            {accounts.map((a, i) => (
                <span key={a.id}>
                    {i > 0 ? ' · ' : null}
                    <Link
                        to="/ledgers/$ledgerId/accounts/$accountId"
                        params={{ ledgerId: home.ledgerId, accountId: a.id }}
                        search={{ status: 'needs_review' }}
                        className="font-medium text-accent hover:underline"
                    >
                        {a.name} <span className="font-mono font-semibold tabular-nums">{a.count}</span> →
                    </Link>
                </span>
            ))}
        </p>
    );
}

function ReviewRow({ item, accountName, paths, systemIds, onReview }: {
    item: LedgerEntry;
    accountName: string | undefined;
    paths: ReadonlyMap<string, string>;
    /** The ledger's system category ids (its Uncategorized): a neutral chip. */
    systemIds: ReadonlySet<string>;
    onReview: (item: LedgerEntry, via: Opener) => void;
}) {
    const home = useHome();
    const navigate = useNavigate();
    const [menu, setMenu] = useState<ContextMenuAnchor | null>(null);
    const row = shownRow(item.entry);
    const name = row.payee ?? '(no payee)';
    // A split's lines share one header, so its first line carries the header's originals
    // and the reason it was held.
    const rounded = reviewNote(row, shownAmount(item.entry));
    const register = { ledgerId: home.ledgerId, accountId: item.accountId };
    const openRegister = () => void navigate({
        to: '/ledgers/$ledgerId/accounts/$accountId', params: register, search: { focus: row.headerId },
    });
    const items = [
        ...(home.canEdit ? [{ id: 'review', label: 'Review…', onSelect: () => onReview(item, 'menu') }] : []),
        { id: 'open', label: 'Open in register', onSelect: openRegister },
    ];

    return (
        <li
            className={ROW}
            data-review-row={entryKey(item)}
            onContextMenu={(e) => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY }); }}
        >
            <span className={ENTRY_LEAD}>
                <ProvenanceIcon origin={row.origin} providerKey={row.providerKey} isMergeWinner={row.isMergeWinner} />
                <span className="font-mono text-xs tabular-nums text-text-subtle">{formatLedgerDateCompact(row.postedAt)}</span>
            </span>
            <span className={ENTRY_PAYEE} title={row.payee ?? undefined}>
                {row.payee ?? <span className="text-text-subtle">—</span>}
            </span>
            <span aria-hidden className={ENTRY_BREAK} />
            <span aria-hidden className={ENTRY_INDENT} />
            <span className={ENTRY_ACCOUNT} title={accountName}>{accountName ?? '—'}</span>
            <span className={ENTRY_CATEGORY_BESIDE_ACTIONS}><EntryCategory item={item} paths={paths} systemIds={systemIds} /></span>
            <span className={ENTRY_AMOUNT}><EntryAmount item={item} /></span>
            {/* After the amount in reading order, so it is read with the row and seen
                without hovering; drawn on its own line under it. */}
            {rounded !== null ? (
                <span className={ROUNDED_LINE}>
                    <span className={ROUNDED_NOTE}>{rounded}</span>
                    <span aria-hidden className={ROUNDED_SPACER}>
                        <span className="inline-flex h-control-28px items-center px-1 text-xs font-medium">
                            {home.canEdit ? 'Review…' : 'Open →'}
                        </span>
                        <span className="inline-flex size-control-28px" />
                    </span>
                </span>
            ) : null}
            {home.canEdit ? (
                <Button
                    variant="link"
                    size="xs"
                    className={cn(ROW_ACTIONS, ROW_ACTION)}
                    data-review-open=""
                    onClick={() => onReview(item, 'review')}
                >
                    Review…
                </Button>
            ) : (
                <Link
                    to="/ledgers/$ledgerId/accounts/$accountId"
                    params={register}
                    search={{ focus: row.headerId }}
                    className={cn(ROW_ACTIONS, ROW_ACTION, OPEN_LINK)}
                >
                    Open →
                </Link>
            )}
            <span className={ROW_MENU}><RowActionsButton label={`Actions for ${name}`} onOpen={setMenu} /></span>
            {menu ? <ContextMenu anchor={menu} items={items} onClose={() => setMenu(null)} /> : null}
        </li>
    );
}
