import { createRef } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import type { RegisterFilterArgs } from '@/lib/api/register';

import { RegisterFilterChips, type RegisterFilterChipsProps } from './RegisterFilterChips';

/** The chips on a category register (no currency) unless a currency is named. */
function renderChips(filter: RegisterFilterArgs, currency: string | null = null) {
    const onChange = vi.fn();
    const search = createRef<HTMLInputElement>();
    render(
        <>
            <input ref={search} aria-label="Search" />
            <RegisterFilterChips filter={filter} onChange={onChange} accounts={[]} currency={currency} resultCount={3} fallbackFocusRef={search} />
        </>,
    );
    return { onChange };
}

describe('RegisterFilterChips: a host', () => {
    // Clear all and removing the last chip take the row away, and focus goes to
    // the host's fallback (ADR-0023 §L). A host that could leave it out would
    // leave focus on the page, so the type refuses one that does; the type
    // check of this file is the test. The other props are checked on their own
    // lines, so the only error the directive can absorb is the missing fallback.
    it('must say where focus goes when the row goes away', () => {
        const withoutFallback: Omit<RegisterFilterChipsProps, 'fallbackFocusRef'> = {
            filter: { tag: 'Tag A' },
            onChange: () => {},
            accounts: [],
            currency: 'USD',
            resultCount: 3,
        };
        // @ts-expect-error fallbackFocusRef is required.
        const host: RegisterFilterChipsProps = withoutFallback;
        expect(host).toBe(withoutFallback);
    });
});

describe('RegisterFilterChips: the amount range', () => {
    it('names both bounds as typed, in no currency, on a category register whose rows need not share one (ADR-0114 D14)', () => {
        renderChips({ amountMin: 10, amountMax: 50 });

        expect(screen.getByRole('button', { name: /Amount: 10 – 50/ })).toBeInTheDocument();
        expect(screen.queryByText(/\$/)).toBeNull();
    });

    it('an open lower bound reads as zero', () => {
        renderChips({ amountMax: 50 });
        expect(screen.getByRole('button', { name: /Amount: 0 – 50/ })).toBeInTheDocument();
    });

    it('an open upper bound reads as unbounded', () => {
        renderChips({ amountMin: 10 });
        expect(screen.getByRole('button', { name: /Amount: 10 – ∞/ })).toBeInTheDocument();
    });

    it('removing the chip clears both bounds', () => {
        const { onChange } = renderChips({ amountMin: 10, amountMax: 50, tag: 'tag-a' });

        fireEvent.click(screen.getByRole('button', { name: /Amount: 10 – 50/ }));

        expect(onChange).toHaveBeenCalledWith({ amountMin: undefined, amountMax: undefined, tag: 'tag-a' });
    });
});
