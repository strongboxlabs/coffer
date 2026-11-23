import { useCallback, useId } from 'react';

import { cn } from '@/lib/cn';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { MoneyInput } from '@/components/ui/MoneyInput';
import { useFieldSizeClass } from '@/components/ui/fieldSize';
import { amountText } from '@/lib/money';
import {
    secondAmountCaption,
    secondAmountName,
    signedSecondAmount,
    type SecondAmountSide,
} from '@/lib/secondAmount';
import { useSecondAmount } from '@/lib/useSecondAmount';

import { parseMoneyText } from './moneyText';

interface TransferAmountFieldProps {
    ledgerId: string;
    /** The transfer account, its currency and which way the money moves for it. */
    side: SecondAmountSide;
    /** The brokerage's currency, Amount's. */
    currency: string;
    /** The entry's date, `yyyy-MM-dd`. */
    date: string;
    /** The figure the second amount pairs with: Amount, the trade amount on BuyXfr,
     *  SellXfr and DividendXfr, never the fee (ADR-0114 D14). */
    amount: number | null;
    /** The second amount's magnitude; null while it is empty. */
    value: number | null;
    /** Typed in, or seeded from a stored figure: never refilled from a rate. */
    fixed: boolean;
    onChange: (magnitude: number | null, fixed: boolean) => void;
    error?: string | null;
    /** The save is in flight: the field keeps focus and is read-only (pending.ts). */
    pending?: boolean;
}

/**
 * The investment editor's second amount (ADR-0114 D14): the transfer account's own figure
 * on an Xfr, BuyXfr, SellXfr or DividendXfr to a real account in another currency, right
 * of Amount and built like the investment editor's Amount, with no colour, as no amount in
 * this editor has one. Its rate line sits in the field's hint slot, and its error in
 * Amount's error style. Mounted only while the field shows, so its derived-rate request
 * lives exactly as long as it does.
 *
 * It holds a magnitude: the sign follows from the action and Amount, so a typed minus is
 * ignored, and it shows the transfer account's figure with that sign.
 */
export function TransferAmountField({
    ledgerId, side, currency, date, amount, value, fixed, onChange, error, pending,
}: TransferAmountFieldProps) {
    const inputId = useId();
    const lineId = useId();
    const sizeClass = useFieldSizeClass();
    const name = secondAmountName(side);
    const onPrefill = useCallback(
        (magnitude: string) => onChange(magnitude.length === 0 ? null : Number(magnitude), false),
        [onChange],
    );
    const line = useSecondAmount({
        ledgerId,
        fromCurrency: currency,
        toCurrency: side.currency,
        date,
        amount,
        value: value === null ? '' : amountText(value, side.currency),
        fixed,
        onPrefill,
    });

    return (
        <div className="flex min-w-0 flex-col gap-1 text-xs" title={name}>
            <FieldLabel htmlFor={inputId}>{secondAmountCaption(side.direction)}</FieldLabel>
            <MoneyInput
                id={inputId}
                currency={side.currency}
                value={value === null ? '' : signedSecondAmount(value, side.direction)}
                pending={pending}
                aria-label={name}
                onValueChange={(text) => {
                    const n = parseMoneyText(text);
                    onChange(n === null ? null : Math.abs(n), true);
                }}
                className={cn(
                    'h-control-28px w-full rounded border bg-surface px-2 text-right font-mono text-xs tabular-nums',
                    sizeClass,
                    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                    'aria-disabled:cursor-not-allowed aria-disabled:opacity-50',
                    error ? 'border-state-danger' : 'border-border',
                )}
                aria-invalid={!!error}
                aria-describedby={error ? `${inputId}-error ${lineId}` : lineId}
            />
            {error ? (
                <span id={`${inputId}-error`} className="text-[0.6875rem] leading-tight text-state-danger">
                    {error}
                </span>
            ) : null}
            {/* The rate line is always shown under the second amount (ADR-0114 D14), the
                missing rate that left it empty included. */}
            <span id={lineId} className="text-[0.625rem] leading-tight tabular-nums text-text-subtle">
                {line}
            </span>
        </div>
    );
}
