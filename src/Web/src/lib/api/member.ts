// Ledger membership endpoints (ADR-0083). Owner-gated mutations server-side.

import type { LedgerMember } from '../types/member';
import { request } from './_request';

/** GET /api/ledgers/{id}/members — every member of the ledger with their role. */
export function fetchLedgerMembers(ledgerId: string): Promise<LedgerMember[]> {
    return request<LedgerMember[]>(`/api/ledgers/${encodeURIComponent(ledgerId)}/members`);
}

/** PUT /api/ledgers/{id}/members/{userId} — change a member's role (owner-only). */
export function setLedgerMemberRole(ledgerId: string, userId: string, role: string): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/members/${encodeURIComponent(userId)}`,
        { method: 'PUT', body: { role } },
    );
}

/** DELETE /api/ledgers/{id}/members/{userId} — remove a member (owner-only). */
export function removeLedgerMember(ledgerId: string, userId: string): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/members/${encodeURIComponent(userId)}`,
        { method: 'DELETE' },
    );
}
