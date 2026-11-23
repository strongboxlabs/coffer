import type { ReactNode } from 'react';

import { splitParentNotes } from '@/lib/annotations/splitHeader';
import type { InvestmentRow, LedgerEntry } from '@/lib/types';

import { renderBankSlot6 } from '../register/strategies/bankStrategy';
import { investmentStrategy, securityLabel } from '../register/strategies/investmentStrategy';
import { renderRowTags } from '../register/strategies/rowNotes';
import { Mixed, Money } from './Money';
import { shownAmount } from './rows';

/**
 * The category cell of an entry in a home list, as its own register draws it: the
 * bank register's chip and tags; a split's summary as its collapsed row states it,
 * without the toggle, since nothing expands here; and for an investment event its
 * action chip (on a feed row not yet confirmed, the register's hint chip; with no
 * action and no hint, the register's neutral "—" chip), the security, then the
 * category or transfer chips it has. The ledger's Uncategorized is
 * always the neutral chip. The register's shares-at-price line is left out, so the
 * row keeps to one line.
 */
export function EntryCategory({ item, paths, systemIds }: {
    item: LedgerEntry;
    paths: ReadonlyMap<string, string>;
    /** The ledger's system category ids (its Uncategorized): a neutral chip. */
    systemIds: ReadonlySet<string>;
}): ReactNode {
    const { entry } = item;
    if (entry.kind === 'group') {
        const parent = splitParentNotes(entry.legs[0]!, entry.legs);
        return (
            <>
                <span className="text-[0.6875rem] font-medium text-text-muted">{entry.legs.length} splits</span>
                {renderRowTags(parent, paths)}
            </>
        );
    }
    if (entry.txn.kind === 'investment') {
        return <InvestmentCategory txn={entry.txn} paths={paths} systemIds={systemIds} />;
    }
    // No subtree rollup here, so no own-category chip: the row is its own account's.
    return renderBankSlot6(entry.txn, paths, null, systemIds);
}

function InvestmentCategory({ txn, paths, systemIds }: {
    txn: InvestmentRow;
    paths: ReadonlyMap<string, string>;
    systemIds: ReadonlySet<string>;
}) {
    const security = securityLabel(txn);
    return (
        <span className="flex min-w-0 max-w-full flex-wrap items-center gap-x-1.5 gap-y-1">
            {investmentStrategy.renderSlot4(txn, { markUnset: true, outsideRegister: true })}
            {security ? (
                <span className="min-w-0 max-w-full truncate text-xs font-medium" title={security}>{security}</span>
            ) : null}
            {investmentStrategy.renderSlot6(txn, paths, systemIds)}
        </span>
    );
}

/**
 * The amount cell of an entry in a home list: the amount the entry shows on its
 * account, in that account's currency, red when negative. Under an investment
 * event's amount of exactly zero (a reinvestment, an in-kind move), the register's
 * second line says how big the event was: "settled …", or "fee …".
 */
export function EntryAmount({ item }: { item: LedgerEntry }) {
    const { entry } = item;
    const investment = entry.kind === 'txn' && entry.txn.kind === 'investment' ? entry.txn : null;
    const amount = shownAmount(entry);
    return (
        <>
            <span className="block">
                {amount === null
                    ? <Mixed />
                    : <Money amount={amount} currency={item.currencyCode} tone="news" />}
            </span>
            {investment !== null && investment.amount === 0
                ? investmentStrategy.renderAmountSubtitle(investment, item.currencyCode)
                : null}
        </>
    );
}
