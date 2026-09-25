import type {
    InvestmentRow,
    LedgerInvestmentAction,
} from '@/lib/types';
import { isLinkedAction } from './actionLayout';
import { seedTransactedAt, type InvestmentTxnDraft } from './validation';

/**
 * Build an editor draft from a sync-imported bank-shape row carrying
 * ADR-0031 Phase 3 classifier hints (`ingestActionHint` +
 * `ingestSecurityId`). The orchestrator inserts these rows with
 * cash-flow legs against an Uncategorized counterparty; the legs
 * therefore don't carry the investment-shape posting roles that
 * <c>legsToDraft</c> inverts off of.
 *
 * Conservative seed: action + (where applicable) the user-mapped
 * security pre-fill; cash amount comes from the brokerage-side cash
 * leg; shares + price + categories + transfer destinations stay
 * null for the user to fill in.
 *
 * The editor's existing per-action layouts hide the irrelevant fields;
 * validation gates Save on the action's required-field set, so
 * partial drafts naturally surface the user's TODO list.
 */
export function hintToDraft(
    action: LedgerInvestmentAction | null,
    brokerageAccountId: string,
    header: {
        postedAt: string;
        /** Tax date from the row; blanked below when it is the posted day. */
        transactedAt?: string | null;
        payee: string | null;
        memo: string | null;
        checkNumber: string | null;
        /** Header-level tags (ADR-0009) as the row carries them. Optional so
         *  callers holding only the text fields still typecheck. */
        tags?: readonly string[];
    },
    legs: readonly InvestmentRow[],
    ingestSecurityId: string | null,
    /** Mig 113: per-row investment prefill carriers. Populated only
     *  on OFX investment rows. Caller pulls these off the canonical
     *  leg's resolved DTO and passes them in so the draft opens
     *  fully populated for the buy/sell/reinvest action family. */
    ingestShares: number | null = null,
    ingestUnitPrice: number | null = null,
    ingestFee: number | null = null,
    /** Mig 228: the file's AUTHORITATIVE total, for rows whose cash leg is
     *  deliberately zero. See its use below. */
    ingestAmount: number | null = null,
): InvestmentTxnDraft {
    // Bank-shape sync rows have two legs at posting_index 0:
    // brokerage cash (signed = -spend / +receive) and an Uncategorized
    // counterparty. Pull the cash leg by accountId match.
    const cashLeg = legs.find((l) => l.accountId === brokerageAccountId) ?? null;
    const cashAmount = cashLeg?.amount ?? 0;

    // For the linked actions (buy / sell / buyx / sellx / dividend_reinvest)
    // the Amount field is a DERIVED value (shares × price). BUT when the wire
    // booked a real cash total — a plain buy/sell posts the ACTUAL amount to the
    // brokerage cash leg — that total is AUTHORITATIVE and must win on open: it
    // can differ from shares × price by a cent or two because the per-share
    // price is rounded (e.g. 4.878 × 29.45 = 143.66 but the trade settled
    // 143.68). Recomputing from shares × price would silently change the amount
    // just by opening the row, and Accept would persist the wrong value.
    //
    // Fall back to shares × price ONLY when the cash leg is ~0 — a cash-neutral
    // dividend reinvestment (dividend in, shares bought out) or a buyx/sellx
    // funded by a transfer — where seeding from the cash leg would leave Amount
    // at 0 and block Accept. (The editor's price↔amount link still recomputes
    // if the user actually edits shares or price.)
    const sharesPrice =
        ingestShares != null && ingestUnitPrice != null
            ? Number.parseFloat((ingestShares * ingestUnitPrice).toFixed(2))
            : null;
    // The trade value implied by the REAL cash total (authoritative — it's the
    // actual settled amount, which can differ from shares × price by a cent or
    // two because the per-share price is rounded). The Amount field is the
    // principal/proceeds excluding the fee: a buy pays principal + fee, a sell
    // nets proceeds − fee, so reconstruct the fee-excluded value accordingly.
    const feeAbs = ingestFee != null ? Math.abs(ingestFee) : 0;
    const cashBasedAmount =
        action === 'buy' || action === 'buyx'
            ? Math.abs(cashAmount) - feeAbs
            : action === 'sell' || action === 'sellx'
                ? Math.abs(cashAmount) + feeAbs
                : null; // reinvest is cash-neutral — no meaningful cash total
    // The file's own total, where it stated one (mig 228). This is the answer
    // for a cash-neutral row: a REINVEST's cash leg is zero BY DESIGN — the
    // dividend funds the purchase — so `cashBasedAmount` is null and the code
    // below used to fall through to shares × price. That is precisely the
    // recomputation this function's own comment warns about: it "would silently
    // change the amount just by opening the row, and Accept would persist the
    // wrong value". It did. A file stating 6.584 units at 48.05 totalling
    // 316.37 opened as 316.36, one cent adrift of the settled row beside it.
    //
    // A fee, if any, is excluded the same way a buy's is: the Amount field is
    // the principal, and the fee rides in its own field.
    const ingestBasedAmount =
        ingestAmount != null && Math.abs(ingestAmount) > 0.005
            ? Number.parseFloat((Math.abs(ingestAmount) - feeAbs).toFixed(2))
            : null;

    const amount =
        action !== null && isLinkedAction(action)
            // Prefer the real settled amount when the cash leg carries one, then
            // the file's stated total, and only then shares × price — the last
            // is a RECONSTRUCTION and is kept purely so a row with neither still
            // opens with something rather than 0 (which would block Accept).
            ? (cashBasedAmount != null && cashBasedAmount > 0.005
                ? Number.parseFloat(cashBasedAmount.toFixed(2))
                : ingestBasedAmount
                    ?? sharesPrice
                    ?? amountForAction(cashAmount, action))
            : amountForAction(cashAmount, action);

    // Price is DERIVED metadata = amount ÷ |shares| (ADR-0073), at the
    // register's 6dp display precision so what's stored equals what's shown.
    // For a linked action the authoritative amount is the real total, so the
    // per-share price reconciles to it (e.g. 143.68 ÷ 4.878 = 29.450594),
    // superseding the wire's rounded price — which stays in ingest_unit_price.
    // Falls back to the wire price only when we can't derive (no shares).
    const price =
        action !== null && isLinkedAction(action)
            && ingestShares != null && ingestShares !== 0 && amount != null
            ? Number.parseFloat((amount / Math.abs(ingestShares)).toFixed(6))
            : ingestUnitPrice;

    return {
        brokerageAccountId,
        postedAt: ymdFromIso(header.postedAt),
        transactedAt: seedTransactedAt(header),
        action,
        payee: header.payee ?? '',
        memo: header.memo ?? '',
        checkNumber: header.checkNumber ?? '',
        securityId: ingestSecurityId,
        // Mig 113: prefill share count + per-share price + fee from
        // the OFX wire when the provider extracted them. The
        // orchestrator persists them as ingest_* on txn_headers; the
        // resolved view projects them onto every leg of the header,
        // so any leg the caller hands us carries the same values.
        shares: ingestShares,
        price,
        amount,
        categoryAccountId: null,
        transferAccountId: null,
        // Fee account stays user-picked — the wire only tells us
        // the magnitude, not which expense category the user wants
        // it categorised under.
        feeAccountId: null,
        feeAmount: ingestFee,
        // A feed row can already carry tags — a rule, an earlier merge, or the
        // MCP bulk tool. Upgrading it to investment shape must not drop them.
        tags: header.tags ?? [],
    };
}

function ymdFromIso(iso: string): string {
    // The draft's postedAt field is yyyy-mm-dd (HTML date input).
    // Server projects PostedAt as UTC instant; strip the time portion.
    return iso.slice(0, 10);
}

/**
 * Sign rule for the editor's Amount field per action. Shared between
 * <c>hintToDraft</c> (initial seed) and the editor's action-picker
 * onChange handler (so switching action re-derives sign from the
 * original signed bank amount). Keep the two callers in sync.
 *
 *   buy / sell / buyx / sellx / dividend_reinvest
 *       → |bank amount|. Editor stores non-negative; the per-action
 *         posting code applies the sign.
 *   dividend_cash / transfer / misc / null
 *       → signed bank amount. The user edits the value with sign
 *         discriminating direction (cash in vs out).
 */
export function amountForAction(
    signedBankAmount: number,
    action: LedgerInvestmentAction | null,
): number {
    if (
        action === 'buy' || action === 'sell'
        || action === 'buyx' || action === 'sellx'
        || action === 'dividend_reinvest'
    ) return Math.abs(signedBankAmount);
    return signedBankAmount;
}
