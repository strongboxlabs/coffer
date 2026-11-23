import type { InvestmentRow, LedgerInvestmentAction } from '@/lib/types';
import investmentActionWords from '@/lib/investmentActionWords.json';
import { Chip } from '@/components/ui/Chip';
import { categoryChipVariant } from '@/lib/categoryChip';
import { displayAccountPath } from '@/lib/accountPath';
import { ProvenanceIcon } from '@/components/register/ProvenanceIcon';
import { formatShares, formatPrice, formatCurrency } from '@/lib/money';
import { taxDateSubLabel } from '../bank/columns';
import { renderAccountChip } from './bankStrategy';
import { renderRowMemo, renderRowTags } from './rowNotes';

/**
 * Investment-register cell renderers (slice A1.c / A1.d). Consumed
 * directly by <c>InvestmentRow</c> (the investment register renders
 * its own row component rather than going through a shared dispatch).
 *
 * Slot layout (9-column grid — see `../investment/columns.ts`):
 *   1 status     2 checkbox    3 date + tax date   4 Action chip + check#
 *   5 Description (payee + memo)
 *   6 Category | Transfer (line 1)  ·  Fee category (line 2)  ·  Tags (line 3)
 *   7 Security · Shares @ Price            ← new vs bank
 *   8 Amount + fee subtitle
 *   9 Cash balance
 *
 * Multi-posting headers (Buy+Fee, Reinvest, BuyXfr / SellXfr /
 * DividendXfr, Misc with fee) get aggregated to ONE row upstream in the
 * register's displayRows pipeline — these renderers render the
 * aggregated facts. Slot 6 splits the line-1 "category" leg from
 * the line-1 "transfer" leg so DividendXfr-style rows can show both
 * sides side-by-side (income category | transfer destination); the
 * fee leg always lives on line 2.
 */

/** The one word each investment action is shown as (ADR-0027's display words).
 *  Every surface that names an action reads it from here: the register's chip and
 *  hint chip, the home lists, a merge candidate, the editor's action picker
 *  (`ACTION_PICKER_ENTRIES`) and a security page's transactions, so no two of them
 *  can name an action differently.
 *  The table is the JSON file the API embeds too, so the register's Action sort,
 *  which the server runs because the register loads in pages, orders by these words.
 *  Typed as keyed by every action, so a new one cannot be added without its word. */
const ACTION_LABEL: Readonly<Record<LedgerInvestmentAction, string>> = investmentActionWords;

/** The word for an action, a derived one (`Xfr`) included. Exported so anything
 *  that names a row's action names it the same way. */
export function formatInvestmentAction(action: string): string {
    const labels: Readonly<Record<string, string | undefined>> = ACTION_LABEL;
    return labels[action] ?? action;
}
const formatAction = formatInvestmentAction;

/** The security as the register's security cell names it: `TICKER · Name`, or the
 *  name alone without a ticker. Null for an action with no security (Xfr, Misc). */
export function securityLabel(txn: InvestmentRow): string | null {
    if (!txn.securityTicker && !txn.securityName) return null;
    return txn.securityTicker
        ? `${txn.securityTicker} · ${txn.securityName ?? ''}`.trim()
        : (txn.securityName ?? '');
}

export const investmentStrategy = {
    renderDateSubLabel(txn: InvestmentRow) {
        // Tax date, same as the bank register: line 2 of the date cell belongs to
        // the date. MD's investment-specific check_number markers (Auto / EXfr /
        // Xfr / numeric) hang off the Action chip instead, where
        // they read as what they are — a qualifier on the action, not on the date.
        const label = taxDateSubLabel(txn);
        return label ? `tax ${label}` : null;
    },

    renderActionSubLabel(txn: InvestmentRow) {
        // MD's investment check_number markers: Auto / EXfr / Xfr, or a plain
        // cheque number. Displaced from the date cell by the tax date.
        return txn.checkNumber ?? null;
    },

    renderSlot4(txn: InvestmentRow, opts?: {
        markUnset?: boolean;
        /** Drawn away from the register (a home list), where no double-click
         *  opens the editor: the hint's title names only what was detected. */
        outsideRegister?: boolean;
    }) {
        // Mig 108 / ADR-0036: read `derivedAction`, not
        // `investmentAction`. For originating-side groups the
        // aggregator already collapsed legs into one row with
        // header.action carried through (derivedAction equals
        // investmentAction in that case). For per-posting TARGET
        // rows (paycheck splits, manual transfers landing in a
        // brokerage cash sleeve) `investmentAction` is NULL but
        // `derivedAction` is `'Xfr'` so the chip renders correctly.
        const action = txn.derivedAction ?? txn.investmentAction;
        if (action) {
            return (
                <Chip variant="default" className="font-sans">
                    {formatAction(action)}
                </Chip>
            );
        }
        // ADR-0031 Phase 3d.2: orchestrator-written classifier hint
        // on a sync-imported row that the user hasn't upgraded yet.
        // Render a distinct chip so the row is visibly "review me to
        // turn this into a real investment transaction." On editor
        // open the action picker pre-fills from this hint.
        if (txn.ingestActionHint) {
            return (
                <Chip
                    variant="warn"
                    className="font-sans"
                    title={`Detected ${formatAction(txn.ingestActionHint)} from sync`
                        + (opts?.outsideRegister ? '' : ' · double-click to review + confirm')}
                >
                    {'↗ '}{formatAction(txn.ingestActionHint)}
                </Chip>
            );
        }
        // ADR-0028 (2026-10-21): on txn and split-parent rows an unset action
        // is still a chip — the same neutral one, reading "—" — so the chip
        // line never collapses and the check# always sits on line 2. Split
        // legs carry no check# line and keep their blank cell.
        if (opts?.markUnset) {
            return (
                <Chip variant="default" className="font-sans" title="No action">
                    —
                </Chip>
            );
        }
        return '';
    },

    renderSlot5(txn: InvestmentRow, accountPaths?: ReadonlyMap<string, string>) {
        // Description = payee (bold) + memo (subtitle). Same shape
        // as bank rows; investment txns rarely populate both
        // distinctly but when they do (e.g. user-added context on
        // an imported MD dividend) both lines show. The memo is the one
        // this register shows — the event's on its own brokerage, with the
        // inherited marker on a row that shows the other side's.
        // Mig 107: leading provenance icon (online / file / manual +
        // merge-winner overlay), matching the bank register.
        return (
            <>
                <span className="flex items-center gap-1.5 truncate font-medium">
                    <ProvenanceIcon
                        origin={txn.origin}
                        providerKey={txn.providerKey}
                        isMergeWinner={txn.isMergeWinner}
                    />
                    <span className="truncate">
                        {txn.payee ?? <span className="text-text-subtle">—</span>}
                    </span>
                </span>
                {renderRowMemo(txn, accountPaths)}
            </>
        );
    },

    renderSlot6(
        txn: InvestmentRow,
        accountPaths: ReadonlyMap<string, string> | undefined,
        /** The ledger's system category ids: their chip is neutral. */
        systemCategoryIds: ReadonlySet<string>,
    ) {
        const isSystem = (id: string | null | undefined) =>
            id !== null && id !== undefined && systemCategoryIds.has(id);
        // Slot 6 — up to three chips across two visual lines:
        //
        //   Line 1: category chip + transfer chip, side-by-side. Both
        //           chips render only when populated; DividendXfr-style rows
        //           with income + transfer destination show BOTH, solo
        //           Dividend / Xfr / Misc-exp show ONE, solo Buy / Sell show
        //           nothing (Holdings sibling is stripped upstream).
        //   Line 2: fee chip — fee posting's counterparty (typically
        //           "Investment Fees"). Only rendered when present.
        //
        // No em-dash placeholders: an empty slot stays visually empty.
        // The chip variant (color) + the Action column already tell the
        // user what each chip means, so a positional placeholder adds
        // noise without information. When the entire cell would be
        // empty (solo Buy/Sell, Holdings stripped), it stays empty.
        //
        // The investment aggregator (slice A1.d) classifies each leg's
        // role and stamps these onto the synthesized row:
        //   - postingRole='income' OR ('fee' AND legIndex=0)
        //         → categoryAccountName
        //   - postingRole='transfer' → transferAccountName
        //   - postingRole='fee' AND legIndex>0
        //         → feeCategoryName + feeAmount
        const hasCategory = !!txn.categoryAccountName;
        const hasTransfer = !!txn.transferAccountName;
        const hasFee = !!txn.feeCategoryName;
        // Tags are header-level (ADR-0009) and apply to every action, so they
        // can be the ONLY thing in this cell — a tagged plain buy has no
        // category, transfer or fee chip. They count toward the early-out.
        const hasTags = txn.tags.length > 0;
        if (!hasCategory && !hasTransfer && !hasFee && !hasTags) return null;

        const categoryVariant = hasCategory
            ? categoryChipVariant(
                  txn.categoryAccountName!,
                  txn.categoryAccountType ?? null,
                  txn.categoryAccountId ?? null,
                  isSystem(txn.categoryAccountId),
              )
            : 'default';
        const transferVariant = hasTransfer
            ? categoryChipVariant(
                  txn.transferAccountName!,
                  txn.transferAccountType ?? null,
                  txn.transferAccountId ?? null,
                  isSystem(txn.transferAccountId),
              )
            : 'default';
        const feeVariant = hasFee
            ? categoryChipVariant(txn.feeCategoryName!, 'category', null, isSystem(txn.feeCategoryId))
            : 'default';

        return (
            <>
                {(hasCategory || hasTransfer) ? (
                    <span className="flex min-w-0 items-center gap-2">
                        {hasCategory ? (
                            <Chip
                                variant={categoryVariant}
                                className="min-w-0 max-w-full truncate"
                                title={displayAccountPath(accountPaths, txn.categoryAccountId, txn.categoryAccountName) ?? undefined}
                            >
                                <span className="truncate">
                                    {displayAccountPath(accountPaths, txn.categoryAccountId, txn.categoryAccountName)}
                                </span>
                            </Chip>
                        ) : null}
                        {/* An Xfr, BuyXfr, SellXfr or DividendXfr between currencies
                            shows the transfer account's own amount after its chip, as
                            the bank register does (ADR-0114 D14, ADR-0028 Slot 6). */}
                        {hasTransfer ? renderAccountChip({
                            variant: transferVariant,
                            label: displayAccountPath(accountPaths, txn.transferAccountId, txn.transferAccountName),
                            chipClassName: 'min-w-0 max-w-full truncate',
                            otherAmount: txn.transferAmount,
                            otherCurrency: txn.transferCurrencyCode,
                            amount: txn.amount,
                        }) : null}
                    </span>
                ) : null}
                {hasFee ? (
                    <Chip
                        variant={feeVariant}
                        className="max-w-full truncate"
                        title={displayAccountPath(accountPaths, txn.feeCategoryId, txn.feeCategoryName) ?? undefined}
                    >
                        <span className="truncate">{displayAccountPath(accountPaths, txn.feeCategoryId, txn.feeCategoryName)}</span>
                    </Chip>
                ) : null}
                {/* Tags wrap beneath the account chips, in the same cell and the
                    same order as the bank register's category · tags column —
                    one set per event, since the aggregator collapses an
                    investment event's legs into a single row. */}
                {renderRowTags(txn, accountPaths)}
            </>
        );
    },

    renderSlot7(
        txn: InvestmentRow,
        /** The register's account currency: the price the trade was made at
         *  is the brokerage's, as its amount is. */
        currency: string,
    ) {
        // Security cell. Line 1: `TICKER · Name` (or just Name when
        // ticker is null). Line 2: `N sh @ <price>` when the row
        // carries qty + price, in the account's currency. Empty cell
        // for actions with no security (Xfr / Misc).
        const label = securityLabel(txn);
        if (label === null) return null;

        const hasQtyPrice =
            txn.quantity !== null &&
            txn.quantity !== 0 &&
            txn.unitPrice !== null &&
            txn.unitPrice !== 0;

        return (
            <>
                <span className="block truncate font-medium" title={label}>
                    {label}
                </span>
                {hasQtyPrice ? (
                    <span className="block truncate text-[0.6875rem] text-text-muted">
                        {formatShares(Math.abs(txn.quantity!))} sh @ {formatPrice(txn.unitPrice!, currency)}
                    </span>
                ) : null}
            </>
        );
    },

    renderAmountSubtitle(
        txn: InvestmentRow,
        /** The register's account currency, the one the Amount above is in. */
        currency: string,
    ) {
        // Two possible subtitles share this one line, and only one can win.
        //
        // FEE. The investmentAggregator (slice A1.d) sums the fee-leg amounts
        // across all postings under one header and stamps the positive total
        // onto the synthesized row's `feeAmount`.
        //
        // SETTLED. `amount` is ADR-0028's net of this account's legs, which is
        // structurally ZERO for a cash-neutral event — a reinvestment, an
        // in-kind transfer, a sale a fee consumes. Every reinvestment is in
        // that class, and without this line every one of them
        // renders a zero amount with nothing to say how big the event was, while the editor behind the
        // same row shows the trade value (ADR-0073 D1 calls that the Amount;
        // ADR-0028 calls the net the Amount — the two ADRs disagree, and this
        // line is where a reader meets the consequence).
        //
        // Shown only when the Amount cell is uninformative, i.e. exactly zero.
        // On a row whose net is real, the net IS the answer and a second
        // figure beneath it is noise.
        const fee = txn.feeAmount ?? null;
        const settled = txn.settledAmount ?? null;

        // The fee wins the line whenever there is one, because it is the one the
        // Amount does not already imply — and on a fee-consumed sale the two
        // are the SAME NUMBER by construction (proceeds exactly cancelled by
        // the fee), so showing both would print it twice under a zero. The
        // shape can occur on sell, sellx AND buy; because the settled line is
        // reached only on a row with no fee, no comparison of the two figures
        // is needed, at any currency's places (ADR-0114 D15).
        if (fee !== null) {
            return (
                <span className="block text-right text-[0.6875rem] text-text-muted">
                    fee {formatCurrency(fee, currency)}
                </span>
            );
        }
        if (txn.amount === 0 && settled !== null && settled !== 0) {
            return (
                <span className="block text-right text-[0.6875rem] text-text-muted">
                    settled {formatCurrency(settled, currency)}
                </span>
            );
        }
        return null;
    },
};
