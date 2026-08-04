import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    fetchLedgerMembers,
    setLedgerMemberRole,
    removeLedgerMember,
    fetchVisibleLedgers,
    fetchLedgerInvites,
    createLedgerInvite,
    revokeLedgerInvite,
    type LedgerMember,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { InviteLinkModal } from '@/components/invites/InviteLinkModal';
import { PendingInvitesList } from '@/components/invites/PendingInvitesList';

const ROLE_OPTIONS = [
    { value: 'owner', label: 'Owner' },
    { value: 'editor', label: 'Editor' },
    { value: 'viewer', label: 'Viewer' },
] as const;

/**
 * Ledger → Settings → Members (ADR-0083): who can access this ledger and at what role
 * (owner / editor / viewer). Owners change roles, remove members, and invite new people
 * via a one-time link; everyone else sees the list read-only. A ledger must keep ≥1
 * owner, so the sole owner's controls are locked (the API enforces it too). The
 * endpoints are the authority — this is UX.
 */
export function MembersPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const membersKey = ['ledger-members', ledgerId] as const;
    const invitesKey = ['ledger-invites', ledgerId] as const;

    const membersQuery = useQuery({ queryKey: membersKey, queryFn: () => fetchLedgerMembers(ledgerId) });
    const ledgersQuery = useQuery({ queryKey: ['ledgers'], queryFn: fetchVisibleLedgers });
    const isOwner = ledgersQuery.data?.find((l) => l.id === ledgerId)?.role === 'owner';

    const invalidateMembers = () => queryClient.invalidateQueries({ queryKey: membersKey });
    const setRole = useMutation({
        mutationFn: (v: { userId: string; role: string }) => setLedgerMemberRole(ledgerId, v.userId, v.role),
        onSuccess: invalidateMembers,
    });
    const remove = useMutation({
        mutationFn: (userId: string) => removeLedgerMember(ledgerId, userId),
        onSuccess: invalidateMembers,
    });

    // Invites (owner only).
    const invitesQuery = useQuery({
        queryKey: invitesKey,
        queryFn: () => fetchLedgerInvites(ledgerId),
        enabled: isOwner,
    });
    const invalidateInvites = () => queryClient.invalidateQueries({ queryKey: invitesKey });
    const [inviteRole, setInviteRole] = useState('viewer');
    const [createdToken, setCreatedToken] = useState<string | null>(null);
    const createInvite = useMutation({
        mutationFn: () => createLedgerInvite(ledgerId, inviteRole),
        onSuccess: (data) => { setCreatedToken(data.token); invalidateInvites(); },
    });
    const revokeInvite = useMutation({
        mutationFn: (id: string) => revokeLedgerInvite(ledgerId, id),
        onSuccess: invalidateInvites,
    });

    const [removeTarget, setRemoveTarget] = useState<LedgerMember | null>(null);

    const members = membersQuery.data;
    const ownerCount = members?.filter((m) => m.role === 'owner').length ?? 0;
    const busy = setRole.isPending || remove.isPending;

    return (
        <section className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Members</h2>
                <p className="text-sm text-text-muted">
                    Who can access this ledger and at what role. Owners manage members and the ledger
                    itself; editors can make changes; viewers are read-only.
                </p>
            </header>
            <Panel>
                <PanelBody>
                    {membersQuery.isPending ? (
                        <p className="text-sm text-text-muted">Loading…</p>
                    ) : membersQuery.isError ? (
                        <p className="text-sm text-text-muted">Couldn&rsquo;t load members.</p>
                    ) : !members || members.length === 0 ? (
                        <p className="text-sm text-text-muted">No members.</p>
                    ) : (
                        <ul className="divide-y divide-border">
                            {members.map((m) => {
                                const isSoleOwner = m.role === 'owner' && ownerCount <= 1;
                                const lockNote = isSoleOwner
                                    ? 'A ledger must keep at least one owner.'
                                    : undefined;
                                return (
                                    <li
                                        key={m.userId}
                                        className="flex items-center justify-between gap-4 py-2"
                                    >
                                        <div className="min-w-0 text-sm">
                                            <div className="font-medium">{m.displayName}</div>
                                            {m.username ? (
                                                <div className="truncate text-xs text-text-muted">
                                                    {m.username}
                                                </div>
                                            ) : null}
                                        </div>
                                        <div className="flex shrink-0 items-center gap-2">
                                            {isOwner ? (
                                                <>
                                                    <select
                                                        aria-label={`Role for ${m.displayName}`}
                                                        value={m.role}
                                                        disabled={busy || isSoleOwner}
                                                        title={lockNote}
                                                        onChange={(e) =>
                                                            setRole.mutate({ userId: m.userId, role: e.target.value })
                                                        }
                                                        className="rounded border border-border bg-surface px-2 py-1 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:opacity-60"
                                                    >
                                                        {ROLE_OPTIONS.map((o) => (
                                                            <option key={o.value} value={o.value}>
                                                                {o.label}
                                                            </option>
                                                        ))}
                                                    </select>
                                                    <Button
                                                        variant="danger"
                                                        size="sm"
                                                        disabled={busy || isSoleOwner}
                                                        title={lockNote}
                                                        onClick={() => setRemoveTarget(m)}
                                                    >
                                                        Remove
                                                    </Button>
                                                </>
                                            ) : (
                                                <span className="text-sm capitalize text-text-muted">
                                                    {m.role}
                                                </span>
                                            )}
                                        </div>
                                    </li>
                                );
                            })}
                        </ul>
                    )}
                    {setRole.isError || remove.isError ? (
                        <p className="mt-2 text-xs text-state-danger">
                            {errorMessage(setRole.error ?? remove.error, 'Action failed.')}
                        </p>
                    ) : null}
                </PanelBody>
            </Panel>

            {isOwner ? (
                <>
                    <div className="space-y-3">
                        <header className="space-y-1">
                            <h3 className="text-sm font-semibold">Invite to this ledger</h3>
                            <p className="text-sm text-text-muted">
                                Pick a role and create a one-time link. Send it to the person you want to
                                add — they register (or, if already signed in, accept) to join at that role.
                            </p>
                        </header>
                        <Panel>
                            <PanelBody>
                                <div className="flex items-center gap-2">
                                    <select
                                        aria-label="Invite role"
                                        value={inviteRole}
                                        onChange={(e) => setInviteRole(e.target.value)}
                                        className="rounded border border-border bg-surface px-2 py-1 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                                    >
                                        {ROLE_OPTIONS.map((o) => (
                                            <option key={o.value} value={o.value}>{o.label}</option>
                                        ))}
                                    </select>
                                    <Button
                                        variant="secondary"
                                        size="sm"
                                        disabled={createInvite.isPending}
                                        onClick={() => createInvite.mutate()}
                                    >
                                        {createInvite.isPending ? 'Creating…' : 'Create invite link'}
                                    </Button>
                                </div>
                                {createInvite.isError ? (
                                    <p className="mt-2 text-xs text-state-danger">
                                        {errorMessage(createInvite.error, 'Couldn’t create the invite.')}
                                    </p>
                                ) : null}
                            </PanelBody>
                        </Panel>
                    </div>

                    <div className="space-y-3">
                        <header className="space-y-1">
                            <h3 className="text-sm font-semibold">Pending invites</h3>
                            <p className="text-sm text-text-muted">
                                Invite links you’ve created for this ledger that no one has used yet. Revoke
                                kills a link so it can no longer be used.
                            </p>
                        </header>
                        <Panel>
                            <PanelBody>
                                {invitesQuery.data ? (
                                    <PendingInvitesList
                                        invites={invitesQuery.data}
                                        onRevoke={(id) => revokeInvite.mutate(id)}
                                        revoking={revokeInvite.isPending}
                                    />
                                ) : (
                                    <p className="text-sm text-text-muted">Loading…</p>
                                )}
                                {revokeInvite.isError ? (
                                    <p className="mt-2 text-xs text-state-danger">
                                        {errorMessage(revokeInvite.error, 'Couldn’t revoke the invite.')}
                                    </p>
                                ) : null}
                            </PanelBody>
                        </Panel>
                    </div>
                </>
            ) : null}

            <ConfirmDialog
                open={removeTarget !== null}
                title={`Remove “${removeTarget?.displayName ?? ''}”?`}
                body="They lose access to this ledger. You can invite them back later."
                confirmLabel="Remove"
                variant="danger"
                isConfirming={remove.isPending}
                onConfirm={() => {
                    if (removeTarget) {
                        remove.mutate(removeTarget.userId, { onSuccess: () => setRemoveTarget(null) });
                    }
                }}
                onCancel={() => setRemoveTarget(null)}
            />
            <InviteLinkModal token={createdToken} onClose={() => setCreatedToken(null)} />
        </section>
    );
}
