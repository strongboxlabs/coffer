import { forwardRef, useRef, type CSSProperties, type InputHTMLAttributes } from 'react';

import { cn } from '@/lib/cn';
import {
    amountInputMode, amountPlaceholder, amountStep, amountText, currencyAffix, roundToMinorUnits,
} from '@/lib/money';

import { pendingTextProps } from './pending';

type NativeInputProps = Omit<
    InputHTMLAttributes<HTMLInputElement>,
    'type' | 'value' | 'defaultValue' | 'onChange' | 'step' | 'inputMode' | 'placeholder' | 'children'
>;

export interface MoneyInputProps extends NativeInputProps {
    /**
     * The currency the figure is in. Null while it is not known yet (the accounts are
     * still loading): the input then shows no symbol, takes any step and rounds nothing,
     * never a default (ADR-0114 D14).
     */
    currency: string | null;
    /**
     * The field's text, as typed or as seeded (`amountText` seeds a stored figure). A
     * caller that holds the figure as a number passes the number, so a typed trailing
     * zero is not overwritten while the text still equals it.
     */
    value: string | number;
    onValueChange: (text: string) => void;
    /**
     * A price: it shows its currency's symbol but is not money (ADR-0114 D15), so it
     * takes any step, a decimal keypad and no rounding on blur.
     */
    price?: boolean;
    /** Text for the empty field in place of the currency's zero, for a field whose
     *  emptiness says something of its own. */
    placeholder?: string;
    /** The save is in flight: the field keeps focus and is read-only (pending.ts). */
    pending?: boolean;
    /** Classes for the wrapper that positions the symbol, to place the field in its track. */
    wrapperClassName?: string;
    /** The locale the symbol is placed for; the browser's when omitted. */
    locale?: string;
    /**
     * The width of a fixed-width field before its symbol, as a CSS length. The field
     * is drawn that much wider again as the symbol's padding, so the symbol takes no
     * room from the figure. Omitted, the caller's classes size the field.
     */
    figureWidth?: string;
}

/**
 * A signed money input that shows its currency's symbol inside it, muted and not typed,
 * where the currency formatter puts it, before or after the figure (ADR-0114 D14). Its
 * step, placeholder and keypad follow the currency's places, and a figure the user typed
 * is rounded to them on blur, half away from zero (ADR-0114 D15). A figure it was seeded
 * with is never rounded by focusing and leaving the field: a stored amount that has more
 * places than its currency is kept, and an edit that leaves it as it was passes. A price
 * (`price`) shows the symbol the same way and is never rounded (ADR-0114 D15).
 *
 * The symbol is aria-hidden, so the field's accessible name is its own; a caller names
 * the account and the currency in the name or the title where the screen needs them.
 * The caller supplies the input's look (`className`); the side the symbol sits on is
 * padded for the symbol's length, so a wide one (CA$, a three-letter code) does not
 * overlap the figure.
 */
export const MoneyInput = forwardRef<HTMLInputElement, MoneyInputProps>(function MoneyInput(
    { currency, value, onValueChange, price, placeholder, pending, wrapperClassName, locale, figureWidth, className, style, onBlur, ...props },
    ref,
) {
    // Set by a keystroke, cleared by the blur that rounds it: only a figure the user
    // changed is rounded, never a seeded one.
    const edited = useRef(false);
    const affix = currencyAffix(currency, locale);
    const before = affix?.position === 'before';
    const symbolPad = affix ? `0.75rem + ${[...affix.symbol].length}ch` : null;
    const width = figureWidth === undefined
        ? undefined
        : symbolPad ? `calc(${figureWidth} + ${symbolPad})` : figureWidth;
    const sized: CSSProperties | undefined = width === undefined ? style : { ...style, width };
    const padded: CSSProperties | undefined = symbolPad
        ? { ...sized, [before ? 'paddingLeft' : 'paddingRight']: `calc(${symbolPad})` }
        : sized;
    const symbol = affix ? (
        <span
            aria-hidden="true"
            data-money-affix={affix.position}
            className={cn(
                'pointer-events-none absolute inset-y-0 flex items-center font-mono text-xs text-text-subtle',
                before ? 'left-2' : 'right-2',
            )}
        >
            {affix.symbol}
        </span>
    ) : null;

    return (
        <span className={cn('relative block', wrapperClassName)}>
            {before ? symbol : null}
            <input
                ref={ref}
                type="number"
                step={price ? 'any' : amountStep(currency)}
                inputMode={price ? 'decimal' : amountInputMode(currency)}
                placeholder={placeholder ?? amountPlaceholder(currency)}
                value={value}
                {...props}
                {...pendingTextProps<HTMLInputElement>(pending)}
                className={className}
                style={padded}
                onChange={(e) => {
                    edited.current = true;
                    onValueChange(e.target.value);
                }}
                onBlur={(e) => {
                    if (edited.current) {
                        edited.current = false;
                        const text = e.target.value.trim();
                        const n = Number(text);
                        if (!price && currency && text.length > 0 && Number.isFinite(n)) {
                            const rounded = amountText(roundToMinorUnits(n, currency), currency);
                            if (rounded !== String(value)) onValueChange(rounded);
                        }
                    }
                    onBlur?.(e);
                }}
            />
            {before ? null : symbol}
        </span>
    );
});
