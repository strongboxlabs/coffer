// The Review marker (ADR-0114 D15, boards RV-1a and RV-2): an imported row awaiting review
// says why the bank's figure put it there. A row whose amount was rounded to its account
// currency's places says what the bank sent; a row a promote-on-clear held because it does
// not hold the bank's cleared figure (a split whose lines add up to another total, or the
// row a cleared transaction was merged into) says what the bank cleared. The To review
// widget draws it as a muted line under the amount, and the editor Review… opens gives it
// to Amount as the field's description. No other surface shows it.

import { amountText, formatAsSent } from '@/lib/money';
import type { RegisterRow } from '@/lib/types';

type NoteRow = Pick<
    RegisterRow,
    | 'needsReview' | 'roundedAtImport' | 'originalAmount' | 'reviewClearedAmount' | 'reviewClearedByMatch'
    | 'currencyCode' | 'isReadOnly'
>;

/** Text the API sent as an exact figure: present and not blank. */
function figureOf(text: string | null | undefined): string | null {
    return text === null || text === undefined || text.trim() === '' ? null : text;
}

/**
 * "Rounded from <bank figure> as sent by the bank", while the row needs review, the
 * import (or a promote-on-clear) rounded it, and its originals hold the bank's figure;
 * null otherwise. The figure is the API's exact text through `formatAsSent`: every
 * digit, a trailing zero and the sign kept, and no currency symbol, so it is not read as
 * an amount in the account's currency.
 */
export function roundedNote(row: Pick<RegisterRow, 'needsReview' | 'roundedAtImport' | 'originalAmount'>): string | null {
    if (!row.needsReview || row.roundedAtImport !== true) return null;
    const figure = figureOf(row.originalAmount);
    if (figure === null) return null;
    return `Rounded from ${formatAsSent(figure)} as sent by the bank`;
}

/**
 * The row's Review marker while it needs review. A promote-on-clear that held the row
 * because it does not hold the bank's cleared figure says so, in place of a rounded note
 * left from the row's own import: "The bank cleared a matched transaction at <figure>"
 * on the row a cleared transaction was merged into, and "The bank cleared this at
 * <figure>; your lines add up to <total>" on a split held for its own, where `linesTotal`
 * is the entry's amount on its account (no total when it is null). Otherwise the rounded
 * note. Both figures are shown as the bank's is, with no currency symbol, the total at
 * its currency's places.
 *
 * The bank's figure was compared with the split's lines on the account the transaction
 * was entered from, the one side that edits it. The header's review flag lists every
 * account it touches, so a line on another account (a read-only row there) says only what
 * the bank cleared, as the rounded note gives the bank's figure on every side: its own
 * amount is not the total the bank's figure was compared with.
 */
export function reviewNote(row: NoteRow, linesTotal: number | null): string | null {
    if (!row.needsReview) return null;
    const cleared = figureOf(row.reviewClearedAmount);
    if (cleared === null) return roundedNote(row);
    const figure = formatAsSent(cleared);
    if (row.reviewClearedByMatch === true) return `The bank cleared a matched transaction at ${figure}`;
    if (linesTotal === null || row.isReadOnly) return `The bank cleared this at ${figure}`;
    return `The bank cleared this at ${figure}; your lines add up to ${formatAsSent(amountText(linesTotal, row.currencyCode))}`;
}
