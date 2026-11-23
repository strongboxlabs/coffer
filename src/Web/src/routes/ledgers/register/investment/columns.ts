/**
 * Grid template for the investment register surface (ADR-0028).
 * Nine columns:
 *   1 checkbox   2 status      3 date + tax date    4 action chip + check#
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
 *
 * The action track is 7rem, not the 6rem the chips alone need: its header
 * reads "Action · Check #" and truncates at 6rem.
 */
export const INVESTMENT_REGISTER_COLS =
    '1.75rem 2.25rem 6.5rem 7rem minmax(5rem,1fr) 20rem minmax(5rem,1fr) 7rem 6.5rem';
