// Tag-management endpoints (Tags v1) — the /tags REST surface behind the
// Tags tab of Structure + the shared tag autocomplete. Dictionary admin
// (list-with-usage, create, rename / recolor, merge, delete, cleanup-unused) and
// the read of what one tag labels; assigning tags to a transaction stays on the
// transaction PATCH endpoint (./bank.ts).

import type {
    CleanupTagsResponse,
    CreateTagRequest,
    MergeTagRequest,
    MergeTagResponse,
    PatchTagRequest,
    TagDto,
    TagTransactionFilter,
    TagTransactionPage,
} from '../types/tag';
import { request } from './_request';

/**
 * GET /api/ledgers/{ledgerId}/tags — every tag in the ledger with its
 * assignment count, name-sorted. Source for the management table and the
 * autocomplete list.
 */
export function fetchTags(ledgerId: string): Promise<TagDto[]> {
    return request<TagDto[]>(`/api/ledgers/${encodeURIComponent(ledgerId)}/tags`);
}

/**
 * POST /api/ledgers/{ledgerId}/tags — make a tag, optionally coloured. 201 with the
 * tag as the list reads it; 422 `tag-name-exists` (a name another tag has, case and
 * spacing aside), `transaction-tag-empty`, `transaction-tag-too-long`,
 * `tag-color-invalid`.
 */
export function createTag(ledgerId: string, body: CreateTagRequest): Promise<TagDto> {
    return request<TagDto>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/tags`,
        { method: 'POST', body },
    );
}

/**
 * GET /api/ledgers/{ledgerId}/tags/{tagId}/transactions — the lines this tag is in play
 * for across every account, newest first, one page at a time. 422 `tag-not-found`.
 */
export function fetchTagTransactions(
    ledgerId: string,
    tagId: string,
    filter: TagTransactionFilter,
    page: { limit: number; offset: number },
): Promise<TagTransactionPage> {
    const params = new URLSearchParams({ limit: String(page.limit), offset: String(page.offset) });
    if (filter.accountId) params.set('account_id', filter.accountId);
    if (filter.search) params.set('search', filter.search);
    if (filter.dateFrom) params.set('date_from', filter.dateFrom);
    if (filter.dateTo) params.set('date_to', filter.dateTo);
    return request<TagTransactionPage>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/tags/${encodeURIComponent(tagId)}/transactions?${params.toString()}`,
    );
}

/**
 * PATCH /api/ledgers/{ledgerId}/tags/{tagId} — rename and/or recolor.
 * 204 on success; 422 `tag-name-exists` (rename collided — merge
 * instead), `tag-not-found`, `tag-color-invalid`.
 */
export function patchTag(
    ledgerId: string,
    tagId: string,
    body: PatchTagRequest,
): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/tags/${encodeURIComponent(tagId)}`,
        { method: 'PATCH', body },
    );
}

/**
 * POST /api/ledgers/{ledgerId}/tags/{tagId}/merge — merge this (source)
 * tag into `intoTagId`, repointing every assignment (deduped) and
 * deleting the source. 422 `tag-merge-self` / `tag-not-found`.
 */
export function mergeTag(
    ledgerId: string,
    tagId: string,
    body: MergeTagRequest,
): Promise<MergeTagResponse> {
    return request<MergeTagResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/tags/${encodeURIComponent(tagId)}/merge`,
        { method: 'POST', body },
    );
}

/**
 * DELETE /api/ledgers/{ledgerId}/tags/{tagId} — hard-delete a tag and
 * untag every transaction that carried it (FK cascade). 204; 422
 * `tag-not-found`.
 */
export function deleteTag(ledgerId: string, tagId: string): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/tags/${encodeURIComponent(tagId)}`,
        { method: 'DELETE' },
    );
}

/**
 * DELETE /api/ledgers/{ledgerId}/tags/unused — remove every tag with
 * zero assignments. Returns the count removed.
 */
export function cleanupUnusedTags(ledgerId: string): Promise<CleanupTagsResponse> {
    return request<CleanupTagsResponse>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/tags/unused`,
        { method: 'DELETE' },
    );
}
