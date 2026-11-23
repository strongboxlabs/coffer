// Tag-management API types (Tags v1). The per-ledger tag dictionary
// (`tags` table; assignments in `txn_header_tags` and `txn_leg_tags`,
// what a row shows in `txn_leg_note_tags`). Assigning tags goes through
// the bank write's `notes` (./bank.ts), the investment write's `tags`
// field, or MCP's `set_transaction_tags` with an `accountId`; these are
// the dictionary-admin shapes for the Tags panel + the shared
// autocomplete.

/**
 * Mirror of API `Coffer.Api.Contracts.TagDto`. One tag with its
 * assignment count (header plus leg pairings) and the number of transactions
 * it is in play on. `color` is a `#rrggbb` hex, or `null` for the default gray.
 */
export interface TagDto {
    id: string;
    name: string;
    color: string | null;
    /** Pairings: a transaction tagged on itself and on a posting counts twice, and zero
     *  means nothing points at the tag (so it is safe to remove). */
    usageCount: number;
    /** Distinct transactions the tag is in play on a line of: the lines a register shows it
     *  on, which is what the tag's own page lists. A line with no tags of its own shows its
     *  category side's, so those count; a tag a line's own tags hide, and hidden or
     *  merged-away rows and reminder templates, do not. The number a reader means by "how
     *  many". */
    transactionCount: number;
}

/** Body of `POST /api/ledgers/{ledgerId}/tags`. A tag with no colour is drawn gray. */
export interface CreateTagRequest {
    name: string;
    color?: string;
}

/**
 * One line a tag is in play for. Mirror of the API `TransactionLine`: a line is what an
 * account's register shows, so a transfer tagged on both sides is a line in each account.
 */
export interface TagTransactionLine {
    headerId: string;
    postedAt: string;
    payee: string | null;
    accountId: string;
    accountName: string;
    counterpartyAccountId: string | null;
    /** The other side of the posting: the category for a money line. Full path. */
    counterpartyAccountName: string | null;
    amount: number;
    memo: string | null;
    status: string;
    /** The transaction's own memo, on a split's own lines only. */
    transactionMemo: string | null;
    /** Every tag in play for the line, this one included. */
    tags: string[];
    /** The line's own leg: unique per line, where a split's lines share the header
     *  and the account, so it is what names a line. */
    legId: string;
    /** The currency `amount` is in: the line's account's. Lines in different
     *  currencies sit on one page and do not sum. */
    currencyCode: string;
}

/** A page of a tag's lines; `hasMore` says whether another follows at `offset + limit`. */
export interface TagTransactionPage {
    lines: TagTransactionLine[];
    limit: number;
    offset: number;
    hasMore: boolean;
}

/** Narrowing for a tag's lines. Dates are calendar days (`yyyy-MM-dd`), inclusive at both ends. */
export interface TagTransactionFilter {
    accountId?: string;
    search?: string;
    dateFrom?: string;
    dateTo?: string;
}

/** Body of `PATCH /api/ledgers/{ledgerId}/tags/{tagId}`. Both optional;
 *  an omitted field is left unchanged. Renaming to a name another tag
 *  already carries (case-insensitive) returns `tag-name-exists`. */
export interface PatchTagRequest {
    name?: string;
    color?: string;
}

/** Body of `POST /api/ledgers/{ledgerId}/tags/{tagId}/merge`. */
export interface MergeTagRequest {
    intoTagId: string;
}

/** Response of the merge endpoint. Mirror of API `IMergeTagResponseNotes`. */
export interface MergeTagResponse {
    /** Tag assignments moved onto the surviving tag. */
    transactionsRepointed: number;
    /** Saved reports now filtered on the surviving tag that the merging user
     *  can see: their own and the ledger-shared ones. Another member's private
     *  report is repointed too but never counted here. */
    savedReportsRepointed: number;
}

/** Response of `DELETE .../tags/unused` — how many orphan tags removed. */
export interface CleanupTagsResponse {
    tagsRemoved: number;
}
