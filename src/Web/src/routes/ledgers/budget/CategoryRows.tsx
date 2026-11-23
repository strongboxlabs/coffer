import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { fetchBudgetTransactions } from '@/lib/api';
import { amountText, atDisplayPrecision, currencySymbol, formatCurrency, roundToMinorUnits } from '@/lib/money';
import { MoneyInput } from '@/components/ui/MoneyInput';
import { formatLedgerDate } from '@/lib/dates';
import { categoryDotClass } from '@/lib/categoryChip';
import { MarkBar } from '@/components/ui/MarkBar';
import { useFocusHandOff } from '@/components/ui/rowRemovalFocus';
import { cn } from '@/lib/cn';
import type { BudgetCategoryRow, BudgetTransactionLine, RateUnavailable } from '@/lib/types';

import { NoRate } from '../home/Money';
import { hasStoredTarget, pastMark, rootRows, targetConflict } from './cumulative';

/** The target editor's width before its currency's symbol (the `w-fixed-96px` token). */
const TARGET_FIGURE_WIDTH = 'var(--spacing-fixed-96px)';

/**
 * The category table: a TREE, one row per category at any depth, each row
 * expandable into its children and openable to its own transactions.
 *
 * WHY A TREE AND NOT ROOTS-PLUS-A-LIST. Rendering roots as real rows and a
 * category's children as a flat list inside the expanded panel has three
 * problems, and all three go away here: the child list cannot align with the
 * table (a `<ul>` of flex spans and a `<table>`'s columns are different layout
 * systems, and no amount of padding reconciles them); children cannot carry an
 * editable target, though ADR-0099 D1a was designed to allow exactly that; and
 * children have no transactions of their own, so "open it up and look" works at
 * one level only.
 *
 * ROOTS STILL GOVERN THE ARITHMETIC. The table is a tree; the CEILING is still
 * the sum over roots, because the API rolls up and a parent's number already
 * contains its children. Those are different questions — presentation versus
 * double-counting — and conflating them draws the ceiling too high by every
 * child's share. `rootRows` is used here only to decide what sits at depth 0.
 *
 * ONE SCALE FOR EVERY BAR, at every depth. Bar length means money, full stop,
 * so a child's bar is short because a child is smaller. Rescaling per level
 * would make a small category look like one a hundred times its size two rows apart.
 *
 * TWO GESTURES, NOT ONE. The triangle reveals CHILDREN — structural, cheap, and
 * many may be open at once. Clicking the NAME opens that row's transactions,
 * and only one such panel is open at a time. As a single control the two make
 * the screen unreadable: a row costs ~29px and a panel ~215px, so every
 * expansion would be seven rows tall and two of them would bury the table.
 *
 * THE PANEL SITS DIRECTLY UNDER ITS OWN ROW, above that row's children.
 * Children first reads well in the abstract and orphans the panel: a parent's
 * detail ends up below all its children and any nested panel of theirs, with
 * nothing but a thin rule to say whose it is. Touching the row it belongs to is
 * worth more than the ordering.
 *
 * NO PER-ROW CURVE. One at every depth would be most of the height for very
 * little: a category with two transactions draws a near-flat line 128px tall. The hero already answers "what shape was this month"; a row
 * is opened to answer "what is IN it", which is the transaction list.
 *
 * COLOUR MEANS STATE, NEVER CATEGORY. The palette appears only as the identity
 * dot; painting the bar by category would spend the one channel that carries
 * meaning on decoration, leaving nothing to say "over".
 */

interface CategoryRowsProps {
    rows: readonly BudgetCategoryRow[];
    /** id → full `Parent/Child` path, for the tooltip. The visible label is the
     *  bare leaf: this is a tree, and indentation already says the ancestry. */
    accountPaths: ReadonlyMap<string, string>;
    /** The ledger's system category ids (its Uncategorized): a neutral dot.
     *  Required, so no host can leave it out and colour Uncategorized. */
    systemCategoryIds: ReadonlySet<string>;
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
    /** Owner or editor: a target can be set. A viewer reads each row's mark. */
    canEdit: boolean;
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
    // By spend, largest first; a row whose spend needs a missing rate goes last.
    for (const list of childrenOf.values()) {
        list.sort((a, b) => (a.actual === null ? (b.actual === null ? 0 : 1)
            : b.actual === null ? -1 : b.actual - a.actual));
    }

    // Across the ROOTS, over both sides, so a category far under its mark still
    // shows the tick inside the track.
    const scaleMax = Math.max(...roots.map((r) => Math.max(r.actual ?? 0, r.mark ?? 0)), 1) * 1.05;

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
        expandedIds, onToggle, detailId, onToggleDetail, onSetTarget, saving, rows, canEdit,
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
                                categoryDotClass(row.name, row.categoryId, props.systemCategoryIds.has(row.categoryId)),
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
                    canEdit={canEdit}
                />
                <td className="px-3 py-1">
                    {/* No run for a spend that needs a missing rate: it is not a figure.
                        The mark's tick alone still says where the row is judged. */}
                    <MarkBar
                        actual={row.actual === null ? 0 : atDisplayPrecision(row.actual, currency)}
                        mark={row.mark === null ? null : atDisplayPrecision(row.mark, currency)}
                        scaleMax={scaleMax}
                        noRun={row.actual === null}
                    />
                </td>
            </tr>

            {/* DIRECTLY UNDER ITS OWN ROW. Children follow, so a panel can never
                end up below a branch it does not belong to. */}
            {detail ? (
                <Transactions
                    ledgerId={ledgerId}
                    categoryId={row.categoryId}
                    monthKey={monthKey}
                    hasKids={kids.length > 0}
                    depth={depth}
                    currency={currency}
                    missing={row.rateUnavailable ?? []}
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
 * WHY NOT A SCROLLING BLOCK. It trades away the thing it exists for. Spanning
 * the first two columns does put a container at
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
 * (A `colSpan={5}` grid puts the amounts at the far right under the BAR column,
 * aligned with nothing. If it must line up with the table, it must BE the
 * table.)
 *
 * Covers the whole SUBTREE, so the list adds up to the number on the row it
 * hangs from — that row is a rollup. Fetched only while open: this component
 * does not mount otherwise, so a closed table costs nothing.
 *
 * Each line is named by its own leg and printed in its own currency, the paying
 * account's: a split's lines share their header and account, and a ledger's
 * accounts need not share a currency. The row's figure is in the reporting
 * currency, each line converted at its own date (ADR-0114 D7), so where lines are in
 * another currency the closing note says so: that is where a reader adds the lines
 * up and finds they do not match the row. A line whose day has no rate says so on
 * itself (ADR-0114 D9).
 */
function Transactions({
    ledgerId, categoryId, monthKey, hasKids, depth, currency, missing,
}: {
    ledgerId: string;
    categoryId: string;
    monthKey: string;
    hasKids: boolean;
    depth: number;
    /** The reporting currency the row's figure is in. */
    currency: string;
    /** Every missing rate the row's figures needed. */
    missing: readonly RateUnavailable[];
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

    // The other currencies the lines are in, by the symbol each line prints in.
    const others = [...new Set(lines.map((l) => l.currencyCode).filter((c) => c !== currency))].sort();
    const symbols = others.map((c) => currencySymbol(c));
    const converted = others.length === 0 ? null
        : `The ${symbols.length === 1 ? symbols[0] : `${symbols.slice(0, -1).join(', ')} and ${symbols.at(-1)}`} lines `
            + `are converted to ${currency} at each one's date in the row's figure. `;
    const noRateFor = (l: BudgetTransactionLine) => missing.some((m) =>
        m.fromCurrency === l.currencyCode && m.toCurrency === currency && m.date === l.postedAt.slice(0, 10));

    return (
        <>
            {lines.map((l) => (
                <tr key={l.legId} className="bg-accent-soft/10 text-xs">
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
                                {noRateFor(l) ? (
                                    <span className="ml-1 text-text-subtle">
                                        · no {l.currencyCode} to {currency} rate for this day
                                    </span>
                                ) : null}
                            </span>
                        </span>
                    </td>
                    {/* The Spent cell itself — aligned by construction. */}
                    <td className="px-3 py-0.5 text-right font-mono tabular-nums text-text-muted">
                        {formatCurrency(Math.abs(l.amount), l.currencyCode)}
                    </td>
                    <td colSpan={3} />
                </tr>
            ))}
            {note(
                <>
                    {query.data?.hasMore
                        ? `Showing the ${lines.length} most recent. `
                        : `${lines.length} transaction${lines.length === 1 ? '' : 's'} this month. `}
                    {converted}
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
    row, currency, conflict, onSetTarget, saving, canEdit,
}: {
    row: BudgetCategoryRow;
    currency: string;
    conflict: string | null;
    onSetTarget: (categoryId: string, amount: number | null) => void;
    saving?: boolean;
    canEdit: boolean;
}) {
    // Everything compares against the MARK, never `typical`: the mark is the
    // typed target where there is one, so judging against history after someone
    // has said what they intend is answering a question nobody asked. Over is
    // over: any amount past it prints, in danger, and "—" means exactly at it.
    const diff = pastMark(row, currency);
    const over = diff !== null && diff > 0;
    const missing = row.rateUnavailable ?? [];
    return (
        <>
            <td className="px-3 py-1 text-right font-mono tabular-nums">
                {row.actual === null ? <NoRate missing={missing} /> : formatCurrency(row.actual, currency)}
            </td>
            <td className="px-3 py-1 text-right">
                <TargetCell
                    row={row}
                    currency={currency}
                    conflict={conflict}
                    onSetTarget={onSetTarget}
                    saving={saving}
                    canEdit={canEdit}
                />
            </td>
            <td className={cn('px-3 py-1 text-right font-mono tabular-nums', over ? 'text-state-danger' : 'text-text-muted')}>
                {diff === null && missing.length > 0 ? <NoRate missing={missing} />
                    : diff === null ? 'new'
                        : diff === 0 ? '—' : `${over ? '+' : '−'}${formatCurrency(Math.abs(diff), currency)}`}
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
 *
 * A TARGET THAT DID NOT CONVERT is still a typed target (ADR-0114 D9, D12): typed
 * in another currency whose rate for the month is missing, it arrives with
 * `target` null and its own `targetCurrencyCode`. It renders solid, reads "no
 * rate" with its pair and day on hover, and can still be cleared. There is no
 * number in the field to delete, so the field opens empty and Enter on it clears;
 * leaving it any other way keeps the target, since opening a cell and leaving it
 * untouched must never delete what is stored.
 */
function TargetCell({
    row, currency, conflict, onSetTarget, saving, canEdit,
}: {
    row: BudgetCategoryRow;
    currency: string;
    conflict: string | null;
    onSetTarget: (categoryId: string, amount: number | null) => void;
    saving?: boolean;
    /** A viewer reads the mark, in the same typed-or-usual style, and sets nothing. */
    canEdit: boolean;
}) {
    const [editing, setEditing] = useState(false);
    const [draft, setDraft] = useState('');
    const inputRef = useRef<HTMLInputElement | null>(null);
    // Enter or Escape takes the field away with focus on it, and the cell's
    // button takes its place and its focus (ADR-0023 §L). A blur has moved
    // focus already, so it stays where the reader put it.
    const buttonRef = useRef<HTMLButtonElement | null>(null);
    const handOff = useFocusHandOff(() => buttonRef.current);
    const fieldRef = useCallback((el: HTMLInputElement | null) => {
        inputRef.current = el;
        handOff(el);
    }, [handOff]);

    // Opening swaps the other way: the pressed button gives way to the field,
    // which takes its focus. select() alone does not focus it in every browser.
    useEffect(() => {
        if (!editing) return;
        inputRef.current?.focus();
        inputRef.current?.select();
    }, [editing]);

    // A mark that is null for a missing rate (ADR-0114 D9) is not "nothing to compare
    // against": a target typed in another currency can sit behind it, so it reads
    // "no rate", never the dash an unset row reads.
    const missing = row.rateUnavailable ?? [];
    const typed = hasStoredTarget(row);
    const unconverted = typed && row.target === null;
    // An unconverted target's own pair is the one to name on hover; the row's other
    // missing rates (its spend's, its window's) are named on their own figures.
    const targetMissing = unconverted
        ? missing.filter((m) => m.fromCurrency === row.targetCurrencyCode && m.toCurrency === currency)
        : [];
    const shown = row.mark !== null ? formatCurrency(row.mark, currency)
        : targetMissing.length > 0 ? <NoRate missing={targetMissing} />
            : missing.length > 0 ? <NoRate missing={missing} /> : '—';

    if (!canEdit) {
        return (
            <span className={cn('px-1 font-mono tabular-nums', typed ? 'text-text' : 'italic text-text-muted')}>
                {shown}
            </span>
        );
    }

    if (conflict !== null) {
        return (
            <span
                className="cursor-not-allowed font-mono tabular-nums text-text-subtle"
                title={`“${conflict}” already has a target for this month, and one target `
                    + 'covers everything beneath it. Clear that one to set this.'}
            >
                {shown}
            </span>
        );
    }

    if (editing) {
        const commit = (how: 'enter' | 'blur') => {
            setEditing(false);
            const trimmed = draft.trim();
            // Empty clears a target the cell showed, by blur or Enter. One that could
            // not convert (ADR-0114 D9) had no number to delete, so the field opened
            // empty, and only Enter clears it: a blur means the reader left a field
            // they never changed.
            if (trimmed === '') {
                if (row.target !== null || (unconverted && how === 'enter')) onSetTarget(row.categoryId, null);
                return;
            }
            const parsed = Number(trimmed);
            if (!Number.isFinite(parsed) || parsed < 0) return;   // keep the old value
            if (parsed === row.target) return;                    // nothing to say
            // A typed target is the reporting currency's money, at its places, half
            // away from zero, by Enter as by blur (ADR-0114 D12, D15). The figure left
            // as it opened returned above, so a stored one finer than its currency is
            // never rewritten by a visit.
            const rounded = roundToMinorUnits(parsed, currency);
            if (rounded === row.target) return;
            onSetTarget(row.categoryId, rounded);
        };
        return (
            <MoneyInput
                ref={fieldRef}
                // The target is typed in the reporting currency, which is what the save
                // stores (ADR-0114 D12, D14).
                currency={currency}
                min="0"
                // Shrinks to the field, so the symbol sits at the field's edge, and keeps
                // to the cell's right alignment.
                wrapperClassName="inline-block"
                // 96px for the figure, plus the symbol's room: a three-character symbol
                // would otherwise cut an ordinary target short (ADR-0114 D14).
                figureWidth={TARGET_FIGURE_WIDTH}
                className="rounded border border-accent bg-surface px-1 py-0.5 text-right font-mono text-sm tabular-nums focus-visible:outline-none aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                aria-label={`Target for ${row.name}`}
                value={draft}
                onValueChange={setDraft}
                placeholder={unconverted ? 'Enter clears' : undefined}
                pending={saving}
                onBlur={() => commit('blur')}
                onKeyDown={(e) => {
                    if (e.key === 'Enter') { e.preventDefault(); commit('enter'); }
                    // Escape abandons the draft. Without it the only way out of a
                    // half-typed number is to blur, which COMMITS it.
                    if (e.key === 'Escape') { e.preventDefault(); setEditing(false); }
                }}
            />
        );
    }

    const typedIn = unconverted ? ` Typed in ${row.targetCurrencyCode}, which has no rate for this month.` : '';
    return (
        <button
            ref={buttonRef}
            type="button"
            onClick={() => {
                setDraft(row.target !== null ? amountText(row.target, currency) : '');
                setEditing(true);
            }}
            title={unconverted ? `Your target.${typedIn} Press Enter on the empty field to clear it, or type a new one.`
                : typed ? 'Your target. Clear it to go back to the usual.' : 'Set a target'}
            className={cn(
                'rounded px-1 font-mono tabular-nums hover:bg-surface-hover',
                'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                typed ? 'text-text' : 'italic text-text-muted',
            )}
        >
            {shown}
        </button>
    );
}

