import { useMemo, useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { MoneyInput } from '@/components/ui/MoneyInput';
import { amountText, formatCurrency, formatPrice, formatSignedAmount } from '@/lib/money';
import { minorUnits } from '@/lib/minorUnits';
import { systemCategoryIds } from '@/lib/categoryChip';
import type { BankRow, RateUnavailable } from '@/lib/types';
import type { DerivedRate } from '@/lib/types/marketSeries';
import { roundedNote } from '@/routes/ledgers/home/roundedNote';
import { RegisterRow } from '@/routes/ledgers/register/shell/RegisterRow';
import { bankRowStrategy } from '@/routes/ledgers/register/strategies/bankRowStrategy';
import { BANK_COLS } from '@/routes/ledgers/register/bank/columns';
import { TxnRowEdit, type TxnRowMode } from '@/routes/ledgers/TxnRowEdit';

import { Board, WithoutMountFocus } from './SampleBoard';
import { SAMPLE_LEDGER as LEDGER, sampleAccount as account, sampleBankRow as bank } from './sampleRows';

/**
 * Style-guide samples for each currency's own decimals and for transfers between
 * currencies (ADR-0114 D13, D14, D15): money printed and typed at its currency's places,
 * the symbol inside a money input on either side of the figure, the register's other-leg
 * figure, the bank editor's second amount with each state of its rate line, and the
 * Review editor's rounded-at-import note. Rendered through the real formatters, input,
 * row strategy and editor, so a capture of this page is a capture of the product.
 *
 * Placeholder data only. The editors run against a local query client that never
 * fetches: each derived rate a second amount asks for is seeded under its key, never
 * stale, so a seeded query does not fetch.
 */

const USD_BANK = 'sg-x-usd';
const USD_SAVINGS = 'sg-x-usd-savings';
const EUR_BANK = 'sg-x-eur';
const JPY_BANK = 'sg-x-jpy';
const KWD_BANK = 'sg-x-kwd';
const CAD_BANK = 'sg-x-cad';
const GROCERIES = 'sg-x-groceries';

const ACCOUNTS = [
    account(USD_BANK, 'Test USD Bank', 'bank'),
    account(USD_SAVINGS, 'Test USD Savings', 'bank'),
    account(EUR_BANK, 'Test EUR Bank', 'bank', 'EUR'),
    account(JPY_BANK, 'Test JPY Bank', 'bank', 'JPY'),
    account(KWD_BANK, 'Test KWD Bank', 'bank', 'KWD'),
    account(CAD_BANK, 'Test CAD Bank', 'bank', 'CAD'),
    account(GROCERIES, 'Groceries', 'category'),
];
const PATHS = new Map(ACCOUNTS.map((a) => [a.id, a.name]));
const SYSTEM_CATEGORY_IDS = systemCategoryIds(ACCOUNTS);

/** The day every editor sample is dated, and the rate it is derived for. */
const DAY = '2026-11-12';
const today = new Date('2026-11-17T12:00:00Z');
const noop = () => {};

// ---------------------------------------------------------------------------
// Money at each currency's places
// ---------------------------------------------------------------------------

/** One currency per minor-unit count the table holds in use: two, none and three. */
const PLACES_SAMPLES = [
    { currency: 'USD', amount: 4820.5, price: 12.3456 },
    { currency: 'JPY', amount: 4820, price: 1234.5 },
    { currency: 'KWD', amount: 18.75, price: 2.5 },
] as const;

function MoneyPlacesSamples() {
    return (
        <table className="w-full text-xs">
            <thead className="border-b border-border bg-surface-muted text-[0.625rem] uppercase tracking-wider text-text-muted">
                <tr>
                    <th className="px-2 py-1.5 text-left font-semibold">Currency</th>
                    <th className="px-2 py-1.5 text-right font-semibold">Places</th>
                    <th className="px-2 py-1.5 text-right font-semibold">Amount</th>
                    <th className="px-2 py-1.5 text-right font-semibold">Signed</th>
                    <th className="px-2 py-1.5 text-right font-semibold">Price</th>
                </tr>
            </thead>
            <tbody className="divide-y divide-border">
                {PLACES_SAMPLES.map(({ currency, amount, price }) => (
                    <tr key={currency} data-places-sample={currency}>
                        <td className="px-2 py-1 font-mono">{currency}</td>
                        <td className="px-2 py-1 text-right font-mono tabular-nums">{minorUnits(currency)}</td>
                        <td data-sample="amount" className="px-2 py-1 text-right font-mono tabular-nums">{formatCurrency(amount, currency)}</td>
                        <td data-sample="signed" className="px-2 py-1 text-right font-mono tabular-nums text-state-danger">{formatSignedAmount(-amount, currency)}</td>
                        <td data-sample="price" className="px-2 py-1 text-right font-mono tabular-nums">{formatPrice(price, currency)}</td>
                    </tr>
                ))}
            </tbody>
        </table>
    );
}

// ---------------------------------------------------------------------------
// Money inputs: the symbol on each side, and each currency's step
// ---------------------------------------------------------------------------

/** The register editor's Amount look. */
const INPUT = 'h-control-28px w-full rounded border border-border bg-surface px-2 text-right font-mono text-xs tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

const INPUT_SAMPLES: Array<{ label: string; currency: string | null; value: number; locale?: string; price?: boolean }> = [
    { label: 'USD · symbol before', currency: 'USD', value: -100 },
    { label: 'EUR, de-DE · symbol after', currency: 'EUR', value: -100, locale: 'de-DE' },
    { label: 'CAD · a wide symbol', currency: 'CAD', value: 250 },
    { label: 'JPY · whole units, step 1', currency: 'JPY', value: 4820 },
    { label: 'KWD · three places, step 0.001', currency: 'KWD', value: 18.75 },
    { label: 'Currency not known yet · no symbol', currency: null, value: 42 },
    { label: 'Price in USD · any step, never rounded', currency: 'USD', value: 12.3456, price: true },
];

function MoneyInputDemo({ label, currency, value, locale, price }: (typeof INPUT_SAMPLES)[number]) {
    const [text, setText] = useState(() => amountText(value, price ? null : currency));
    return (
        <div className="flex items-center gap-3">
            <span className="w-fixed-208px shrink-0 font-mono text-[0.625rem] text-text-subtle">{label}</span>
            <MoneyInput
                currency={currency}
                value={text}
                onValueChange={setText}
                price={price}
                locale={locale}
                aria-label={label}
                wrapperClassName="w-fixed-160px"
                className={`${INPUT} ${text.trim().startsWith('-') ? 'text-state-danger' : 'text-text'}`}
            />
        </div>
    );
}

// ---------------------------------------------------------------------------
// Register rows
// ---------------------------------------------------------------------------

function Rows({ rows, currency }: { rows: BankRow[]; currency: string }) {
    return (
        <>
            {rows.map((row) => (
                <RegisterRow
                    key={row.id}
                    strategy={bankRowStrategy}
                    variant="txn"
                    row={row}
                    currency={currency}
                    today={today}
                    focused={false}
                    onFocus={noop}
                    selected={false}
                    onToggleSelected={noop}
                    status={row.status}
                    cycleStatus={row.status}
                    statusHeaderId={row.headerId}
                    onCycleReconStatus={noop}
                    readOnly={row.isReadOnly}
                    accountPaths={PATHS}
                    systemCategoryIds={SYSTEM_CATEGORY_IDS}
                />
            ))}
        </>
    );
}

/** A Groceries row paid from `from`: its Amount in the paying account's currency, its
 *  Balance in the reporting one. */
const paidFrom = (from: string, name: string, currencyCode: string, amount: number, balanceAfter: number, postedAt: string, payee: string): BankRow =>
    bank({
        accountId: GROCERIES, payee, amount, currencyCode, balanceAfter, balanceCurrencyCode: 'USD', postedAt,
        counterpartyAccountId: from, counterpartyAccountName: name, counterpartyAccountType: 'bank',
    });

const CATEGORY_ROWS: BankRow[] = [
    paidFrom(KWD_BANK, 'Test KWD Bank', 'KWD', 18.75, 295.57, '2026-11-13T12:00:00Z', 'Sample Grocer'),
    paidFrom(JPY_BANK, 'Test JPY Bank', 'JPY', 4820, 234.44, '2026-11-12T12:00:00Z', 'Corner Market'),
    paidFrom(USD_BANK, 'Test USD Bank', 'USD', 42.18, 202.63, '2026-11-10T12:00:00Z', 'Sample Grocer'),
    paidFrom(USD_BANK, 'Test USD Bank', 'USD', -8.99, 160.45, '2026-11-07T12:00:00Z', 'Sample Grocer refund'),
];

const USD_SIDE_ROWS: BankRow[] = [
    bank({
        accountId: USD_BANK, payee: 'Fund EUR account', amount: -100, balanceAfter: 1900, postedAt: '2026-11-12T12:00:00Z',
        counterpartyAccountId: EUR_BANK, counterpartyAccountName: 'Test EUR Bank', counterpartyAccountType: 'bank',
        counterpartyAmount: 90, counterpartyCurrencyCode: 'EUR',
    }),
    bank({
        accountId: USD_BANK, payee: 'Move to savings', amount: -50, balanceAfter: 2000, postedAt: '2026-11-10T12:00:00Z',
        counterpartyAccountId: USD_SAVINGS, counterpartyAccountName: 'Test USD Savings', counterpartyAccountType: 'bank',
        counterpartyAmount: null, counterpartyCurrencyCode: null,
    }),
];

const EUR_SIDE_ROWS: BankRow[] = [
    bank({
        accountId: EUR_BANK, payee: 'Fund EUR account', amount: 90, currencyCode: 'EUR', balanceAfter: 566.5,
        balanceCurrencyCode: 'EUR', postedAt: '2026-11-12T12:00:00Z',
        counterpartyAccountId: USD_BANK, counterpartyAccountName: 'Test USD Bank', counterpartyAccountType: 'bank',
        counterpartyAmount: -100, counterpartyCurrencyCode: 'USD',
    }),
];

// ---------------------------------------------------------------------------
// The bank editor's second amount
// ---------------------------------------------------------------------------

const missing = (fromCurrency: string): RateUnavailable =>
    ({ fromCurrency, toCurrency: 'USD', date: DAY, reason: 'rate_unavailable' });

/** Every derived rate a sample asks for, under the key useDerivedRate reads, for the
 *  magnitude its Amount converts. */
const DERIVED: Array<{ amount: number; answer: DerivedRate }> = [
    {
        // USD to EUR on the day, with the reporting currency one of the two: one stored leg.
        amount: 100,
        answer: {
            fromCurrency: 'USD', toCurrency: 'EUR', date: DAY, reportingCurrency: 'USD',
            rate: 0.9, rateDate: DAY, legs: [{ fromCurrency: 'EUR', toCurrency: 'USD', date: DAY, rate: 1 / 0.9 }],
            converted: 90, rateUnavailable: [],
        },
    },
    {
        // USD to KWD with no KWD rate stored: one leg missing.
        amount: 100,
        answer: {
            fromCurrency: 'USD', toCurrency: 'KWD', date: DAY, reportingCurrency: 'USD',
            rate: null, rateDate: null, legs: [], converted: null, rateUnavailable: [missing('KWD')],
        },
    },
    {
        // CAD to KWD through USD with neither leg stored.
        amount: 100,
        answer: {
            fromCurrency: 'CAD', toCurrency: 'KWD', date: DAY, reportingCurrency: 'USD',
            rate: null, rateDate: null, legs: [], converted: null, rateUnavailable: [missing('CAD'), missing('KWD')],
        },
    },
];

function newTransfer(sourceAccountId: string, postings: Array<{ counterpartyAccountId: string; amount: number }>): TxnRowMode {
    return { kind: 'new', sourceAccountId, postedAt: DAY, prefill: { postings } };
}

/** The transfer of the first sample, opened from the EUR register as stored. */
const EDIT_FROM_EUR: TxnRowMode = {
    kind: 'edit', headerId: 'sg-x-edit', sourceAccountId: EUR_BANK,
    postings: [{
        legId: 'sg-x-edit-leg', counterpartyAccountId: USD_BANK, counterpartyAccountName: 'Test USD Bank',
        amount: 90, legMemo: null, legTags: [], counterpartyAmount: -100,
    }],
    payee: 'Fund EUR account', memo: null, checkNumber: null,
    postedAt: `${DAY}T00:00:00.000Z`, transactedAt: null, balanceAfter: 566.5, balanceCurrencyCode: 'EUR',
    tags: [], inherited: null, needsReview: false,
};

/** A feed row on the JPY account, rounded at import and awaiting review. */
const ROUNDED_REVIEW: TxnRowMode = {
    kind: 'edit', headerId: 'sg-x-review', sourceAccountId: JPY_BANK,
    postings: [{
        legId: 'sg-x-review-leg', counterpartyAccountId: GROCERIES, counterpartyAccountName: 'Groceries',
        amount: 1235, legMemo: null, legTags: [],
    }],
    payee: 'Test Grocer refund', memo: null, checkNumber: null,
    postedAt: '2026-11-14T00:00:00.000Z', transactedAt: null, balanceAfter: null, balanceCurrencyCode: 'JPY',
    tags: [], inherited: null, needsReview: true,
};
const ROUNDED_NOTE = roundedNote({ needsReview: true, roundedAtImport: true, originalAmount: '1234.5' });

function Editor({ mode, currency, dialog = false, amountNote }: { mode: TxnRowMode; currency: string; dialog?: boolean; amountNote?: string | null }) {
    const common = {
        ledgerId: LEDGER, mode, payees: [], accounts: ACCOUNTS, accountPaths: PATHS, currency,
        onCancel: noop, onSavePatch: noop, onSaveCreate: noop, isSaving: false, saveError: null,
        cancelOnOutsideClick: false, hints: false, amountNote,
    };
    return dialog
        ? <TxnRowEdit {...common} layout="dialog" />
        : <TxnRowEdit {...common} cols={BANK_COLS} />;
}

export function CurrencySamples() {
    // Local and offline: every query the editors leave `enabled` unset on is held, and
    // the derived rates, which set their own, are answered from the seeded cache.
    const client = useMemo(() => {
        const c = new QueryClient({
            defaultOptions: { queries: { enabled: false, retry: false, staleTime: Infinity, gcTime: Infinity } },
        });
        for (const { amount, answer } of DERIVED) {
            c.setQueryData<DerivedRate>(
                ['fx-rates', LEDGER, 'derived', answer.fromCurrency, answer.toCurrency, answer.date, amount],
                answer,
            );
        }
        return c;
    }, []);

    return (
        <QueryClientProvider client={client}>
            <WithoutMountFocus className="space-y-6">
                <Board title="Money at each currency's places" note="Each figure at exactly its currency's places; a price at least those and at most six.">
                    <MoneyPlacesSamples />
                </Board>

                <Board title="Money inputs" note="The symbol sits inside the input, muted and not typed, on the side the formatter puts it; step, placeholder and blur rounding follow the currency's places.">
                    <div id="money-inputs" className="space-y-2 p-3">
                        {INPUT_SAMPLES.map((s) => <MoneyInputDemo key={s.label} {...s} />)}
                    </div>
                </Board>

                <Board title="Category register · Groceries, paid in three currencies" note="Each Amount in the paying account's currency at its own places, right-aligned; the Balance in the reporting currency.">
                    <div id="category-register">
                        <Rows rows={CATEGORY_ROWS} currency="USD" />
                    </div>
                </Board>

                <Board title="Transfer between currencies · Test USD Bank register" note="The other leg's own amount follows the account chip, muted. The same-currency transfer below it is unchanged.">
                    <Rows rows={USD_SIDE_ROWS} currency="USD" />
                </Board>

                <Board title="Transfer between currencies · Test EUR Bank register">
                    <Rows rows={EUR_SIDE_ROWS} currency="EUR" />
                </Board>

                <Board title="Second amount · new, prefilled from the day's rate">
                    <Editor mode={newTransfer(USD_BANK, [{ counterpartyAccountId: EUR_BANK, amount: -100 }])} currency="USD" />
                </Board>

                <Board title="Second amount · opened from the other side, as stored" note="Both amounts seeded and never refilled; the line shows their ratio, this register's currency first.">
                    <Editor mode={EDIT_FROM_EUR} currency="EUR" />
                </Board>

                <Board title="Second amount · no stored rate for one leg" note="Nothing to prefill: the field is empty and required.">
                    <Editor mode={newTransfer(USD_BANK, [{ counterpartyAccountId: KWD_BANK, amount: -100 }])} currency="USD" />
                </Board>

                <Board title="Second amount · no stored rate for either leg">
                    <Editor mode={newTransfer(CAD_BANK, [{ counterpartyAccountId: KWD_BANK, amount: -100 }])} currency="CAD" />
                </Board>

                <Board title="Second amount · a split with one line to another currency" note="The line's rate sits on a second row of the line, ending under its field.">
                    <Editor
                        mode={newTransfer(USD_BANK, [
                            { counterpartyAccountId: GROCERIES, amount: -40 },
                            { counterpartyAccountId: EUR_BANK, amount: -100 },
                        ])}
                        currency="USD"
                    />
                </Board>

                <Board title="Review editor · a row rounded at import" note="Amount's description under it: the bank's figure as sent, with no symbol.">
                    <div className="@container/dialog max-w-5xl p-3">
                        <Editor mode={ROUNDED_REVIEW} currency="JPY" dialog amountNote={ROUNDED_NOTE} />
                    </div>
                </Board>
            </WithoutMountFocus>
        </QueryClientProvider>
    );
}
