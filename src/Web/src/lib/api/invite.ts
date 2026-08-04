// Invite management endpoints (ADR-0083 slice B). Owner-gated (ledger) / admin-gated
// server-side. The redeem ceremony (begin/complete/accept) lives in lib/auth.ts with
// the other WebAuthn flows.

import type { InviteCreated, PendingInvite } from '../types/invite';
import { request } from './_request';

// ── Owner: invites for their own ledger ──────────────────────────────
export function fetchLedgerInvites(ledgerId: string): Promise<PendingInvite[]> {
    return request<PendingInvite[]>(`/api/ledgers/${encodeURIComponent(ledgerId)}/invites`);
}

export function createLedgerInvite(ledgerId: string, role: string): Promise<InviteCreated> {
    return request<InviteCreated>(`/api/ledgers/${encodeURIComponent(ledgerId)}/invites`, {
        method: 'POST',
        body: { role },
    });
}

export function revokeLedgerInvite(ledgerId: string, inviteId: string): Promise<void> {
    return request<void>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/invites/${encodeURIComponent(inviteId)}`,
        { method: 'DELETE' },
    );
}

// ── Admin: all invites ───────────────────────────────────────────────
export function fetchAdminInvites(): Promise<PendingInvite[]> {
    return request<PendingInvite[]>('/api/admin/invites');
}

export function createAdminInvite(
    body: { ledgerId?: string; role?: string; grantsAdmin: boolean },
): Promise<InviteCreated> {
    return request<InviteCreated>('/api/admin/invites', { method: 'POST', body });
}

export function revokeAdminInvite(inviteId: string): Promise<void> {
    return request<void>(`/api/admin/invites/${encodeURIComponent(inviteId)}`, { method: 'DELETE' });
}
