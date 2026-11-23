// Reminders read + manage types (ADR-0047 / ADR-0049), mirroring the C#
// DTOs in ReminderDtos.cs (camelCase). DateOnly fields are 'YYYY-MM-DD' strings.

import type { TransactionNotesRequest, TransactionPosting } from './bank';
import type { RateUnavailable } from './conversion';
import type { CreateInvestmentTransactionRequest } from './investment';

/** One recurring-reminder series for the management list / agenda. */
export interface ReminderSummary {
    id: string;
    payee: string | null;
    memo: string | null;
    /** Source-side net (cash impact on the originating account; negative =
     * outflow). The figure the agenda shows. */
    amount: number;
    /** Raw RFC-5545 rule; humanize with `humanizeRrule`. Null on a custom
     * (manual-fire) or not-yet-re-imported series. */
    rrule: string | null;
    startDate: string;
    endDate: string | null;
    nextDueDate: string | null;
    autoCommitDaysBefore: number | null;
    isActive: boolean;
    isLoanReminder: boolean;
    origin: string;
    /** Mig 220: the series' estimate, when it asked for one. */
    estimate?: ReminderEstimate | null;
    /** The currency `amount` is in: the series' source account's. Null for a series
     *  with no single source, whose amount is in no currency. */
    currencyCode?: string | null;
}

/**
 * An estimated amount, present only on a series that asked for one (mig 220).
 *
 * `amount` non-null means the surface's own amount IS this estimate.
 * `amount` null means the series asked and could not have one — `unavailableReason`
 * says why, and the amount shown is the reminder's template figure, which stands in
 * for the estimate and is marked as one all the same (`isEstimating`).
 *
 * On a transfer between real accounts in different currencies the other side is
 * estimated too (ADR-0114 D13), per occurrence: `counterpartyAmount` is `amount` at the
 * derived rate for the occurrence's due date, rounded once at the other account's
 * places. With no rate for that day there is no estimate: `amount` is null, the reason
 * is `rate-unavailable` and `rateUnavailable` names each missing stored leg.
 */
export interface ReminderEstimate {
    requestedSampleCount: number;
    /** How many occurrences it actually averaged: at most the requested count. */
    availableSampleCount: number;
    amount: number | null;
    /**
     * Stable codes, rendered differently by each surface:
     * 'no-history' | 'zero-estimate' | 'sign-mismatch' | 'has-split' |
     * 'loan-driven' | 'not-bank-shape' | 'no-source-account' | 'rate-unavailable'
     */
    unavailableReason: string | null;
    /** The other side's estimate, signed, in the other account's currency; null when the
     *  series is not a transfer between currencies, or `amount` is null. */
    counterpartyAmount?: number | null;
    /** On `rate-unavailable`, each missing stored leg (account currency into the
     *  reporting currency) and the due date; absent otherwise. */
    rateUnavailable?: RateUnavailable[];
}

export type UpcomingKind = 'scheduled' | 'reminder' | 'skipped';

/** One entry in the upcoming agenda/calendar window. `scheduled` = already
 * fired (a committed header, `headerId` set); `skipped` = a skipped/catch-up
 * slot (read-only trail); `reminder` = an un-fired, actionable slot. */
export interface UpcomingOccurrence {
    date: string;
    kind: UpcomingKind;
    reminderId: string;
    headerId: string | null;
    payee: string | null;
    memo: string | null;
    /** Source-side net of the occurrence (signed). */
    amount: number;
    /** Series' next-due cursor ('YYYY-MM-DD' | null). When it's earlier than
     * this occurrence's date, acting will catch-up (skip) earlier occurrences —
     * the form warns inline (ADR-0047 §9.2). */
    seriesNextDue: string | null;
    /** Mig 220. Null on a FIRED occurrence: that carries the real committed figure. */
    estimate?: ReminderEstimate | null;
    /** The series' source account, its name and currency carried here so a
     *  deactivated source still has both. All three are null for a series with no
     *  single source. */
    sourceAccountId?: string | null;
    sourceAccountName?: string | null;
    currencyCode?: string | null;
    /** Null when the series has no auto-post days; otherwise whether the auto-post
     *  job posts its occurrences, by the job's own rule. */
    autoPost?: UpcomingAutoPost | null;
}

/** Why the auto-post job leaves a series' occurrences for a person to post.
 *  `loan-across-currencies`: a managed loan payment whose paying account, or real
 *  escrow account, is in another currency than the loan's, so it can never post
 *  (ADR-0114 D13). `template-amount-does-not-fit`: a figure the fire would write has
 *  more places than its currency uses, so it is refused until the reminder or the
 *  loan terms are edited (ADR-0114 D15). Mirror of API `AutoPostExclusion`. */
export type AutoPostExcludedReason =
    | 'investment'
    | 'no-lines'
    | 'inactive-account'
    | 'loan-across-currencies'
    | 'template-amount-does-not-fit';

export interface UpcomingAutoPost {
    /** Null when the job posts the series' occurrences. */
    excludedReason: AutoPostExcludedReason | null;
}

export type ReminderKind = 'bank' | 'investment';

/** One template leg in a reminder detail (bank fields + investment metadata). */
export interface ReminderLegDto {
    /** The template leg's own id. An edit names each stored line by its
     *  source leg's id, which is what ties a rebuilt line to the stored one
     *  and keeps both of its sides' memo and tags with it. */
    legId: string;
    accountId: string;
    accountName: string;
    postingIndex: number;
    amount: number;
    legMemo: string | null;
    /** The leg's own tags. Every leg carries its set; the editor seeds and
     *  edits only the source side's. Always an array. */
    legTags: string[];
    securityId: string | null;
    securityTicker: string | null;
    quantity: number | null;
    unitPrice: number | null;
    postingRole: string | null;
}

/** Per-series detail: recurrence metadata + the template's legs. */
export interface ReminderDetail {
    id: string;
    kind: ReminderKind;
    payee: string | null;
    memo: string | null;
    /** The template's transaction-level tags, from the source side: the
     *  transaction's own set where it is in play (a split, a multi-posting
     *  event), else the source side's. The editor opens with them, so a
     *  picked tag adds to the stored set rather than replacing it. Always an
     *  array. */
    tags: string[];
    checkNumber: string | null;
    action: string | null;
    rrule: string | null;
    startDate: string;
    endDate: string | null;
    nextDueDate: string | null;
    autoCommitDaysBefore: number | null;
    isActive: boolean;
    isLoanReminder: boolean;
    origin: string;
    /** Mig 220: the series' estimate, when it asked for one. */
    estimate?: ReminderEstimate | null;
    /** Originating account (mig 125): the bank editor's source / the investment
     * brokerage. Null on a custom / pre-125 series. */
    sourceAccountId: string | null;
    legs: ReminderLegDto[];
}

export interface SetReminderActiveRequest {
    active: boolean;
}

export interface SkipReminderRequest {
    occurrenceDate: string;
}

export interface SkipReminderResponse {
    occurrenceDate: string;
    nextDueDate: string | null;
    /** Catch-up (ADR-0047 §9.2): earlier un-acted occurrences this skip also
     * marked skipped, and the earliest of them ('YYYY-MM-DD' or null). */
    skippedEarlierCount: number;
    skippedEarlierFrom: string | null;
}

/**
 * DELETE /reminders/{id}/skip.
 *
 * No cascade count, unlike SkipReminderResponse: un-skip touches exactly the slot
 * asked for. Skipping sweeps earlier un-acted occurrences, and undoing that sweep
 * would mean guessing which of them the user wanted back — each is independently
 * un-skippable instead.
 */
export interface UnskipReminderResponse {
    occurrenceDate: string;
    /** May have moved EARLIER: the restored slot can now be the earliest un-acted one. */
    nextDueDate: string | null;
}

export interface FireReminderResponse {
    headerId: string;
    /** Catch-up (ADR-0047 §9.2): earlier un-acted occurrences this fire also
     * marked skipped, and the earliest of them. */
    skippedEarlierCount: number;
    skippedEarlierFrom: string | null;
}

/** Body for POST /reminders/{id}/fire/bank — adjust-at-post for a BANK series:
 * the EDITED transaction (one source + N postings, incl. splits). Mirrors C#
 * FireBankReminderRequest. */
export interface FireBankReminderRequest {
    occurrenceDate: string;
    sourceAccountId: string;
    postings: readonly TransactionPosting[];
    payee?: string | null;
    checkNumber?: string | null;
    /** Memo and tags of the posted occurrence, side-aware. */
    notes: TransactionNotesRequest;
    /** 'YYYY-MM-DD'; null = the occurrence date. */
    postedDate?: string | null;
}

/** Body for POST /reminders/{id}/fire/investment — adjust-at-post for an
 * INVESTMENT series. Mirrors C# FireInvestmentReminderRequest. */
export interface FireInvestmentReminderRequest {
    occurrenceDate: string;
    transaction: CreateInvestmentTransactionRequest;
}

// ---------------------------------------------------------------------------
// Authoring: create / edit a reminder series (ADR-0051 slice B). Mirrors the
// C# CreateReminderRequest / CreateInvestmentReminderRequest /
// EditReminderRequest / EditInvestmentReminderRequest. The kind is derived from
// the source account, so there's no kind field — the caller picks the endpoint.
// ---------------------------------------------------------------------------

/** Body for POST /reminders — create a BANK-shape reminder series. */
export interface CreateReminderRequest {
    rrule: string;
    /** 'YYYY-MM-DD'. */
    startDate: string;
    endDate?: string | null;
    /** null = manual approve; N >= 0 = auto-commit N days before due. */
    autoCommitDaysBefore?: number | null;
    /** Mig 220. 1-24, or null to leave alone. Single-posting non-loan only. */
    estimateSampleCount?: number | null;
    /** Explicit clear, because a null estimateSampleCount means 'unchanged'. */
    clearEstimate?: boolean;
    payee?: string | null;
    checkNumber?: string | null;
    sourceAccountId: string;
    postings: readonly TransactionPosting[];
    /** The template's memo and tags, side-aware. */
    notes: TransactionNotesRequest;
}

/** Body for POST /reminders/investment — create an INVESTMENT-shape series.
 * The embedded transaction's postedAt is ignored (derived from startDate). */
export interface CreateInvestmentReminderRequest {
    rrule: string;
    startDate: string;
    endDate?: string | null;
    autoCommitDaysBefore?: number | null;
    /** Mig 220. 1-24, or null to leave alone. Single-posting non-loan only. */
    estimateSampleCount?: number | null;
    /** Explicit clear, because a null estimateSampleCount means 'unchanged'. */
    clearEstimate?: boolean;
    transaction: CreateInvestmentTransactionRequest;
}

/** Replace-all postings sub-shape for {@link EditReminderRequest}. */
export interface PatchReminderPostings {
    sourceAccountId: string;
    items: readonly TransactionPosting[];
}

/** Body for PATCH /reminders/{id} — edit a BANK series. PARTIAL: omit a scalar
 * to leave it unchanged; use clearEndDate / clearAutoCommit to null those.
 * `postings`, when present, replaces the template legs wholesale. */
export interface EditReminderRequest {
    rrule?: string | null;
    startDate?: string | null;
    clearEndDate?: boolean;
    endDate?: string | null;
    clearAutoCommit?: boolean;
    autoCommitDaysBefore?: number | null;
    /** Mig 220. 1-24, or null to leave alone. Single-posting non-loan only. */
    estimateSampleCount?: number | null;
    /** Explicit clear, because a null estimateSampleCount means 'unchanged'. */
    clearEstimate?: boolean;
    payee?: string | null;
    checkNumber?: string | null;
    postings?: PatchReminderPostings | null;
    /** The template's memo and tags; omitted keys are left as they are. */
    notes?: TransactionNotesRequest;
}

/** Body for PATCH /reminders/{id}/investment — edit an INVESTMENT series.
 * PARTIAL on the recurrence scalars; `transaction`, when present, replaces the
 * template shape wholesale. */
export interface EditInvestmentReminderRequest {
    rrule?: string | null;
    startDate?: string | null;
    clearEndDate?: boolean;
    endDate?: string | null;
    clearAutoCommit?: boolean;
    autoCommitDaysBefore?: number | null;
    /** Mig 220. 1-24, or null to leave alone. Single-posting non-loan only. */
    estimateSampleCount?: number | null;
    /** Explicit clear, because a null estimateSampleCount means 'unchanged'. */
    clearEstimate?: boolean;
    transaction?: CreateInvestmentTransactionRequest | null;
}
