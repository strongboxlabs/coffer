import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';

import { amountText } from '@/lib/money';

import { MoneyInput } from './MoneyInput';

// A money input shows its currency's symbol, muted and not typed, and takes the
// currency's places (ADR-0114 D14, D15). Each case drives a controlled field as an
// editor does: the text is held by the caller and handed back on every change.

function Field({
    currency, initial = '', locale, onValueChange,
}: { currency: string | null; initial?: string; locale?: string; onValueChange?: (text: string) => void }) {
    const [text, setText] = useState(initial);
    return (
        <label>
            <span>Amount</span>
            <MoneyInput
                currency={currency}
                value={text}
                locale={locale}
                onValueChange={(next) => {
                    onValueChange?.(next);
                    setText(next);
                }}
                className="px-2"
            />
        </label>
    );
}

function setup(props: Parameters<typeof Field>[0]) {
    const view = render(<Field {...props} />);
    // The symbol is not part of the name: the field is named by its label alone.
    const input = screen.getByRole('spinbutton', { name: 'Amount' }) as HTMLInputElement;
    const affix = view.container.querySelector('[data-money-affix]');
    return { input, affix };
}

function typeAndLeave(input: HTMLInputElement, text: string) {
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: text } });
    fireEvent.blur(input);
}

describe('MoneyInput as a price (ADR-0114 D15)', () => {
    function Price({ currency }: { currency: string }) {
        const [text, setText] = useState('');
        return (
            <label>
                <span>Price</span>
                <MoneyInput price currency={currency} value={text} onValueChange={setText} locale="en-US" />
            </label>
        );
    }

    it('shows the currency\'s symbol, takes any step and keeps every place on blur', () => {
        const view = render(<Price currency="JPY" />);
        const input = screen.getByRole('spinbutton', { name: 'Price' }) as HTMLInputElement;
        expect(view.container.querySelector('[data-money-affix]')!.textContent).toBe('¥');
        expect(input.step).toBe('any');
        // A price in a whole-unit currency still takes fractions on the keypad.
        expect(input.inputMode).toBe('decimal');
        typeAndLeave(input, '12.345678');
        expect(input.value).toBe('12.345678');
    });
});

describe('MoneyInput', () => {
    it('shows a dollar sign before the figure, hidden from the field\'s name', () => {
        const { input, affix } = setup({ currency: 'USD', locale: 'en-US' });
        expect(affix).not.toBeNull();
        expect(affix!.textContent).toBe('$');
        expect(affix!.getAttribute('aria-hidden')).toBe('true');
        // Before the figure: it comes first in the wrapper, and pads the left side.
        expect(affix!.nextElementSibling).toBe(input);
        expect(input.style.paddingLeft).not.toBe('');
        expect(input.style.paddingRight).toBe('');
        expect(input.step).toBe('0.01');
        expect(input.placeholder).toBe('0.00');
    });

    it('takes whole yen: a whole step, the numeric keypad and a bare zero', () => {
        const { input, affix } = setup({ currency: 'JPY' });
        expect(affix!.textContent).not.toBe('');
        expect(input.step).toBe('1');
        expect(input.inputMode).toBe('numeric');
        expect(input.placeholder).toBe('0');
    });

    it('rounds a typed fraction to whole yen on blur, half away from zero', () => {
        const { input } = setup({ currency: 'JPY' });
        typeAndLeave(input, '1234.5');
        expect(input.value).toBe('1235');
        typeAndLeave(input, '-1234.5');
        expect(input.value).toBe('-1235');
    });

    it('rounds a three-place currency at three places on blur, and pads a whole entry to them', () => {
        const { input } = setup({ currency: 'KWD' });
        expect(input.step).toBe('0.001');
        typeAndLeave(input, '1.2345');
        expect(input.value).toBe('1.235');
        typeAndLeave(input, '2');
        expect(input.value).toBe('2.000');
    });

    it('shows no symbol and rounds nothing while the currency is null', () => {
        const { input, affix } = setup({ currency: null });
        expect(affix).toBeNull();
        expect(input.style.paddingLeft).toBe('');
        expect(input.step).toBe('any');
        expect(input.placeholder).toBe('');
        typeAndLeave(input, '1.23456');
        expect(input.value).toBe('1.23456');
    });

    it('leaves a seeded figure finer than its currency as it is when the field is only visited', () => {
        const onValueChange = vi.fn();
        const seeded = amountText(1234.5, 'JPY');
        const { input } = setup({ currency: 'JPY', initial: seeded, onValueChange });
        fireEvent.focus(input);
        fireEvent.blur(input);
        expect(input.value).toBe(seeded);
        expect(onValueChange).not.toHaveBeenCalled();
    });

    it('rounds that figure once the user changes it', () => {
        const { input } = setup({ currency: 'JPY', initial: amountText(1234.5, 'JPY') });
        typeAndLeave(input, '1234.75');
        expect(input.value).toBe('1235');
        // A later visit with no keystroke leaves the rounded figure alone too.
        fireEvent.focus(input);
        fireEvent.blur(input);
        expect(input.value).toBe('1235');
    });

    it('puts the symbol after the figure for a locale that writes it there, and pads the right side', () => {
        const { input, affix } = setup({ currency: 'EUR', locale: 'de-DE' });
        expect(affix!.textContent).toBe('€');
        expect(affix!.previousElementSibling).toBe(input);
        expect(affix!.className).toContain('right-2');
        expect(input.style.paddingRight).not.toBe('');
        expect(input.style.paddingLeft).toBe('');
    });

    it('pads a wider symbol further than a narrow one', () => {
        const narrow = render(<MoneyInput currency="USD" locale="en-US" value="" onValueChange={() => {}} aria-label="Narrow" />);
        const wide = render(<MoneyInput currency="CAD" locale="en-US" value="" onValueChange={() => {}} aria-label="Wide" />);
        const pad = (name: string) => (screen.getByRole('spinbutton', { name }) as HTMLInputElement).style.paddingLeft;
        expect(pad('Narrow')).not.toBe(pad('Wide'));
        expect(pad('Wide')).toContain('3ch');
        narrow.unmount();
        wide.unmount();
    });

    it('grows a fixed-width field by its symbol\'s padding, so the figure keeps its room', () => {
        const field = (currency: string | null, name: string) => render(
            <MoneyInput currency={currency} locale="en-US" figureWidth="96px" value="" onValueChange={() => {}} aria-label={name} />,
        );
        const style = (name: string) => (screen.getByRole('spinbutton', { name }) as HTMLInputElement).style;
        field('CAD', 'Wide');
        field(null, 'Bare');
        // The width is the figure's 96px plus what the symbol's side is padded by (the
        // style engine may reorder the terms of a calc, so each is checked on its own).
        const wide = style('Wide');
        for (const term of ['0.75rem', '3ch']) {
            expect(wide.paddingLeft).toContain(term);
            expect(wide.width).toContain(term);
        }
        expect(wide.width).toContain('96px');
        // No symbol, no padding to make room for: the figure's width as given.
        expect(style('Bare').width).toBe('96px');
    });
});
