// Bank-domain transaction types — the manual / bank-feed editor
// surface. Investment-domain transaction types live in
// [./investment.ts] (when A4.c.3 lands).

/**
 * Mirror of API `Coffer.Api.Contracts.TransactionPosting`. One
 * posting in a bank manual create or edit body (ADR-0025). A bank
 * transaction is a list of these. `legId` is meaningful only in
 * PATCH — it asks the server to preserve the existing leg (its
 * counterparty / amount updated to the request values); line memos
 * travel in `notes.lines`. On POST it's ignored.
 *
 * `amount` is the signed source-side amount (negative = outflow).
 * Server writes the paired counterparty leg's amount as `-amount`
 * so the posting sums to zero (ADR-0019), except on a posting between
 * two real accounts in different currencies, whose counterparty leg is
 * `counterpartyAmount` (ADR-0114 D13).
 */
export interface TransactionPosting {
    /** Existing source-side leg id to preserve on PATCH; null /
     *  omitted for new postings. */
    legId?: string | null;
    counterpartyAccountId: string;
    /** Signed source-side amount, in the source account's currency (a category's
     *  line in the real side's), at its places (ADR-0114 D15): a figure with more is
     *  refused with `amount-places-exceed-currency`. */
    amount: number;
    /**
     * The counterparty leg's own amount, signed, in the counterparty account's
     * currency, on a posting between two real accounts in different currencies
     * (ADR-0114 D13). Required there, nonzero and opposite in sign to `amount`
     * (`transaction-posting-counterparty-amount-required`,
     * `transaction-posting-two-amounts-nonzero`, `transaction-posting-two-amounts-sign`),
     * and checked at the counterparty's places. Like the places rule, the nonzero and sign
     * rules check only a pair the write changes: a pair resent equal to its two stored
     * legs, on the same accounts, passes them, so a mirrored zero pair a move or a
     * currency edit left across currencies can still be edited. Omit it on a posting with
     * a category or between accounts in one currency: the server drops it there.
     */
    counterpartyAmount?: number | null;
}

/**
 * Mirror of API `TransactionNotesRequest`: the `notes` member of the bank
 * create and PATCH bodies and of the bank reminder create, edit and fire
 * bodies. It replaces every top-level and per-posting memo and tag field, so a
 * posting carries shape only.
 *
 * PRESENCE decides. An omitted key leaves the value alone; `null`, `''` or `[]`
 * clears it, which on a one-split means "inherit the other side's".
 *
 * `memo` and `tags` are this side's when the transaction ends with one posting
 * and the whole transaction's when it ends with several.
 */
export interface TransactionNotesRequest {
    /** The register the write is made from. Equals `postings.sourceAccountId`
     *  when postings are sent; on a split it becomes the owning account. */
    side: string;
    memo?: string | null;
    tags?: readonly string[];
    /** Per-line notes of a multi-posting result. Refused on a one-posting
     *  result (`line-notes-on-one-posting`). */
    lines?: readonly TransactionLineNotesRequest[];
}

/** Mirror of API `TransactionLineNotesRequest`; same presence rules. */
export interface TransactionLineNotesRequest {
    /** The line's position in the postings sent. */
    index: number;
    memo?: string | null;
    tags?: readonly string[];
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
    /** Short free-text check number (MD's `txn.chk`). */
    checkNumber?: string | null;
    /** ISO-8601 UTC timestamp string. */
    transactedAt?: string | null;
    /** The register's account — every posting's source-side leg
     *  goes here. */
    sourceAccountId: string;
    /** ≥1 posting. */
    postings: readonly TransactionPosting[];
    /** Memo and tags, side-aware (see {@link TransactionNotesRequest}).
     *  Tag names are created on first use, case-insensitively. */
    notes: TransactionNotesRequest;
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
    /* Header fields: PRESENCE decides, not nullness (migration 230).
     * OMIT a key to leave that column alone; send it as `null` to CLEAR
     * the column. "null = leave alone" would make a cleared payee, memo
     * or check number impossible to express — the server would read the
     * null as "no change" and hand the old text back. */
    payee?: string | null;
    checkNumber?: string | null;
    /** ISO-8601 UTC timestamp string — the bank-side posted date. The
     *  column is NOT NULL, so an explicit `null` is a 422, not a clear. */
    postedAt?: string;
    /** ISO-8601 UTC timestamp string — the tax/transaction date. NOT NULL
     *  since mig 189 ("no distinct tax date" is the posted date), so an
     *  explicit `null` is a 422, not a clear. */
    transactedAt?: string;
    /** When supplied, replaces the postings list. */
    postings?: PatchTransactionPostings;
    /** Slice 2c.6a: when `true`, clears `needs_review` on this row
     *  in the same atomic transaction. Replaces the prior dedicated
     *  POST /approve endpoint. May be sent on its own (no other
     *  fields set) to accept a bank-feed row as-is. Idempotent. */
    approve?: boolean;
    /** Memo and tags, side-aware (see {@link TransactionNotesRequest}). The
     *  server refuses a tag set that grows past 20 tags; one that stays the
     *  same size or shrinks is saved even when it is over the limit. */
    notes?: TransactionNotesRequest;
    /** The merge candidate the editor armed. Never sent on the PATCH (the
     *  server ignores it there): `saveBankEdit` routes a body carrying it to
     *  `mergeTransaction` (POST …/merge), which folds THIS header into the
     *  candidate; the candidate survives with its own fields. The server
     *  refuses an invalid candidate (another ledger, not settled, already
     *  folded away, the row itself) with 422 `merge-source-invalid`. */
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
 * concern: one chip of the editor's "Merge candidates" strip for a
 * needs_review bank-feed target. Picking a candidate arms
 * `mergeFromHeaderId`; the save folds the edited row into it through
 * the merge route, and the candidate survives with its own payee,
 * postings and notes. Nothing is copied into the editor. The chip shows
 * the posted date, the payee and the counterparty account of the one
 * posting, or the count of a split's; `origin` names the row in its
 * tooltip. Nothing else is carried, since nothing else is shown.
 */
export interface MergeCandidateDto {
    headerId: string;
    payee: string | null;
    /** ISO-8601 UTC. */
    postedAt: string;
    postings: readonly MergeCandidatePostingDto[];
    /** How the candidate came into the ledger, as its register row's `origin`:
     *  `manual`, `online_import` or `file_import`. The chip's tooltip names the
     *  row a merge keeps by it (mergeChipTitle). */
    origin: string;
}

/**
 * Mirror of API `MergeCandidatePostingDto`. The non-source legs
 * of the candidate, in `posting_index` order — what the chip names
 * (one counterparty, or the split count). The merge keeps them as
 * they are.
 */
export interface MergeCandidatePostingDto {
    counterpartyAccountId: string;
    counterpartyAccountName: string;
}
