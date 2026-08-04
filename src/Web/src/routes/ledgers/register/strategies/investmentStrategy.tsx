import type { InvestmentRow } from '@/lib/types';
import { Chip } from '@/components/ui/Chip';
import { categoryChipVariant } from '@/lib/categoryChip';
import { displayAccountPath } from '@/lib/accountPath';
import { ProvenanceIcon } from '@/components/register/ProvenanceIcon';
import { formatShares, formatPrice, formatCurrency } from '@/lib/money';

/**
 * Investment-register cell renderers (slice A1.c / A1.d). Consumed
 * directly by <c>InvestmentRow</c> (the investment register renders
 * its own row component rather than going through a shared dispatch).
 *
 * Slot layout (9-column grid — see `../investment/columns.ts`):
 *   1 status     2 checkbox    3 date + check#    4 Action chip
 *   5 Description (payee + memo)
 *   6 Category | Transfer (line 1)  ·  Fee category (line 2)
 *   7 Security · Shares @ Price            ← new vs bank
 *   8 Amount + fee subtitle
 *   9 Cash balance
 *
 * Multi-posting headers (Buy+Fee, DivReinvest, BuyXfr / SellXfr /
 * DivXfr, Misc with fee) get aggregated to ONE row upstream in the
 * register's displayRows pipeline — these renderers render the
 * aggregated facts. Slot 6 splits the line-1 "category" leg from
 * the line-1 "transfer" leg so DivXfr-style rows can show both
 * sides side-by-side (income category | transfer destination); the
 * fee leg always lives on line 2.
 */

const ACTION_LABEL: Record<string, string> = {
    buy: 'Buy',
    buyx: 'BuyXfr',
    sell: 'Sell',
    sellx: 'SellXfr',
    dividend_cash: 'Div',
    dividend_reinvest: 'DivReinvest',
    divx: 'DivXfr',
    transfer: 'Xfr',
    misc: 'Misc',
};

function formatAction(action: string): string {
    return ACTION_LABEL[action] ?? action;
}

export const investmentStrategy = {
    renderDateSubLabel(txn: InvestmentRow) {
        // Investment rows surface MD's investment-specific check_number
        // marker values (Auto / EXfr / Xfr / numeric) on line 2 of the
        // date cell.
        return txn.checkNumber ?? null;
    },

    renderSlot4(txn: InvestmentRow) {
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
                    title={`Detected ${formatAction(txn.ingestActionHint)} from sync · double-click to review + confirm`}
                >
                    {'↗ '}{formatAction(txn.ingestActionHint)}
                </Chip>
            );
        }
        return '';
    },

    renderSlot5(txn: InvestmentRow) {
        // Description = payee (bold) + memo (subtitle). Same shape
        // as bank rows; investment txns rarely populate both
        // distinctly but when they do (e.g. user-added context on
        // an imported MD dividend) both lines show.
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
                {txn.memo ? (
                    <span className="block truncate text-[0.6875rem] text-text-muted">
                        {txn.memo}
                    </span>
                ) : null}
            </>
        );
    },

    renderSlot6(
        txn: InvestmentRow,
        accountPaths?: ReadonlyMap<string, string>,
    ) {
        // Slot 6 — up to three chips across two visual lines:
        //
        //   Line 1: category chip + transfer chip, side-by-side. Both
        //           chips render only when populated; DivXfr-style rows
        //           with income + transfer destination show BOTH, solo
        //           Div / Xfr / Misc-exp show ONE, solo Buy / Sell show
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
        if (!hasCategory && !hasTransfer && !hasFee) return null;

        const categoryVariant = hasCategory
            ? categoryChipVariant(
                  txn.categoryAccountName!,
                  txn.categoryAccountType ?? null,
                  txn.categoryAccountId ?? null,
              )
            : 'default';
        const transferVariant = hasTransfer
            ? categoryChipVariant(
                  txn.transferAccountName!,
                  txn.transferAccountType ?? null,
                  txn.transferAccountId ?? null,
              )
            : 'default';
        const feeVariant = hasFee
            ? categoryChipVariant(txn.feeCategoryName!, 'category', null)
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
                        {hasTransfer ? (
                            <Chip
                                variant={transferVariant}
                                className="min-w-0 max-w-full truncate"
                                title={displayAccountPath(accountPaths, txn.transferAccountId, txn.transferAccountName) ?? undefined}
                            >
                                <span className="truncate">
                                    {displayAccountPath(accountPaths, txn.transferAccountId, txn.transferAccountName)}
                                </span>
                            </Chip>
                        ) : null}
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
            </>
        );
    },

    renderSlot7(txn: InvestmentRow) {
        // Security cell. Line 1: `TICKER · Name` (or just Name when
        // ticker is null). Line 2: `N sh @ $X.XX` when the row
        // carries qty + price. Empty cell for actions with no
        // security (Xfr / Misc).
        if (!txn.securityTicker && !txn.securityName) return null;

        const label = txn.securityTicker
            ? `${txn.securityTicker} · ${txn.securityName ?? ''}`.trim()
            : (txn.securityName ?? '');

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
                        {formatShares(Math.abs(txn.quantity!))} sh @ {formatPrice(txn.unitPrice!, 'USD')}
                    </span>
                ) : null}
            </>
        );
    },

    renderAmountSubtitle(txn: InvestmentRow) {
        // Fee amount subtitle. The investmentAggregator (slice A1.d)
        // sums the fee-leg amounts across all postings under one
        // header and stamps the positive total onto the synthesized
        // row's `feeAmount`. Render `fee $X.XX` beneath the Amount
        // when set; skip the subtitle (null) on rows with no fee
        // leg (single-posting Buy/Sell, Div, etc.) so the column
        // scans cleanly.
        if (txn.feeAmount === null || txn.feeAmount === undefined) return null;
        return (
            <span className="block text-right text-[0.6875rem] text-text-muted">
                fee {formatCurrency(txn.feeAmount, 'USD')}
            </span>
        );
    },
};
