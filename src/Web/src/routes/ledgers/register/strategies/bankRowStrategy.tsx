import type { ReactNode } from 'react';

import type { BankRow } from '@/lib/types';
import { Chip } from '@/components/ui/Chip';
import { TagChip } from '@/components/tags/TagChip';
import { ProvenanceIcon } from '@/components/register/ProvenanceIcon';
import { categoryChipVariant } from '@/lib/categoryChip';
import { displayAccountPath } from '@/lib/accountPath';
import { formatLedgerDate } from '@/lib/dates';
import { formatCurrency, formatSignedAmount } from '@/lib/money';

import type {
    RegisterRowBodyCtx,
    RegisterRowContainerCtx,
    RegisterRowStrategy,
} from '../shell/RegisterRow';
import { isInvestmentOwnedRow, resolveRowStatus, taxDateSubLabel } from '../bank/columns';
import {
    BANK_COLS,
    renderBankSlot4,
    renderBankSlot5,
    renderBankSlot6,
} from './bankStrategy';

// ---------------------------------------------------------------------------
// Bank register row STRATEGY (ADR-0030 reuse). Implements
// `RegisterRowStrategy<BankRow>`: the cells AFTER the shared lead for all
// three row variants (txn / split-parent / split-leg) via one
// `renderBody` switch. This is where the per-variant JSX from the three
// former bank row components (BankTxnRow, SplitParentRowCells,
// SplitLegRowCells) was relocated verbatim — same class strings, same
// slot renderers from `./bankStrategy`, same tree glyph / expand control
// / read-only "↗" treatment. The shell (shell/RegisterRow) owns the
// container + lead.
// ---------------------------------------------------------------------------

function renderTxnBody(txn: BankRow, ctx: RegisterRowBodyCtx): ReactNode {
    const { currency, today } = ctx;
    const status = resolveRowStatus(txn, today);
    const scheduled = status === 'scheduled';
    // Read-only row guards (mirror the former BankTxnRow): an
    // investment-owned cash leg (canonical owner is the brokerage
    // register) or a split counter-side (source side owns edit / delete).
    const isInvestmentOwned = isInvestmentOwnedRow(txn);
    const isReadOnly = ctx.isTargetSplit;
    const taxDateLabel = taxDateSubLabel(txn);
    const dateSubLabel = taxDateLabel ? `tax ${taxDateLabel}` : null;
    return (
        <>
            <span role="cell" className="font-mono tabular-nums">
                <span className="block leading-tight">
                    {formatLedgerDate(txn.postedAt)}
                </span>
                {dateSubLabel ? (
                    <span className="block truncate text-[0.625rem] leading-tight text-text-subtle">
                        {dateSubLabel}
                    </span>
                ) : null}
            </span>
            <span
                role="cell"
                className="truncate font-mono tabular-nums text-text-muted"
            >
                {renderBankSlot4(txn)}
            </span>
            <span role="cell" className="min-w-0">
                {isReadOnly ? (
                    <span className="flex min-w-0 items-baseline gap-1">
                        <span
                            className="inline-flex shrink-0 items-center rounded bg-accent-soft px-1 py-px text-[0.625rem] font-medium uppercase tracking-wide text-accent"
                            title={
                                isInvestmentOwned
                                    ? 'Row owned by an investment transaction · view + edit in the brokerage register'
                                    : 'Counter-side of a split transaction · edit + delete from the source-side register'
                            }
                        >
                            {isInvestmentOwned ? '↗ Investment' : '↗ Split'}
                        </span>
                        <span className="min-w-0">
                            {renderBankSlot5(txn)}
                        </span>
                    </span>
                ) : (
                    renderBankSlot5(txn)
                )}
            </span>
            <span role="cell" className="min-w-0 space-y-1">
                {renderBankSlot6(txn, ctx.accountPaths, ctx.rollupRootId)}
            </span>
            <span
                role="cell"
                className={
                    'text-right font-mono tabular-nums ' +
                    (scheduled
                        ? ''
                        : txn.amount < 0
                            ? 'text-state-danger'
                            : 'text-text')
                }
            >
                <span className="block">
                    {formatSignedAmount(txn.amount, currency)}
                </span>
            </span>
            <span
                role="cell"
                className="text-right font-mono tabular-nums"
            >
                {formatCurrency(txn.balanceAfter, currency)}
            </span>
        </>
    );
}

/**
 * One entry per DISTINCT counterparty across a split group, in first-seen
 * order, with amounts summed and legs counted.
 *
 * Deduplication is not cosmetic. An ADR-0036 target cluster's legs frequently
 * share ONE counterparty, so a naive per-leg list prints the same category
 * three times; and a paycheck's Medicare Tax can appear twice (regular plus
 * surtax), which a reader expects to see added up, not listed twice.
 *
 * Amounts come from the LEGS and never from the parent row: a split parent's
 * `amount` is the group's NET, so pairing it with any one category would
 * attribute a whole paycheck to it.
 */
interface SplitCategoryEntry {
    id: string | null | undefined;
    label: string | null | undefined;
    name: string | null | undefined;
    type: string | null | undefined;
    sum: number;
    legCount: number;
}

function summarizeSplitCategories(
    legs: readonly BankRow[],
    accountPaths?: ReadonlyMap<string, string>,
): readonly SplitCategoryEntry[] {
    const byId = new Map<string, SplitCategoryEntry>();
    for (const leg of legs) {
        // Key on the id when there is one; fall back to the name so two
        // uncategorised legs do not merge into one misleading entry.
        const key = leg.counterpartyAccountId ?? `name:${leg.counterpartyAccountName ?? ''}`;
        const found = byId.get(key);
        if (found !== undefined) {
            found.sum += leg.amount;
            found.legCount += 1;
            continue;
        }
        byId.set(key, {
            id: leg.counterpartyAccountId,
            name: leg.counterpartyAccountName,
            type: leg.counterpartyAccountType,
            label: displayAccountPath(
                accountPaths, leg.counterpartyAccountId, leg.counterpartyAccountName,
            ),
            sum: leg.amount,
            legCount: 1,
        });
    }
    return [...byId.values()];
}

/**
 * Which category stands for the whole split in one line.
 *
 * The ACTIVE FILTER WINS, because the row is on screen BECAUSE of it: the
 * server filters at entry level, so filtering by Insurance/Health returns the
 * paycheck, and the one thing the collapsed row has to answer is "which leg
 * matched, and for how much".
 *
 * Failing that, the largest entry by ABSOLUTE summed amount. On both real
 * shapes in this repo — a paycheck (gross pay against a dozen deductions) and
 * a two-way grocery split — one entry carries most of the group, and it is the
 * one a human would name the transaction after. "Most common category" is a
 * coin flip on both (a paycheck's deductions are all distinct; a two-way split
 * ties), and "sign matches the net" discriminates nothing on an all-negative
 * split.
 */
function leadSplitCategory(
    entries: readonly SplitCategoryEntry[],
    filterCategoryId?: string | null,
): { entry: SplitCategoryEntry; matchedFilter: boolean } | null {
    if (entries.length === 0) return null;
    if (filterCategoryId != null) {
        const hit = entries.find((e) => e.id != null && e.id === filterCategoryId);
        if (hit !== undefined) return { entry: hit, matchedFilter: true };
    }
    let best = entries[0]!;
    for (const e of entries) {
        // Strictly greater, so a tie keeps the first-seen (leg order) entry.
        if (Math.abs(e.sum) > Math.abs(best.sum)) best = e;
    }
    return { entry: best, matchedFilter: false };
}

function renderSplitParentBody(row: BankRow, ctx: RegisterRowBodyCtx<BankRow>): ReactNode {
    // The page synthesizes the representative parent row as the canonical
    // leg with the group's amount + balance-after-last-leg, so the cells
    // read off `row` directly (same fields the former SplitParentRowCells
    // computed from canonical / groupAmount / groupBalanceAfter).
    const { currency, today, expand, accountPaths, legs, filterCategoryId } = ctx;
    const status = resolveRowStatus(row, today);
    const scheduled = status === 'scheduled';
    const taxDateLabel = taxDateSubLabel(row);
    // The collapsed row used to show NO category anywhere — the expand toggle
    // occupied the whole cell. That is worst immediately after a category
    // filter, which matches per LEG on the server and hands back a group whose
    // row then names neither the matching leg nor its amount.
    const lead = leadSplitCategory(
        summarizeSplitCategories(legs ?? [], accountPaths),
        filterCategoryId,
    );
    return (
        <>
            <span role="cell" className="font-mono tabular-nums">
                <span className="block leading-tight">
                    {formatLedgerDate(row.postedAt)}
                </span>
                {taxDateLabel ? (
                    <span className="block text-[0.625rem] leading-tight text-text-subtle">
                        tax {taxDateLabel}
                    </span>
                ) : null}
            </span>
            <span
                role="cell"
                className="truncate font-mono tabular-nums text-text-muted"
            >
                {row.checkNumber ?? ''}
            </span>
            <span role="cell" className="min-w-0">
                {/* Parent row shows payee + raw header memo
                    (migration 032, ADR-0025). Mig 107: leading
                    provenance icon. */}
                <span className="flex items-center gap-1.5 truncate font-medium">
                    <ProvenanceIcon
                        origin={row.origin}
                        providerKey={row.providerKey}
                        isMergeWinner={row.isMergeWinner}
                    />
                    <span className="truncate">
                        {row.payee ?? (
                            <span className="text-text-subtle">—</span>
                        )}
                    </span>
                </span>
                {row.headerMemo ? (
                    <span className="block truncate text-[0.6875rem] text-text-muted">
                        {row.headerMemo}
                    </span>
                ) : null}
            </span>
            {/* Combined category · tags column. The split parent leads with
                the category that stands for the group — at the cell's LEFT
                edge, exactly where a flat row's chip sits, so the column reads
                straight down rather than alternating between two shapes — and
                pins the expand toggle to the right. Tags belong to the header
                so they wrap beneath. */}
            <span role="cell" className="min-w-0 space-y-1">
                <span className="flex min-w-0 items-center gap-1">
                    {lead !== null ? (
                        <Chip
                            variant={categoryChipVariant(
                                lead.entry.name ?? null, lead.entry.type ?? null, lead.entry.id ?? null,
                            )}
                            className="min-w-0 max-w-full truncate"
                            title={
                                lead.matchedFilter
                                    ? `${lead.entry.label} — matches the active category filter`
                                    : lead.entry.label ?? undefined
                            }
                        >
                            <span className="truncate">{lead.entry.label}</span>
                        </Chip>
                    ) : (
                        <span className="text-text-subtle">—</span>
                    )}
                    {/* The matched leg's own summed amount, shown ONLY when the
                        filter picked this category. Unfiltered it would be
                        noise — and worse, a second figure on a row whose
                        Amount column already shows the group's net, inviting
                        the reader to subtract one from the other. */}
                    {lead?.matchedFilter === true ? (
                        <span className="shrink-0 font-mono text-[0.625rem] tabular-nums text-text-muted">
                            {formatSignedAmount(lead.entry.sum, currency)}
                        </span>
                    ) : null}
                    <button
                        type="button"
                        aria-expanded={expand?.expanded ?? false}
                        onClick={(e) => {
                            // The row underneath handles click (focus).
                            e.stopPropagation();
                            expand?.onToggle();
                        }}
                        // BOTH handlers are needed, and that is the whole
                        // trap. `dblclick` is its own DOM event, not something
                        // derived from the two clicks, so stopping click
                        // propagation does nothing for it — an impatient
                        // double-click on the toggle used to expand the split
                        // AND drop the editor on top of it.
                        //
                        // The investment strategy stops only the click and
                        // looks fine, but that is luck: its split parents are
                        // readOnly, so RegisterRow never wires a double-click
                        // handler for the event to reach.
                        onDoubleClick={(e) => e.stopPropagation()}
                        title={
                            expand?.expanded === true
                                ? 'Hide this split’s categories'
                                : 'Show this split’s categories'
                        }
                        className="ml-auto inline-flex shrink-0 items-center gap-1 rounded px-1.5 py-0.5 text-[0.6875rem] font-medium text-text-muted hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1"
                    >
                        <span aria-hidden>{expand?.expanded ? '▾' : '▸'}</span>
                        {/* The count stays in the TEXT, not in an aria-label:
                            an aria-label here would be the button's whole
                            accessible name and the category chip beside it
                            would be all a screen reader lost. */}
                        {expand?.count ?? 0} splits
                    </button>
                </span>
                {row.tags.length > 0 ? (
                    <span className="flex flex-wrap gap-1">
                        {row.tags.map((tag) => (
                            <TagChip key={tag} name={tag} />
                        ))}
                    </span>
                ) : null}
            </span>
            <span
                role="cell"
                className={
                    'text-right font-mono tabular-nums ' +
                    (scheduled
                        ? ''
                        : row.amount < 0
                            ? 'text-state-danger'
                            : 'text-text')
                }
            >
                {formatSignedAmount(row.amount, currency)}
            </span>
            <span role="cell" className="text-right font-mono tabular-nums">
                {formatCurrency(row.balanceAfter, currency)}
            </span>
        </>
    );
}

function renderSplitLegBody(leg: BankRow, ctx: RegisterRowBodyCtx): ReactNode {
    const { currency } = ctx;
    const chipVariant = categoryChipVariant(
        leg.counterpartyAccountName,
        leg.counterpartyAccountType,
        leg.counterpartyAccountId,
    );
    const categoryLabel = displayAccountPath(
        ctx.accountPaths, leg.counterpartyAccountId, leg.counterpartyAccountName,
    );
    // Leg body: the two blank lead cells are rendered by the shell; this
    // covers the four remaining blank cells + memo / category / amount /
    // blank-balance (mirrors the former SplitLegRowCells).
    return (
        <>
            <span role="cell" />
            <span role="cell" />
            <span role="cell" className="min-w-0 pl-4">
                {/* Tree-prefix glyph + raw leg memo (migration 032,
                    ADR-0025). Blank when this leg has no per-leg memo. */}
                <span className="block truncate">
                    <span aria-hidden className="mr-1 text-text-subtle">└</span>
                    {leg.legMemo ?? <span className="text-text-subtle">—</span>}
                </span>
            </span>
            {/* Combined category · tags. Leg rows are per-posting, so tags
                (header-level) are intentionally NOT rendered here — they
                sit on the parent row. Just the category chip. */}
            <span role="cell" className="min-w-0">
                {leg.counterpartyAccountName ? (
                    <Chip
                        variant={chipVariant}
                        className="max-w-full truncate"
                        title={categoryLabel ?? undefined}
                    >
                        <span className="truncate">
                            {categoryLabel}
                        </span>
                    </Chip>
                ) : (
                    <span className="text-text-subtle">—</span>
                )}
            </span>
            <span
                role="cell"
                className={
                    'text-right font-mono tabular-nums ' +
                    (leg.amount < 0 ? 'text-state-danger' : 'text-text')
                }
            >
                {formatSignedAmount(leg.amount, currency)}
            </span>
            {/* Balance intentionally blank on leg rows — only the group's
                final balance is meaningful in the running-total flow. */}
            <span role="cell" />
        </>
    );
}

// Bank-specific container attributes (preserved exactly as the three
// former bank components emitted them): `aria-rowindex` on every
// variant; `data-scheduled` + `data-needs-review` on the txn row only;
// the `italic text-text-subtle` scheduled treatment on txn + parent.
// Bank-specific container attribute: `aria-rowindex` on every variant. The
// row-STATE styling (scheduled / needs-review / hidden + leg muting) now lives
// in the shared RegisterRow so bank + investment render it identically.
function bankContainerAttrs(row: BankRow, ctx: RegisterRowContainerCtx) {
    return {
        dataAttrs: {
            'aria-rowindex':
                ctx.rowIndex !== undefined ? ctx.rowIndex + 1 : undefined,
            // Mirrors the investment strategy. Focus was previously visible on a
            // bank row only as styling, so "which row has the keyboard cursor"
            // could not be asserted — which is why the anchor bug below it went
            // unnoticed: nothing could see that focus had landed on the wrong
            // row. `data-headerid` is omitted on legs, as investment omits it,
            // because a leg is not the header's row.
            'data-headerid': ctx.variant === 'split-leg' ? undefined : row.headerId,
            'data-focused': ctx.focused ? 'true' : 'false',
        },
    };
}

export const bankRowStrategy: RegisterRowStrategy<BankRow> = {
    cols: BANK_COLS,
    rowClassName: 'min-h-control-36px items-center',
    cursorClassName: 'cursor-pointer',
    containerAttrs: bankContainerAttrs,
    renderBody(row, ctx) {
        switch (ctx.variant) {
            case 'split-parent':
                return renderSplitParentBody(row, ctx);
            case 'split-leg':
                return renderSplitLegBody(row, ctx);
            default:
                return renderTxnBody(row, ctx);
        }
    },
};
