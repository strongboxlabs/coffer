// Tag-management API types (Tags v1). The per-ledger tag dictionary
// (`tags` table + `txn_header_tags` junction). Assigning tags to a
// transaction stays on the transaction PATCH endpoint (./bank.ts);
// these are the dictionary-admin shapes for the Tags panel + the shared
// autocomplete.

/**
 * Mirror of API `Coffer.Api.Contracts.TagDto`. One tag with its
 * assignment count (header pairings). `color` is a `#rrggbb` hex, or
 * `null` for the default gray.
 */
export interface TagDto {
    id: string;
    name: string;
    color: string | null;
    usageCount: number;
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

/** Response of the merge endpoint — how many assignments were repointed. */
export interface MergeTagResponse {
    transactionsRepointed: number;
}

/** Response of `DELETE .../tags/unused` — how many orphan tags removed. */
export interface CleanupTagsResponse {
    tagsRemoved: number;
}
