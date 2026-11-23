import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';

import { FieldSizeContext, NARROW_DIALOG_FIELD } from '@/components/ui/fieldSize';
import { currencyAffix } from '@/lib/money';

import { FeeField } from './FeeField';

// The fee amount is a fixed-width field that shows its brokerage's currency symbol
// (ADR-0114 D14). The symbol's padding is added to the width, not taken out of it, so a
// three-character symbol does not cut an ordinary fee short, in the register row or in
// the narrow editor dialog's larger text.

function renderFee(currency: string | null) {
    render(
        <FieldSizeContext.Provider value={NARROW_DIALOG_FIELD}>
            <FeeField
                accounts={[]}
                feeAccountId={null}
                onChangeFeeAccount={() => {}}
                feeAmount={100}
                onChangeFeeAmount={() => {}}
                currency={currency}
            />
        </FieldSizeContext.Provider>,
    );
    return screen.getByRole('spinbutton', { name: 'Fee amount' }) as HTMLInputElement;
}

describe('FeeField amount width', () => {
    it('adds a three-character symbol\'s padding to the 96px figure width', () => {
        const affix = currencyAffix('CAD');
        // The case is only meaningful for a symbol of three characters (CA$).
        expect([...affix!.symbol]).toHaveLength(3);
        const input = renderFee('CAD');
        // The style engine may reorder a calc's terms, so each is checked on its own.
        for (const term of ['calc(', 'var(--spacing-fixed-96px)', '0.75rem', '3ch']) {
            expect(input.style.width).toContain(term);
        }
        // No class sets a width that the inline one would have to fight.
        expect(input.className).not.toMatch(/(^|\s)w-/);
    });

    it('stays at the 96px figure width while the currency is not known', () => {
        const input = renderFee(null);
        expect(input.style.width).toBe('var(--spacing-fixed-96px)');
    });
});
