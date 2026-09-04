// Reminders read + manage types (ADR-0047 / ADR-0049), mirroring the C#
// DTOs in ReminderDtos.cs (camelCase). DateOnly fields are 'YYYY-MM-DD' strings.

import type { TransactionPosting } from './bank';
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
}

/**
 * An estimated amount, present only on a series that asked for one (mig 220).
 *
 * `amount` non-null means the surface's own amount IS this estimate.
 * `amount` null means the series asked and could not have one — `unavailableReason`
 * says why, and the amount shown is the reminder's template figure.
 */
export interface ReminderEstimate {
    requestedSampleCount: number;
    /** How many occurrences it actually averaged: at most the requested count. */
    availableSampleCount: number;
    amount: number | null;
    /**
     * Stable codes, rendered differently by each surface:
     * 'no-history' | 'zero-estimate' | 'sign-mismatch' | 'has-split' |
     * 'loan-driven' | 'not-bank-shape' | 'no-source-account'
     */
    unavailableReason: string | null;
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
}

export type ReminderKind = 'bank' | 'investment';

/** One template leg in a reminder detail (bank fields + investment metadata). */
export interface ReminderLegDto {
    accountId: string;
    accountName: string;
    postingIndex: number;
    amount: number;
    legMemo: string | null;
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

export interface FireReminderRequest {
    occurrenceDate: string;
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
    memo?: string | null;
    checkNumber?: string | null;
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
    memo?: string | null;
    checkNumber?: string | null;
    sourceAccountId: string;
    postings: readonly TransactionPosting[];
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
    memo?: string | null;
    checkNumber?: string | null;
    postings?: PatchReminderPostings | null;
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
