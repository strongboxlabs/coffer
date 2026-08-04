// Invite types (ADR-0083 slice B).

/** A pending (unconsumed, unexpired) invite — mirror of API `InvitesRepository.PendingInvite`. */
export interface PendingInvite {
    id: string;
    ledgerId: string | null;
    ledgerName: string | null;
    role: string | null;
    grantsAdmin: boolean;
    createdAt: string;
    expiresAt: string;
}

/** Mirror of API `InviteCreatedResponse` — the one-time link token + expiry. */
export interface InviteCreated {
    token: string;
    expiresAt: string;
}

/** Mirror of API `InvitePreviewResponse` — what an invite link confers. */
export interface InvitePreview {
    ledgerName: string | null;
    role: string | null;
    grantsAdmin: boolean;
}
