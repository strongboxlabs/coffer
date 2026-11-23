import { cn } from '@/lib/cn';
import { MIXED_LABEL, MIXED_TITLE, NO_RATE_LABEL, rateUnavailableTitle } from '@/lib/conversion';
import { atDisplayPrecision, formatCurrency, formatSignedAmount } from '@/lib/money';
import type { RateUnavailable } from '@/lib/types';

/**
 * A money figure as the Overview and the Budget page show one (ADR-0023 §H, as
 * amended).
 *
 * - `news`: a negative that is news (a transaction, a change, a net below zero) is
 *   red; a positive is default text. Never green.
 * - `fixed`: a total whose sign is fixed by what it is (expenses, liabilities, a
 *   balance) is default text whatever its sign.
 * - `muted`: an unposted amount, which is not news yet.
 *
 * The sign, and so the colour, is the printed figure's: an amount is taken at the
 * precision it prints, so a residue smaller than the last digit is a plain zero,
 * never a red one.
 *
 * A figure that combines accounts is converted into the reporting currency (ADR-0114
 * D7), so none here reads "mixed" any more. A converted figure that is null for want of a
 * rate (ADR-0114 D9) reads "no rate",
 * naming each missing pair and day on hover: pass the figure's `rateUnavailable`. A
 * null figure with none missing is a dash, as before.
 *
 * A figure whose currency is not known (`currency` null) reads as a dash, as one with
 * no amount does: it is never printed in a currency it may not be in.
 */
export function Money({ amount, currency, tone, rateUnavailable, className }: {
    amount: number | null | undefined;
    currency: string | null;
    tone: 'news' | 'fixed' | 'muted';
    rateUnavailable?: readonly RateUnavailable[];
    className?: string;
}) {
    if ((amount === null || amount === undefined) && rateUnavailable !== undefined && rateUnavailable.length > 0) {
        return <NoRate missing={rateUnavailable} className={className} />;
    }
    if (amount === null || amount === undefined || currency === null) return <span className={className}>—</span>;
    const shown = atDisplayPrecision(amount, currency);
    const toneClass = tone === 'muted' ? 'text-text-muted'
        : tone === 'news' && shown < 0 ? 'text-state-danger'
        : 'text-text';
    return (
        <span className={cn('font-mono font-semibold tabular-nums', toneClass, className)}>
            {tone === 'news' ? formatSignedAmount(shown, currency) : formatCurrency(shown, currency)}
        </span>
    );
}

/**
 * The marker for a total whose figures are in more than one currency and that no rule
 * converts: a category's split paid from accounts in different currencies (ADR-0114
 * D7). It says so on hover. It keeps the subtle colour whatever colour the caller
 * passes for the figure. Nothing is derived from a figure that reads this.
 */
export function Mixed({ className }: { className?: string }) {
    return (
        <span className={cn('font-mono tabular-nums', className, 'text-text-subtle')} title={MIXED_TITLE}>
            {MIXED_LABEL}
        </span>
    );
}

/**
 * The marker for a converted figure that needs a rate the ledger does not hold
 * (ADR-0114 D9): "no rate", with each missing pair and day on hover. It keeps the
 * subtle colour whatever colour the caller passes for the figure.
 * Nothing is derived from a figure that reads this.
 */
export function NoRate({ missing, className }: { missing: readonly RateUnavailable[]; className?: string }) {
    return (
        <span className={cn('font-mono tabular-nums', className, 'text-text-subtle')} title={rateUnavailableTitle(missing)}>
            {NO_RATE_LABEL}
        </span>
    );
}
