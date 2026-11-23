import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';

import type { BankRow, InvestmentRow } from '@/lib/types';
import { formatCurrency } from '@/lib/money';

import { bankRow, investmentRow } from '../../home/homeTestFixtures';
import { bankRowStrategy } from './bankRowStrategy';
import { renderBankSlot6 } from './bankStrategy';
import { investmentStrategy } from './investmentStrategy';

/**
 * The other leg's own amount on a register row (ADR-0114 D14, board RG-1a): on a transfer
 * between currencies the counterparty cell shows it after the account chip, muted, in the
 * app's formatter, titled with the way the money moved for that account. Same-currency
 * transfers and category rows are unchanged. Every name and figure is invented.
 */

const NONE: ReadonlySet<string> = new Set();

/** A transfer row on a USD account out to an account in EUR. */
const outToEuro = (over: Partial<BankRow> = {}): BankRow => bankRow({
    amount: -100,
    counterpartyAccountId: 'acct-eur', counterpartyAccountName: 'Account E', counterpartyAccountType: 'bank',
    counterpartyAmount: 90, counterpartyCurrencyCode: 'EUR',
    ...over,
});

const slot6 = (row: BankRow) => render(<div>{renderBankSlot6(row, undefined, null, NONE)}</div>);

/** The chip of `label`: the span that carries its colour. */
const chipOf = (label: string) => screen.getByText(label).closest('span[class*="bg-"]') as HTMLElement;

describe('a bank row\'s other leg', () => {
    it('follows the chip, muted, after a hidden dot, in the formatter, titled Received in', () => {
        const { container } = slot6(outToEuro());

        const figure = container.querySelector('[data-other-leg]') as HTMLElement;
        expect(figure).toHaveTextContent(formatCurrency(90, 'EUR'));
        expect(figure.textContent).not.toContain('+');
        expect(figure).toHaveAttribute('title', `Received in Account E: ${formatCurrency(90, 'EUR')}`);
        expect(figure).toHaveClass('text-text-muted', 'shrink-0', 'font-mono', 'tabular-nums');
        // Never red, whichever way it moved (ADR-0023's amendment).
        expect(figure.className).not.toMatch(/danger/);

        // Chip, dot, figure, in that order, in one line.
        const chip = chipOf('Account E');
        const dot = chip.nextElementSibling as HTMLElement;
        expect(dot).toHaveTextContent('·');
        expect(dot).toHaveAttribute('aria-hidden', 'true');
        expect(dot.nextElementSibling).toBe(figure);
    });

    it('reads Sent from on the receiving side, the figure negative and still muted', () => {
        const { container } = slot6(bankRow({
            accountId: 'acct-eur', currencyCode: 'EUR', amount: 90,
            counterpartyAccountId: 'acct-usd', counterpartyAccountName: 'Account U', counterpartyAccountType: 'bank',
            counterpartyAmount: -100, counterpartyCurrencyCode: 'USD',
        }));

        const figure = container.querySelector('[data-other-leg]') as HTMLElement;
        expect(figure).toHaveTextContent(formatCurrency(-100, 'USD'));
        expect(figure).toHaveAttribute('title', `Sent from Account U: ${formatCurrency(-100, 'USD')}`);
        expect(figure.className).not.toMatch(/danger/);
    });

    it.each<[string, Partial<BankRow>]>([
        ['a transfer in one currency', {
            counterpartyAccountId: 'acct-usd-2', counterpartyAccountName: 'Account S', counterpartyAccountType: 'bank',
            counterpartyAmount: null, counterpartyCurrencyCode: null,
        }],
        ['a category row', {
            counterpartyAccountId: 'cat-a', counterpartyAccountName: 'Account S', counterpartyAccountType: 'category',
            counterpartyAmount: undefined, counterpartyCurrencyCode: undefined,
        }],
    ])('is absent on %s, whose chip stands alone', (_, over) => {
        const { container } = slot6(bankRow(over));

        // Anchored on the chip, which is there: nothing follows it.
        const chip = chipOf('Account S');
        expect(chip).toBeInTheDocument();
        expect(chip.nextElementSibling).toBeNull();
        expect(container.querySelector('[data-other-leg]')).toBeNull();
        expect(container).not.toHaveTextContent('·');
    });

    it('lets a long name truncate while the figure keeps its width', () => {
        const long = 'Account with a name far longer than the category cell can hold on one line';
        const { container } = slot6(outToEuro({ counterpartyAccountName: long }));

        const chip = chipOf(long);
        expect(chip).toHaveClass('min-w-0', 'truncate');
        expect(chip.parentElement).toHaveClass('flex', 'min-w-0');
        expect(container.querySelector('[data-other-leg]')).toHaveClass('shrink-0');
    });

    it('shows a split line\'s own other amount', () => {
        const line = outToEuro({ id: 'leg-2', txnGroupId: 'g-1', legIndex: 1, amount: -100, counterpartyAmount: 90 });
        const { container } = render(<div>{bankRowStrategy.renderBody(line, {
            variant: 'split-leg', currency: 'USD', today: new Date(2026, 0, 31), isReadOnly: false,
            systemCategoryIds: NONE,
        })}</div>);

        const figure = container.querySelector('[data-other-leg]') as HTMLElement;
        expect(figure).toHaveTextContent(formatCurrency(90, 'EUR'));
        expect(figure).toHaveAttribute('title', `Received in Account E: ${formatCurrency(90, 'EUR')}`);
    });
});

describe('an investment row\'s transfer chip', () => {
    const transfer = (over: Partial<InvestmentRow>) => investmentRow({
        currencyCode: 'EUR', amount: 90,
        transferAccountId: 'acct-usd', transferAccountName: 'Account U', transferAccountType: 'bank',
        transferAmount: -100, transferCurrencyCode: 'USD',
        ...over,
    });

    it.each(['transfer', 'buyx', 'sellx', 'divx'])('shows the other amount after the chip on %s', (action) => {
        const row = transfer({
            investmentAction: action,
            ...(action === 'sellx' || action === 'divx' ? { amount: -90, transferAmount: 100 } : {}),
        });
        const { container } = render(<div>{investmentStrategy.renderSlot6(row, undefined, NONE)}</div>);

        const figure = container.querySelector('[data-other-leg]') as HTMLElement;
        const shown = row.transferAmount!;
        expect(figure).toHaveTextContent(formatCurrency(shown, 'USD'));
        expect(figure).toHaveAttribute('title',
            `${shown > 0 ? 'Received in' : 'Sent from'} Account U: ${formatCurrency(shown, 'USD')}`);
        expect(chipOf('Account U').nextElementSibling).toHaveTextContent('·');
    });

    it('leaves a transfer in one currency as the chip alone', () => {
        const { container } = render(<div>{investmentStrategy.renderSlot6(
            transfer({ investmentAction: 'transfer', transferAmount: null, transferCurrencyCode: null }), undefined, NONE)}</div>);

        expect(chipOf('Account U').nextElementSibling).toBeNull();
        expect(container.querySelector('[data-other-leg]')).toBeNull();
    });
});
