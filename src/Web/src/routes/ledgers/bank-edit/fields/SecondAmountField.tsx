/**
 * The bank editor's second amount (ADR-0114 D14): the other account's own figure on a
 * posting between real accounts in different currencies, right of Amount and built like
 * it, with the rate line under it. Two renderings share one hook: the one-posting
 * editor's (a caption over the field, on a grid slot the shell names) and a split line's
 * (the field alone in its column, the line on a second row of the leg).
 *
 * Mounted only while the posting has a second amount, so its derived-rate request lives
 * exactly as long as the field does. Holds no draft state: the figure, and whether it is
 * typed, are the posting's (`otherAmount`, `otherFixed`), written back through `onPatch`.
 */

import { useCallback, useId } from 'react';

import { MoneyInput } from '@/components/ui/MoneyInput';
import { cn } from '@/lib/cn';
import {
    secondAmountCaption,
    secondAmountDisplay,
    secondAmountMagnitude,
    secondAmountName,
    type SecondAmountSide,
} from '@/lib/secondAmount';
import { useSecondAmount } from '@/lib/useSecondAmount';

import type { PostingDraft } from '../postingDraft';

/** The field's look: Amount's, red while it shows the money the other account sent. */
const INPUT = 'h-control-28px w-full rounded border bg-surface px-2 text-right font-mono text-xs tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent aria-disabled:cursor-not-allowed aria-disabled:opacity-50';
/** The rate line's look. */
const RATE_LINE = 'text-right text-[0.6875rem] tabular-nums text-text-subtle';

interface SecondAmountProps {
    ledgerId: string;
    posting: PostingDraft;
    side: SecondAmountSide;
    /** Amount's currency, the source posting's. */
    currency: string;
    /** The entry's date, `yyyy-MM-dd`. */
    date: string;
    pending: boolean;
    onPatch: (fields: Partial<PostingDraft>) => void;
}

function useBankSecondAmount({ ledgerId, posting, side, currency, date, onPatch }: SecondAmountProps) {
    const amount = posting.amount.trim().length === 0 ? null : Number(posting.amount);
    const onPrefill = useCallback((otherAmount: string) => onPatch({ otherAmount }), [onPatch]);
    const line = useSecondAmount({
        ledgerId,
        fromCurrency: currency,
        toCurrency: side.currency,
        date,
        amount: amount !== null && Number.isFinite(amount) ? amount : null,
        value: posting.otherAmount ?? '',
        fixed: posting.otherFixed === true,
        onPrefill,
    });
    const display = secondAmountDisplay(posting.otherAmount ?? '', side.direction);
    // A typed minus is ignored: the sign follows from Amount (ADR-0114 D13). Typing fixes
    // the figure, so it no longer follows Amount and the date.
    const onValueChange = (text: string) =>
        onPatch({ otherAmount: secondAmountMagnitude(text), otherFixed: true });
    const tone = display.startsWith('-') ? 'text-state-danger' : 'text-text';
    return { line, display, onValueChange, tone };
}

/** The one-posting editor's second amount: its caption and field, and the rate line. */
export function OneLineSecondAmount(props: SecondAmountProps & {
    /** The grid slot of the caption and field. */
    fieldClassName: string;
    /** The grid slot of the rate line. */
    lineClassName: string;
    /** The narrow dialog's touch size (components/ui/fieldSize), '' elsewhere. */
    fieldSize: string;
    pendingLook: string;
}) {
    const rateId = useId();
    const { line, display, onValueChange, tone } = useBankSecondAmount(props);
    const name = secondAmountName(props.side);
    return (
        <>
            <label className={cn(props.fieldClassName, 'flex min-w-0 flex-col gap-1')} title={name}>
                <span className="text-right text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                    {secondAmountCaption(props.side.direction)}
                </span>
                <MoneyInput
                    currency={props.side.currency}
                    value={display}
                    pending={props.pending}
                    aria-label={name}
                    aria-describedby={rateId}
                    onValueChange={onValueChange}
                    className={cn(INPUT, 'border-border', tone, props.pendingLook, props.fieldSize)}
                />
            </label>
            <p id={rateId} className={cn(props.lineClassName, RATE_LINE)}>{line}</p>
        </>
    );
}

/** A split line's second amount: the field in its column, and the rate line as a second
 *  row of the leg, ending under the field. */
export function SplitSecondAmount(props: SecondAmountProps & {
    /** The line's 1-based number, which its accessible name starts with. */
    n: number;
    /** The field's id, which the issue strip's link focuses. */
    id: string;
    invalid: boolean;
}) {
    const rateId = useId();
    const { line, display, onValueChange, tone } = useBankSecondAmount(props);
    const name = secondAmountName(props.side);
    return (
        <>
            <MoneyInput
                id={props.id}
                currency={props.side.currency}
                value={display}
                pending={props.pending}
                aria-label={`Posting ${props.n}: ${name}`}
                aria-describedby={rateId}
                aria-invalid={props.invalid || undefined}
                title={name}
                onValueChange={onValueChange}
                className={cn(INPUT, props.invalid ? 'border-state-danger' : 'border-border', tone)}
            />
            {/* Placed on the leg's second row by its own row, so the row menu after the
                field still takes the first row's last column. */}
            <p id={rateId} className={cn('row-start-2 col-start-3 col-span-3 pb-0.5', RATE_LINE)}>{line}</p>
        </>
    );
}
