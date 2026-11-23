// The second amount of a transfer between two real accounts in different currencies
// (ADR-0114 D13, D14): the other account's own figure, which the bank editor, a split
// line and the investment editor show right of Amount, with the rate line under it.
//
// The field holds a magnitude. Its sign is always opposite Amount's (ADR-0114 D13), so a
// minus typed into it is ignored, and flipping Amount's sign flips the figure it saves.
// Its words are one caption (Received when the money moves into the other account, Sent
// when it moves out of it), and the account and its currency's code in the accessible
// name and title only.

import { missingLegsByDay } from './conversion';
import { formatLedgerDateLong } from './dates';
import { formatFxRate } from './money';
import type { DerivedRate } from './types/marketSeries';

/** Which way the money moves for the other account. */
export type SecondAmountDirection = 'received' | 'sent';

/** The other account of a transfer between currencies, as the second amount names it. */
export interface SecondAmountSide {
    /** The other account's id. */
    accountId: string;
    /** Its name, as the editor shows it (the account path). */
    accountName: string;
    /** Its currency, the one the second amount is typed in. */
    currency: string;
    direction: SecondAmountDirection;
}

/** The field's one-word caption. */
export function secondAmountCaption(direction: SecondAmountDirection): string {
    return direction === 'received' ? 'Received' : 'Sent';
}

/** The field's accessible name and title: the account and its currency's code. */
export function secondAmountName(side: Pick<SecondAmountSide, 'accountName' | 'currency' | 'direction'>): string {
    return side.direction === 'received'
        ? `Amount received in ${side.accountName}, in ${side.currency}`
        : `Amount sent from ${side.accountName}, in ${side.currency}`;
}

/** What a missing or zero second amount asks for. */
export function secondAmountRequired(side: Pick<SecondAmountSide, 'accountName' | 'currency' | 'direction'>): string {
    return side.direction === 'received'
        ? `Enter the amount in ${side.currency} that arrived in ${side.accountName}`
        : `Enter the amount in ${side.currency} that left ${side.accountName}`;
}

/**
 * The bank editor's direction from its Amount, the source side's signed figure: money
 * leaving the source arrives in the other account. A blank or zero Amount reads as
 * received, the way a new entry opens.
 */
export function bankDirection(amountText: string): SecondAmountDirection {
    const n = Number(amountText);
    return amountText.trim().length > 0 && Number.isFinite(n) && n > 0 ? 'sent' : 'received';
}

/** A typed figure's magnitude: every minus is dropped, as the sign follows from Amount. */
export function secondAmountMagnitude(text: string): string {
    return text.replace(/-/g, '');
}

/**
 * The text the field shows for a stored magnitude: with a minus when the other
 * account's figure is negative, the money it sent, as a bank figure reads.
 */
export function secondAmountDisplay(magnitude: string, direction: SecondAmountDirection): string {
    if (magnitude.length === 0 || direction === 'received') return magnitude;
    return Number(magnitude) === 0 ? magnitude : `-${magnitude}`;
}

/** The figure a save sends: the magnitude with the direction's sign. */
export function signedSecondAmount(magnitude: number, direction: SecondAmountDirection): number {
    const m = Math.abs(magnitude);
    return direction === 'received' ? m : (m === 0 ? 0 : -m);
}

/** The figure a magnitude text parses to, else null for a blank or unparseable one. */
export function parsedMagnitude(text: string): number | null {
    if (text.trim().length === 0) return null;
    const n = Number(text);
    return Number.isFinite(n) ? Math.abs(n) : null;
}

/**
 * The rate line under the second amount (ADR-0114 D14).
 *
 *   * While it is prefilled: "Rate for <date>: 1 X = r Y", the derived rate the one rate
 *     rule gives the entry's date, naming the older stored leg's day.
 *   * With no rate: "No A to R rate for <date>", each missing stored leg in the
 *     direction it is stored, "No A or B to R rate for <date>" when both are.
 *   * Once typed or seeded: the ratio of the two amounts, "1 X = r Y, from the two
 *     amounts", this side's currency first.
 *
 * Empty while the answer is not in, or while there is no ratio to show.
 */
export function rateLineText(args:
    | { kind: 'derived'; answer: DerivedRate | undefined }
    | { kind: 'ratio'; from: string; to: string; amount: number | null; other: number | null },
): string {
    if (args.kind === 'ratio') {
        const { amount, other } = args;
        if (amount === null || other === null || amount === 0 || other === 0) return '';
        return `${formatFxRate(Math.abs(other) / Math.abs(amount), args.from, args.to)}, from the two amounts`;
    }
    const answer = args.answer;
    if (answer === undefined) return '';
    if (answer.rate !== null && answer.rateDate !== null) {
        return `Rate for ${formatLedgerDateLong(answer.rateDate)}: ${formatFxRate(answer.rate, answer.fromCurrency, answer.toCurrency)}`;
    }
    // Both legs are read for the entry's one day, so there is one clause.
    return missingLegsByDay(answer.rateUnavailable)[0] ?? '';
}

/** The server's refusals of a missing or zero second amount (ADR-0114 D13): the bank
 *  posting's and the investment transfer's. A same-signed one has no words of its own
 *  here, since the field's sign always opposes Amount's; its refusal reads as sent. */
const SECOND_AMOUNT_ASKED = new Set([
    'transaction-posting-counterparty-amount-required',
    'transaction-posting-two-amounts-nonzero',
    'investment-txn-transfer-amount-required',
    'investment-txn-transfer-amounts-nonzero',
]);

/**
 * What a save refused for a missing or zero second amount asks for, in the field's own
 * words (board ED-4: a zero is refused with the same sentence), when the editor shows
 * exactly one second amount, so the refusal can only be about it. Null for any other
 * refusal, or when the editor shows none or several: the server's detail then stands.
 */
export function secondAmountRefusal(
    code: string | undefined,
    sides: readonly (SecondAmountSide | null)[],
): string | null {
    if (code === undefined || !SECOND_AMOUNT_ASKED.has(code)) return null;
    const shown = sides.filter((s): s is SecondAmountSide => s !== null);
    return shown.length === 1 ? secondAmountRequired(shown[0]!) : null;
}
