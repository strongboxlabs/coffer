import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { fetchBudgetTransactions } from '@/lib/api';
import { formatCurrency } from '@/lib/money';
import { formatLedgerDate } from '@/lib/dates';
import { categoryChipVariant } from '@/lib/categoryChip';
import { cn } from '@/lib/cn';
import type { BudgetCategoryRow } from '@/lib/types';

import { rootRows, targetConflict } from './cumulative';

/**
 * The category table: a TREE, one row per category at any depth, each row
 * expandable into its children and openable to its own transactions.
 *
 * WHY A TREE AND NOT ROOTS-PLUS-A-LIST. The first cut rendered roots as real
 * rows and a category's children as a flat list inside the expanded panel.
 * Three problems fell out of that one decision, and all three go away here:
 * the child list could not align with the table (a `<ul>` of flex spans and a
 * `<table>`'s columns are different layout systems, and no amount of padding
 * reconciles them); children could not carry an editable target, though
 * ADR-0099 D1a was designed to allow exactly that; and children had no
 * transactions of their own, so "open it up and look" worked at one level only.
 *
 * ROOTS STILL GOVERN THE ARITHMETIC. The table is a tree; the CEILING is still
 * the sum over roots, because the API rolls up and a parent's number already
 * contains its children. Those are different questions — presentation versus
 * double-counting — and conflating them is what drew a ceiling 1.87x too high
 * once already. `rootRows` is used here only to decide what sits at depth 0.
 *
 * ONE SCALE FOR EVERY BAR, at every depth. Bar length means money, full stop,
 * so a child's bar is short because a child is smaller. Rescaling per level
 * would make a 20-pound category look like a 2,000-pound one two rows apart.
 *
 * TWO GESTURES, NOT ONE. The triangle reveals CHILDREN — structural, cheap, and
 * many may be open at once. Clicking the NAME opens that row's transactions,
 * and only one such panel is open at a time. They were a single control, and
 * the conflation is what made the screen unreadable: a row costs ~29px and a
 * panel cost ~215px, so every expansion was seven rows tall and two of them
 * buried the table.
 *
 * THE PANEL SITS DIRECTLY UNDER ITS OWN ROW, above that row's children. An
 * earlier cut put children first, which reads well in the abstract and orphans
 * the panel in practice: a parent's detail ended up below all its children and
 * any nested panel of theirs, with nothing but a thin rule to say whose it was.
 * Touching the row it belongs to is worth more than the ordering.
 *
 * NO PER-ROW CURVE. There was one, at every depth, and it was most of the
 * height for very little: a category with two transactions drew a near-flat
 * line 128px tall. The hero already answers "what shape was this month"; a row
 * is opened to answer "what is IN it", which is the transaction list.
 *
 * COLOUR MEANS STATE, NEVER CATEGORY. The palette appears only as the identity
 * dot; painting the bar by category would spend the one channel that carries
 * meaning on decoration, leaving nothing to say "over".
 */

/**
 * Identity dot colour per category variant.
 *
 * A literal map, not `bg-${variant}-text`: Tailwind finds classes by scanning
 * source TEXT, so a constructed name is never generated and the dot would
 * silently render colourless. Mirrors Chip's variant table (ADR-0021 Rule 5).
 */
const DOT: Record<string, string> = {
    default: 'bg-chip-neutral-text',
    warn: 'bg-state-warning',
    flagged: 'bg-cat-rec-text',
    groc: 'bg-cat-groc-text',
    din: 'bg-cat-din-text',
    house: 'bg-cat-house-text',
    util: 'bg-cat-util-text',
    sub: 'bg-cat-sub-text',
    tran: 'bg-cat-tran-text',
    sal: 'bg-cat-sal-text',
    xfer: 'bg-cat-xfer-text',
    phone: 'bg-cat-phone-text',
    rec: 'bg-cat-rec-text',
};

interface CategoryRowsProps {
    rows: readonly BudgetCategoryRow[];
    /** id → full `Parent/Child` path, for the tooltip. The visible label is the
     *  bare leaf: this is a tree, and indentation already says the ancestry. */
    accountPaths: ReadonlyMap<string, string>;
    currency: string;
    ledgerId: string;
    /** `yyyy-MM`, for the per-row transaction query. */
    monthKey: string;
    /** Rows whose CHILDREN are shown. A set: with nesting, auto-collapse would
     *  mean opening a child closes its own parent. */
    expandedIds: ReadonlySet<string>;
    onToggle: (categoryId: string) => void;
    /** The one row whose transactions are open, or null. Single by design — the
     *  panel is the expensive thing on this screen. */
    detailId: string | null;
    onToggleDetail: (categoryId: string) => void;
    onSetTarget: (categoryId: string, amount: number | null) => void;
    saving?: boolean;
}

export function CategoryRows(props: CategoryRowsProps) {
    const { rows } = props;
    const roots = rootRows(rows);

    const childrenOf = new Map<string, BudgetCategoryRow[]>();
    for (const r of rows) {
        if (r.parentId === null) continue;
        const list = childrenOf.get(r.parentId);
        if (list === undefined) childrenOf.set(r.parentId, [r]);
        else list.push(r);
    }
    for (const list of childrenOf.values()) list.sort((a, b) => b.actual - a.actual);

    // Across the ROOTS, over both sides, so a category far under its mark still
    // shows the tick inside the track.
    const scaleMax = Math.max(...roots.map((r) => Math.max(r.actual, r.mark ?? 0)), 1) * 1.05;

    return (
        <table className="w-full text-sm">
            <caption className="sr-only">
                Spending by category against each one&apos;s target. The triangle shows a
                category&apos;s sub-categories; its name opens that month&apos;s transactions.
            </caption>
            <thead>
                <tr className="border-b border-border text-[0.625rem] uppercase tracking-wider text-text-muted">
                    <th scope="col" className="px-3 py-1.5 text-left font-semibold">Category</th>
                    <th scope="col" className="px-3 py-1.5 text-right font-semibold">Spent</th>
                    <th scope="col" className="px-3 py-1.5 text-right font-semibold">Target</th>
                    <th scope="col" className="px-3 py-1.5 text-right font-semibold">Difference</th>
                    <th scope="col" className="px-3 py-1.5 text-left text-[0.6875rem] font-normal normal-case tracking-normal">
                        bar = spent
                        <span aria-hidden className="mx-1 inline-block h-icon-2xs w-fixed-2px align-[-1px] bg-border-strong" />
                        = target
                    </th>
                </tr>
            </thead>
            <tbody>
                {roots.map((row) => (
                    <Row
                        key={row.categoryId}
                        row={row}
                        depth={0}
                        childrenOf={childrenOf}
                        scaleMax={scaleMax}
                        {...props}
                    />
                ))}
            </tbody>
        </table>
    );
}

interface RowProps extends CategoryRowsProps {
    row: BudgetCategoryRow;
    depth: number;
    childrenOf: ReadonlyMap<string, BudgetCategoryRow[]>;
    scaleMax: number;
}

function Row(props: RowProps) {
    const {
        row, depth, childrenOf, scaleMax, accountPaths, currency, ledgerId, monthKey,
        expandedIds, onToggle, detailId, onToggleDetail, onSetTarget, saving, rows,
    } = props;

    const kids = childrenOf.get(row.categoryId) ?? [];
    const open = expandedIds.has(row.categoryId);
    const detail = detailId === row.categoryId;
    const path = accountPaths.get(row.categoryId) ?? row.name;

    return (
        <>
            <tr className={cn(
                'border-b border-border/30',
                detail ? 'bg-accent-soft/20' : 'hover:bg-surface-hover/40',
            )}>
                <td className="py-1 pr-3" style={{ paddingLeft: `${0.75 + depth * 1.1}rem` }}>
                    <span className="flex min-w-0 items-center gap-1">
                        {/* Reveals CHILDREN. A leaf has none, so it gets a spacer
                            instead and the names stay on one vertical line. */}
                        {kids.length > 0 ? (
                            <button
                                type="button"
                                onClick={() => onToggle(row.categoryId)}
                                aria-expanded={open}
                                aria-label={open ? `Hide sub-categories of ${row.name}` : `Show sub-categories of ${row.name}`}
                                className="shrink-0 rounded px-0.5 text-text-subtle hover:text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                            >
                                {open ? '▾' : '▸'}
                            </button>
                        ) : (
                            <span aria-hidden className="inline-block w-fixed-12px shrink-0" />
                        )}
                        <span
                            aria-hidden
                            className={cn(
                                'size-dot-sm shrink-0 rounded-full',
                                DOT[categoryChipVariant(row.name, 'category', row.categoryId)] ?? DOT.default,
                            )}
                        />
                        {/* Opens this row's TRANSACTIONS — a different question from
                            "what is underneath it", and a much more expensive answer,
                            so it is a different control and only one may be open. */}
                        <button
                            type="button"
                            onClick={() => onToggleDetail(row.categoryId)}
                            aria-expanded={detail}
                            title={path}
                            className="min-w-0 truncate rounded px-1 text-left hover:text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        >
                            {/* The LEAF, not the path: this is a tree, so the
                                indentation carries the ancestry that the old flat
                                list had to spell out. Full path on hover. */}
                            {row.name}
                        </button>
                        {kids.length > 0 && !open ? (
                            <span className="shrink-0 text-[0.625rem] text-text-subtle">+{kids.length}</span>
                        ) : null}
                    </span>
                </td>
                <Numbers
                    row={row}
                    currency={currency}
                    conflict={targetConflict(rows, row.categoryId)}
                    onSetTarget={onSetTarget}
                    saving={saving}
                />
                <td className="px-3 py-1"><Bar row={row} scaleMax={scaleMax} /></td>
            </tr>

            {/* DIRECTLY UNDER ITS OWN ROW. Children follow, so a panel can never
                end up below a branch it does not belong to. */}
            {detail ? (
                <Transactions
                    ledgerId={ledgerId}
                    categoryId={row.categoryId}
                    monthKey={monthKey}
                    currency={currency}
                    hasKids={kids.length > 0}
                    depth={depth}
                />
            ) : null}

            {open ? kids.map((k) => (
                <Row key={k.categoryId} {...props} row={k} depth={depth + 1} />
            )) : null}
        </>
    );
}

/**
 * The month's transactions for this row — all of them, as REAL TABLE ROWS.
 *
 * WHY NOT A SCROLLING BLOCK. That was the previous cut, and it traded away the
 * thing it existed for. Spanning the first two columns does put a container at
 * the Spent column's right edge — but a scrollbar then takes ~15px OUT of that
 * container, so every amount lands short of the column above it and the
 * scrollbar track draws against the digits. Exact alignment and a
 * space-consuming scrollbar cannot both hold, and an overlay scrollbar is not
 * reliable across browsers.
 *
 * Real rows put the amount in the Spent cell itself, so it is aligned by
 * construction rather than by arithmetic. The cost is height: showing
 * everything means the block is as tall as the month was busy. That is the
 * honest price of "show all", and it is bounded in practice by one detail being
 * open at a time.
 *
 * (The first cut put these in a `colSpan={5}` grid, which is why the amounts
 * once sat at the far right under the BAR column, aligned with nothing. Three
 * attempts, one lesson: if it must line up with the table, it must BE the
 * table.)
 *
 * Covers the whole SUBTREE, so the list adds up to the number on the row it
 * hangs from — that row is a rollup. Fetched only while open: this component
 * does not mount otherwise, so a closed table costs nothing.
 */
function Transactions({
    ledgerId, categoryId, monthKey, currency, hasKids, depth,
}: {
    ledgerId: string;
    categoryId: string;
    monthKey: string;
    currency: string;
    hasKids: boolean;
    depth: number;
}) {
    const query = useQuery({
        // 200 is the endpoint's own ceiling rather than a display choice;
        // `hasMore` covers the category that manages to exceed it.
        queryKey: ['budget-transactions', ledgerId, categoryId, monthKey],
        queryFn: () => fetchBudgetTransactions(ledgerId, categoryId, monthKey, 200),
        staleTime: 30_000,
    });

    const indent = `${1.6 + depth * 1.1}rem`;

    const note = (content: ReactNode, tone = 'text-text-subtle') => (
        <tr className="bg-accent-soft/10">
            <td colSpan={5} className={cn('py-1 pr-3 text-[0.6875rem]', tone)} style={{ paddingLeft: indent }}>
                {content}
            </td>
        </tr>
    );

    // Reserves height while loading, so the block does not open one line tall
    // and then grow under the cursor — two shifts per click, the second landing
    // after the eye has settled.
    if (query.isPending) {
        return note(<span className="flex h-fixed-160px items-start">Loading transactions…</span>, 'text-text-muted');
    }
    if (query.isError) {
        return note('Could not load transactions for this category.', 'text-state-danger');
    }

    const lines = query.data?.lines ?? [];
    if (lines.length === 0) return note('No transactions this month.', 'text-text-muted');

    return (
        <>
            {lines.map((l) => (
                <tr key={`${l.headerId}-${l.accountId}`} className="bg-accent-soft/10 text-xs">
                    <td className="py-0.5 pr-3" style={{ paddingLeft: indent }}>
                        <span className="flex min-w-0 items-baseline gap-2">
                            <span className="shrink-0 tabular-nums text-text-subtle">
                                {formatLedgerDate(l.postedAt)}
                            </span>
                            <span className="min-w-0 truncate text-text-muted" title={l.memo ?? undefined}>
                                {l.payee ?? '(no payee)'}
                                {/* Which sub-category it landed in — the reason a
                                    subtree list is legible rather than a jumble.
                                    Only worth the width when there ARE children. */}
                                {hasKids && l.counterpartyAccountName !== null ? (
                                    <span className="ml-1 text-text-subtle">· {l.counterpartyAccountName}</span>
                                ) : null}
                            </span>
                        </span>
                    </td>
                    {/* The Spent cell itself — aligned by construction. */}
                    <td className="px-3 py-0.5 text-right font-mono tabular-nums text-text-muted">
                        {formatCurrency(Math.abs(l.amount), currency)}
                    </td>
                    <td colSpan={3} />
                </tr>
            ))}
            {note(
                <>
                    {query.data?.hasMore
                        ? `Showing the ${lines.length} most recent. `
                        : `${lines.length} transaction${lines.length === 1 ? '' : 's'} this month. `}
                    {/* With the subtree ON. A budget row is a ROLLUP, so the
                        number just read already contains every descendant; a
                        register scoped to the parent alone would open empty
                        and contradict the figure that prompted the click. */}
                    <Link
                        to="/ledgers/$ledgerId/accounts/$accountId"
                        params={{ ledgerId, accountId: categoryId }}
                        search={{ subcategories: true }}
                        className="text-accent hover:underline"
                    >
                        Open the register
                    </Link>
                    {' for the full history.'}
                </>,
            )}
        </>
    );
}

function Numbers({
    row, currency, conflict, onSetTarget, saving,
}: {
    row: BudgetCategoryRow;
    currency: string;
    conflict: string | null;
    onSetTarget: (categoryId: string, amount: number | null) => void;
    saving?: boolean;
}) {
    // Everything compares against the MARK, never `typical`: the mark is the
    // typed target where there is one, so judging against history after someone
    // has said what they intend is answering a question nobody asked.
    const known = row.mark !== null;
    const over = known && row.actual > (row.mark ?? 0);
    const diff = Math.abs(row.actual - (row.mark ?? 0));
    return (
        <>
            <td className="px-3 py-1 text-right font-mono tabular-nums">
                {formatCurrency(row.actual, currency)}
            </td>
            <td className="px-3 py-1 text-right">
                <TargetCell
                    row={row}
                    currency={currency}
                    conflict={conflict}
                    onSetTarget={onSetTarget}
                    saving={saving}
                />
            </td>
            <td className={cn('px-3 py-1 text-right font-mono tabular-nums', over ? 'text-state-danger' : 'text-text-muted')}>
                {!known ? 'new' : diff < 1 ? '—' : `${over ? '+' : '−'}${formatCurrency(diff, currency)}`}
            </td>
        </>
    );
}

/**
 * The one editable thing on this screen, on every row at every depth.
 *
 * AT REST it shows the row's mark. A TYPED target renders solid; a derived
 * normal renders muted and italic, which is the whole of the visual language
 * (ADR-0099 D1): presence of a stored number is the state, so the cell has to
 * say which kind of number you are looking at without a second column.
 *
 * IT COMMITS IN PLACE, which ADR-0023 section B otherwise reserves against for
 * tabular data; section B.2 records the scoped exception. B's stated objection
 * is the multi-cell trap, "where each click commits independently", and a row
 * with exactly one editable field does not have it.
 *
 * EMPTY CLEARS. Deleting the number returns the row to its derived normal
 * rather than storing a zero — zero is a real target meaning "spend nothing
 * here", and conflating the two would make it impossible to say either.
 *
 * A CONFLICTING CELL IS DISABLED, not merely rejected on submit: ADR-0099 D1a
 * allows a target on a node or on its descendants but never both, so where one
 * is impossible the control says so on hover and never takes the keystrokes.
 */
function TargetCell({
    row, currency, conflict, onSetTarget, saving,
}: {
    row: BudgetCategoryRow;
    currency: string;
    conflict: string | null;
    onSetTarget: (categoryId: string, amount: number | null) => void;
    saving?: boolean;
}) {
    const [editing, setEditing] = useState(false);
    const [draft, setDraft] = useState('');
    const inputRef = useRef<HTMLInputElement | null>(null);

    useEffect(() => { if (editing) inputRef.current?.select(); }, [editing]);

    if (conflict !== null) {
        return (
            <span
                className="cursor-not-allowed font-mono tabular-nums text-text-subtle"
                title={`“${conflict}” already has a target for this month, and one target `
                    + 'covers everything beneath it. Clear that one to set this.'}
            >
                {row.mark !== null ? formatCurrency(row.mark, currency) : '—'}
            </span>
        );
    }

    if (editing) {
        const commit = () => {
            setEditing(false);
            const trimmed = draft.trim();
            if (trimmed === '') { onSetTarget(row.categoryId, null); return; }
            const parsed = Number(trimmed);
            if (!Number.isFinite(parsed) || parsed < 0) return;   // keep the old value
            if (parsed === row.target) return;                    // nothing to say
            onSetTarget(row.categoryId, parsed);
        };
        return (
            <input
                ref={inputRef}
                type="number"
                min="0"
                step="0.01"
                inputMode="decimal"
                disabled={saving}
                className="w-fixed-96px rounded border border-accent bg-surface px-1 py-0.5 text-right font-mono text-sm tabular-nums focus-visible:outline-none"
                aria-label={`Target for ${row.name}`}
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onBlur={commit}
                onKeyDown={(e) => {
                    if (e.key === 'Enter') { e.preventDefault(); commit(); }
                    // Escape abandons the draft. Without it the only way out of a
                    // half-typed number is to blur, which COMMITS it.
                    if (e.key === 'Escape') { e.preventDefault(); setEditing(false); }
                }}
            />
        );
    }

    const typed = row.target !== null;
    return (
        <button
            type="button"
            onClick={() => {
                setDraft(row.target !== null ? String(row.target) : '');
                setEditing(true);
            }}
            title={typed ? 'Your target. Clear it to go back to the usual.' : 'Set a target'}
            className={cn(
                'rounded px-1 font-mono tabular-nums hover:bg-surface-hover',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                typed ? 'text-text' : 'italic text-text-muted',
            )}
        >
            {row.mark !== null ? formatCurrency(row.mark, currency) : '—'}
        </button>
    );
}

function Bar({ row, scaleMax }: { row: BudgetCategoryRow; scaleMax: number }) {
    // MARK, not typical — all three of the tick, the teal run and the red
    // overflow. Leaving the split on history while the tick moved to the target
    // would paint a red bar with its own tick sitting to the RIGHT of the red:
    // the most visible pixels on the screen contradicting the number beside
    // them, for a user who had just deliberately raised the target.
    const mark = row.mark;
    const over = mark !== null && row.actual > mark;
    const pct = (v: number) => `${Math.max(0, Math.min(100, (v / scaleMax) * 100))}%`;
    return (
        <span aria-hidden className="relative block h-control-20px">
            <span
                className="absolute top-1.5 h-fixed-8px rounded-l-full bg-accent"
                style={{ left: 0, width: pct(Math.min(row.actual, mark ?? row.actual)) }}
            />
            {over ? (
                <span
                    className="absolute top-1.5 h-fixed-8px rounded-r-full bg-state-danger"
                    style={{ left: pct(mark ?? 0), width: pct(row.actual - (mark ?? 0)) }}
                />
            ) : null}
            {mark !== null ? (
                <span
                    className="absolute top-0 h-full w-fixed-2px bg-border-strong"
                    style={{ left: pct(mark) }}
                />
            ) : null}
        </span>
    );
}
