// The home lists' shared row pieces: the text-action pattern every row with
// actions uses, and the columns of an entry row (Recent transactions and To
// review), so the two lists draw an entry the same way.

import { groupAmount } from '@/lib/splitCollapse';
import type { LedgerEntry, RegisterEntry, RegisterRow } from '@/lib/types';

/** Visible text actions in a row: half strength until the row is hovered or focused,
 *  and always full on a touch screen, where there is no hover, at a 44px target. */
export const ROW_ACTION = 'opacity-50 group-hover:opacity-100 group-focus-within:opacity-100 pointer-coarse:h-control-44px pointer-coarse:opacity-100';

// An entry row is one line from @xs/panel up: the lead (a status or provenance mark
// and the date), account, payee, category, amount. Below it, two, as Bills' rows go:
// the lead, payee and amount, then the account and category under the payee, so the
// payee keeps its width. The order classes move the pieces between the two layouts;
// the DOM order is the reading order. The category column takes an investment
// event's action, security and chips on one line where the panel is wide enough to
// give it the room (@2xl/panel); narrower, it wraps inside its column.
//
// A row that also carries text actions (To review's Review… and its menu) keeps the
// category at 128px at every width: at 224px the actions leave the payee too narrow
// to read on a desktop panel, and the payee is what identifies a row to review. A
// category that does not fit is cut, with its full name in the chip's title.
export const ENTRY_LEAD = 'flex w-fixed-80px shrink-0 items-center gap-3';
export const ENTRY_PAYEE = 'order-1 min-w-0 flex-1 truncate @xs/panel:order-2';
export const ENTRY_BREAK = 'order-3 h-0 basis-full @xs/panel:hidden';
export const ENTRY_INDENT = 'order-4 w-fixed-80px shrink-0 @xs/panel:hidden';
export const ENTRY_ACCOUNT = 'order-4 min-w-0 truncate text-xs text-text-muted @xs/panel:order-1 @xs/panel:w-fixed-128px @xs/panel:flex-none';
const ENTRY_CATEGORY_BASE = 'order-5 flex min-w-0 flex-col items-start gap-1 @xs/panel:order-3 @xs/panel:w-fixed-128px @xs/panel:flex-none';
export const ENTRY_CATEGORY = `${ENTRY_CATEGORY_BASE} @2xl/panel:w-fixed-224px`;
export const ENTRY_CATEGORY_BESIDE_ACTIONS = ENTRY_CATEGORY_BASE;
export const ENTRY_AMOUNT = 'order-2 shrink-0 text-right @xs/panel:order-4 @xs/panel:w-fixed-96px';

/** The row an entry is drawn from: the row itself, or a split's first line. A
 *  split's lines share the date, payee, status and review flag. */
export function shownRow(entry: RegisterEntry): RegisterRow {
    return entry.kind === 'txn' ? entry.txn : entry.legs[0]!;
}

/** The amount the entry shows on its account: a split's net, or the row's own. Null
 *  for a split whose lines are in more than one currency, which only a category's can
 *  be, and the home lists hold no category's entries (ADR-0114 D7). */
export function shownAmount(entry: RegisterEntry): number | null {
    return entry.kind === 'txn' ? entry.txn.amount : groupAmount(entry.legs);
}

/** An entry's own key within its account's register, with the account: the same
 *  header is an entry on each account a transfer touches. */
export function entryKey({ accountId, entry }: LedgerEntry): string {
    return `${accountId}:${entry.kind === 'txn' ? entry.txn.id : entry.groupId}`;
}
