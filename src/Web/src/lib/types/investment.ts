// Investment-domain transaction types — A4.c.3 editor + endpoint
// surface (ADR-0029). Universal register types live in
// [./register.ts]; bank-domain editor types in [./bank.ts].

/**
 * One of the investment actions: the 9 ADR-0027 catalog values plus
 * `transfer_shares` (in-kind share move between two investment
 * accounts — ADR-0065). The editor's action picker
 * (`InvestmentTxnRowEdit`) exposes these as user-facing entries;
 * direction for `misc` and `transfer` is sign-discriminated (no
 * separate picker entries per ADR-0029).
 */
export type LedgerInvestmentAction =
    | 'buy' | 'buyx' | 'sell' | 'sellx'
    | 'dividend_cash' | 'dividend_reinvest' | 'divx'
    | 'transfer' | 'misc'
    // In-kind share move between two investment accounts (ADR-0065):
    // moves FIFO lots + cost basis, zero realized gain.
    | 'transfer_shares';

/**
 * The actions whose transfer account is a real account that may be in another currency,
 * so that across currencies they carry `transferAmount` (ADR-0114 D13). Mirror of the
 * API's `LedgerActions.Transfer`, `BuyXfr`, `SellXfr` and `DivXfr`; typed against
 * {@link LedgerInvestmentAction}, so a name not on the wire does not compile.
 * `transfer_shares` moves shares in kind and carries no second amount.
 */
export const TRANSFER_AMOUNT_ACTIONS: readonly LedgerInvestmentAction[] = ['transfer', 'buyx', 'sellx', 'divx'];

/**
 * Mirror of API `Coffer.Api.Contracts.CreateInvestmentTransactionRequest`
 * (ADR-0029). One investment txn — multi-posting under the hood, but
 * the wire shape speaks the user-facing field set (action × required
 * fields). Server validates against the action × field matrix.
 */
export interface CreateInvestmentTransactionRequest {
    brokerageAccountId: string;
    /** ISO-8601 UTC timestamp string. */
    postedAt: string;
    action: LedgerInvestmentAction;
    payee?: string | null;
    /** The event's memo. The server puts it on the brokerage leg of a
     *  one-posting event and on the transaction of a multi-posting one. */
    memo?: string | null;
    checkNumber?: string | null;
    /** ISO-8601 UTC timestamp string. */
    transactedAt?: string | null;
    securityId?: string | null;
    shares?: number | null;
    price?: number | null;
    amount?: number | null;
    categoryAccountId?: string | null;
    transferAccountId?: string | null;
    /**
     * The transfer account's own leg, signed, in that account's currency, when
     * `transferAccountId` is a real account in another currency than the brokerage's
     * (ADR-0114 D13), on the actions in {@link TRANSFER_AMOUNT_ACTIONS}; on `buyx`,
     * `sellx` and `divx` it pairs with the trade amount, not the fee. Required there,
     * nonzero and opposite in sign to the brokerage's leg of the transfer pair
     * (`investment-txn-transfer-amount-required`,
     * `investment-txn-transfer-amounts-nonzero`, `investment-txn-transfer-amounts-sign`),
     * and checked at the transfer account's places. Like the places rule, the nonzero
     * and sign rules check only a pair the write changes: a pair resent equal to its two
     * stored legs, on the same accounts, passes them. Omit it on a category or a
     * same-currency transfer: the server drops it there.
     */
    transferAmount?: number | null;
    feeAccountId?: string | null;
    feeAmount?: number | null;
    /** ADR-0031 Phase 3d.2 — mirrors PATCH; see PatchInvestmentTransactionRequest. */
    providerSecurityHint?: ProviderSecurityHint | null;
    /**
     * Tags to attach to the new header — same field and semantics as the bank
     * create surface. Case-insensitive create-on-first-use against the ledger's
     * tag dictionary; omitted / `[]` both produce an untagged transaction.
     *
     * Per ADR-0009 a tag belongs to the EVENT, so this is header-level on an
     * investment txn exactly as it is on a bank split — the legs, however many
     * the action derives, do not each carry their own.
     */
    tags?: readonly string[] | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.PatchInvestmentTransactionRequest`.
 * Same shape; PATCH semantics per ADR-0025 are "supplied set IS the
 * new state" — null on a field means null in the saved state, not
 * "leave alone."
 */
export interface PatchInvestmentTransactionRequest {
    brokerageAccountId?: string | null;
    postedAt?: string | null;
    action?: LedgerInvestmentAction | null;
    payee?: string | null;
    /** PRESENCE decides, unlike the fields around it: an omitted memo is left
     *  as it is, `null` or blank clears it (mirror of API
     *  `IInvestmentWriteNotes`). */
    memo?: string | null;
    checkNumber?: string | null;
    transactedAt?: string | null;
    securityId?: string | null;
    shares?: number | null;
    price?: number | null;
    amount?: number | null;
    categoryAccountId?: string | null;
    transferAccountId?: string | null;
    /** The transfer account's own leg across currencies, as on create. A PATCH that
     *  resends the stored leg unchanged passes the places check, and one that resends
     *  the transfer pair equal to its two stored legs, on the same accounts, passes the
     *  nonzero and sign checks too. */
    transferAmount?: number | null;
    feeAccountId?: string | null;
    feeAmount?: number | null;
    /**
     * ADR-0031 Phase 3d.2: optional provider-mapping hint. Mirror of
     * `Coffer.Api.Contracts.ProviderSecurityHint`. When supplied
     * alongside a resolved `securityId`, the server records
     * `(ledger, providerKey, providerSecurityId) → securityId` in
     * `provider_security_mappings` so the next sync of the same
     * ticker auto-resolves without prompting.
     */
    providerSecurityHint?: ProviderSecurityHint | null;
    /** When `true`, clears `needsReview` on this transaction in the same
     *  transaction as the edit — mirrors the bank PATCH. Omit it to save
     *  changes and leave the row queued for review. */
    approve?: boolean;
    /**
     * Replace the event's tag set in the same PATCH. Like `memo`, tags follow
     * the PRESENCE rule rather than ADR-0025's "supplied set IS the new state":
     * omitted or `null` leaves them alone, `[]` clears them. The editor sends
     * them only when the user changed them.
     */
    tags?: readonly string[] | null;

    /**
     * The merge candidate the editor armed (mirrors bank `mergeFromHeaderId`).
     * Never sent on the PATCH (the server ignores it there): the editor's save
     * routes a body carrying it to `mergeInvestmentTransaction` (POST …/merge),
     * which folds the edited row into the candidate; the candidate survives
     * with its own fields. See `InvestmentMergeCandidate`.
     */
    mergeFromHeaderId?: string | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.InvestmentMergeCandidateDto`. One
 * "possible match" surfaced in the editor's merge panel; picking one folds
 * the edited (fresh, needs-review) row into it. Shaped for a one-line chip
 * that reads like the candidate's register row (date · action · payee ·
 * ticker · shares · amount).
 */
export interface InvestmentMergeCandidate {
    headerId: string;
    /** ISO-8601 UTC timestamp string (effective, override-aware). */
    postedAt: string;
    /** Signed day offset from the edited row (chip subtitle). */
    dayDelta: number;
    action: LedgerInvestmentAction | null;
    /** The action the candidate's register row shows: same meaning and values
     *  as the register row's `derivedAction` (`Xfr` on a cash transfer that
     *  carries no header action). */
    derivedAction: string | null;
    securityTicker: string | null;
    /** The candidate's register row's quantity in the edited brokerage's
     *  register: same sign and meaning. */
    shares: number | null;
    unitPrice: number | null;
    /** The candidate's register row's amount in the edited brokerage's
     *  register: same sign and meaning. */
    amount: number;
    payee: string | null;
    /** A cash transfer's other account, same meaning as on a register row;
     *  null otherwise. */
    counterpartyAccountId: string | null;
    counterpartyAccountName: string | null;
    /** How the candidate came into the ledger, as its register row's `origin`:
     *  `manual`, `online_import` or `file_import`. The chip's tooltip names the
     *  row a merge keeps by it (mergeChipTitle). */
    origin: string;
}

/** Mirror of API `Coffer.Api.Contracts.ProviderSecurityHint`. */
export interface ProviderSecurityHint {
    providerKey: string;
    providerSecurityId: string;
}

/** Mirror of API `CreateInvestmentTransactionResponse`. */
export interface CreateInvestmentTransactionResponse {
    headerId: string;
}

/**
 * Mirror of API `InvestmentLotDto` — one open lot for the editor's
 * FIFO consumption preview (ADR-0029). Used by A4.c.4 (preview
 * popover); included here so the API client signature is complete.
 */
export interface InvestmentLotDto {
    lotId: string;
    /** ISO-8601 UTC timestamp string. */
    acquiredAt: string;
    quantity: number;
    unitCost: number;
}
