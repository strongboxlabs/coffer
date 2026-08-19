// Bank-domain transaction types — the manual / bank-feed editor
// surface. Investment-domain transaction types live in
// [./investment.ts] (when A4.c.3 lands).

/**
 * Mirror of API `Coffer.Api.Contracts.TransactionPosting`. One
 * posting in a bank manual create or edit body (ADR-0025). A bank
 * transaction is a list of these. `legId` is meaningful only in
 * PATCH — it asks the server to preserve the existing leg (its
 * counterparty / amount / memo updated to the request values). On
 * POST it's ignored.
 *
 * `amount` is the signed source-side amount (negative = outflow).
 * Server writes the paired counterparty leg's amount as `-amount`
 * so the posting sums to zero (ADR-0019).
 */
export interface TransactionPosting {
    /** Existing source-side leg id to preserve on PATCH; null /
     *  omitted for new postings. */
    legId?: string | null;
    counterpartyAccountId: string;
    /** Signed source-side amount; must be non-zero. */
    amount: number;
    /** Optional per-posting memo (MD's `split.desc`). */
    legMemo?: string | null;
}

/**
 * Mirror of API `Coffer.Api.Contracts.CreateTransactionRequest`
 * (ADR-0025). Body for `POST /api/ledgers/{ledgerId}/transactions`
 * — create a manual bank-shape transaction with one or more
 * postings. `postings.length === 1` makes a single-row;
 * `> 1` makes a multi-split. Same endpoint either way.
 */
export interface CreateTransactionRequest {
    /** ISO-8601 UTC timestamp string. */
    postedAt: string;
    payee?: string | null;
    memo?: string | null;
    /** Short free-text check number (MD's `txn.chk`). */
    checkNumber?: string | null;
    /** ISO-8601 UTC timestamp string. */
    transactedAt?: string | null;
    /** The register's account — every posting's source-side leg
     *  goes here. */
    sourceAccountId: string;
    /** ≥1 posting. */
    postings: readonly TransactionPosting[];
    /** Slice 2c.6b: tags to attach to the new transaction. Same
     *  case-insensitive create-on-first-use semantics as the PATCH
     *  surface; omitted or empty list ⇒ no tags. */
    tags?: readonly string[];
}

/**
 * Mirror of API `Coffer.Api.Contracts.PatchTransactionPostings`.
 * The postings sub-shape of a bank PATCH body. When present,
 * replaces the header's postings list wholesale per ADR-0025
 * reconcile rules: items with a matching `legId` are preserved
 * (with the supplied fields applied), items without `legId` become
 * new postings, existing legs not referenced are deleted, and the
 * new `posting_index` follows the order of `items[]`.
 */
export interface PatchTransactionPostings {
    sourceAccountId: string;
    items: readonly TransactionPosting[];
}

/**
 * Mirror of API `Coffer.Api.Contracts.PatchTransactionRequest`
 * (ADR-0025). A single user "Save" on a bank register row maps to
 * one PATCH that can apply any subset of header fields plus an
 * optional full postings reshape — all in one atomic Postgres
 * transaction.
 *
 * `null` (or omitted) on a header field means "leave the override
 * column alone." `postings` omitted means "don't touch the
 * postings list."
 */
export interface PatchTransactionRequest {
    payee?: string | null;
    memo?: string | null;
    /** Check-number override — goes through `txn_header_overrides`
     *  per ADR-0003 (same layer as Payee / Memo). */
    checkNumber?: string | null;
    /** ISO-8601 UTC timestamp string — the bank-side posted date. */
    postedAt?: string | null;
    /** ISO-8601 UTC timestamp string — the tax/transaction date. */
    transactedAt?: string | null;
    /** When supplied, replaces the postings list. */
    postings?: PatchTransactionPostings;
    /** Slice 2c.6a: when `true`, clears `needs_review` on this row
     *  in the same atomic transaction. Replaces the prior dedicated
     *  POST /approve endpoint. May be sent on its own (no other
     *  fields set) to accept a bank-feed row as-is. Idempotent. */
    approve?: boolean;
    /** Slice 2c.6b: replace the tag set on this header.
     *  - `undefined` / omitted → leave tags untouched.
     *  - `[]` → clear all tags.
     *  - `[...]` → set membership exactly matches this list; tag
     *    names that aren't yet in the ledger's dictionary are
     *    created on first use. Case-insensitive within the ledger;
     *    the first user-supplied casing is preserved on insert.
     *  Server enforces ≤20 tags / ≤64 chars / no empty names. */
    tags?: readonly string[];
    /** Slice 2c.6d: stamps the supplied manual row as merged
     *  into this header (loser → winner). The PATCH body's
     *  other fields define what the winning row ends up with —
     *  the editor pre-fills them from the candidate when the
     *  user clicks a "Possible match" chip. Server rejects
     *  invalid sources (cross-ledger / non-manual / already
     *  merged / self) with 422 `merge-source-invalid`. */
    mergeFromHeaderId?: string | null;
}

/**
 * Mirror of API `SimilarPayeeDto` (slice 2c.6c — Tier 1 recall).
 * Bank-feed editor concern: one suggestion the editor renders as a
 * clickable chip on a needs_review bank-feed row. Clicking pre-fills
 * the payee field and counterparty picker with this pair so the
 * user can categorize a recurring bank charge in one click. Only
 * fires on bank-shape rows (the `needs_review` flag is bank-only).
 *
 * Sort order from the server: `useCount` DESC, then `lastUsedAt`
 * DESC. The SPA renders the first 1-5 verbatim.
 */
export interface SimilarPayeeDto {
    /** The resolved payee text from prior approved rows
     *  (`override.payee` falling back to the raw bank payee). */
    payee: string;
    /** Counterparty leg's account id on the prior rows — a category
     *  on an ordinary expense, a real account when the user settled
     *  those rows as transfers. Drives the editor's
     *  AccountCategoryPicker on chip click, which takes either. */
    counterpartyAccountId: string;
    counterpartyAccountName: string;
    /** How many prior approved bank rows used this `(payee,
     *  counterparty)` pair. Displayed as `(×N)` on the chip when
     *  >1. */
    useCount: number;
    /** ISO-8601 UTC. Tie-breaker on sort; not surfaced in the
     *  chip UI today but available for future "most-recent" hint. */
    lastUsedAt: string;
}

/**
 * Mirror of API `MergeCandidateDto` (slice 2c.6d). Bank-feed editor
 * concern: one row of the editor's "Possible matches" panel for a
 * needs_review bank-feed target. Clicking a candidate pre-fills the
 * editor with this row's header-level fields + posting structure;
 * submit arms `mergeFromHeaderId` so the server stamps
 * `is_merged_into` on the loser in the same PATCH transaction.
 */
export interface MergeCandidateDto {
    headerId: string;
    payee: string | null;
    memo: string | null;
    /** ISO-8601 UTC. */
    postedAt: string;
    /** Signed delta `candidate.postedAt − target.postedAt` in
     *  whole days. Negative when the candidate is older. */
    daysDelta: number;
    tags: readonly string[];
    postings: readonly MergeCandidatePostingDto[];
}

/**
 * Mirror of API `MergeCandidatePostingDto`. The non-source legs
 * of the candidate, in `posting_index` order — what the editor
 * loads into its posting drafts when the user picks a candidate.
 */
export interface MergeCandidatePostingDto {
    counterpartyAccountId: string;
    counterpartyAccountName: string;
    /** Signed counterparty amount. The editor's draft uses the
     *  source-side amount (negated) — we pass through verbatim
     *  and let the pre-fill helper invert. */
    amount: number;
    legMemo: string | null;
}
