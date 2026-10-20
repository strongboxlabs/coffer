/**
 * Grid template for the investment register surface (ADR-0028).
 * Nine columns:
 *   1 checkbox   2 status      3 date+check#    4 action chip
 *   5 description (payee + memo)
 *   6 category | transfer + fee
 *   7 security + shares @ price
 *   8 signed amount + fee subtitle
 *   9 cash balance
 *
 * The leading two tracks (checkbox 1.75rem, status 2.25rem) match
 * `BANK_COLS` in order + width so the shared row-lead
 * (shell/RegisterRowLead — checkbox-first) aligns identically across
 * both registers. Both the column header strip and every
 * <c>InvestmentRow</c> share this string so the columns align.
 */
export const INVESTMENT_REGISTER_COLS =
    '1.75rem 2.25rem 6.5rem 6rem minmax(5rem,1fr) 20rem minmax(5rem,1fr) 7rem 6.5rem';

/**
 * Fluid single-column template for embedding the investment editor in a
 * dialog/form (the reminder editor + occurrence modal), where the register's
 * fixed ~64rem width would overflow the ~960px dialog. InvestmentTxnRowEdit's
 * field rows are all `col-span-full` — they never use the register columns for
 * placement, so the register template's only effect there is width. A single
 * fluid `minmax(0,1fr)` column lets the editor fill the dialog and its flex
 * rows wrap.
 */
export const INVESTMENT_FORM_COLS = 'minmax(0, 1fr)';
