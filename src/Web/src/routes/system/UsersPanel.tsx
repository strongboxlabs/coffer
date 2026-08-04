import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    fetchAdminUsers,
    setUserDisabled,
    setUserAdmin,
    fetchAdminInvites,
    createAdminInvite,
    revokeAdminInvite,
} from '@/lib/api';
import { errorMessage } from '@/lib/errorMessage';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { InviteLinkModal } from '@/components/invites/InviteLinkModal';
import { PendingInvitesList } from '@/components/invites/PendingInvitesList';

const USERS_KEY = ['admin-users'] as const;
const INVITES_KEY = ['admin-invites'] as const;

/**
 * System → Users (admin, ADR-0083): every user on this deployment — enable/disable, grant
 * or revoke the instance admin flag, and invite a new user via a one-time link (optionally
 * granting them admin). The instance always keeps ≥1 enabled admin (the API refuses the
 * last one, surfaced here as an error). Admin-only; the endpoints are the boundary.
 */
export function UsersPanel() {
    const queryClient = useQueryClient();
    const query = useQuery({ queryKey: USERS_KEY, queryFn: fetchAdminUsers, retry: false });
    const invalidateUsers = () => queryClient.invalidateQueries({ queryKey: USERS_KEY });

    const disabled = useMutation({
        mutationFn: (v: { userId: string; disabled: boolean }) => setUserDisabled(v.userId, v.disabled),
        onSuccess: invalidateUsers,
    });
    const admin = useMutation({
        mutationFn: (v: { userId: string; isAdmin: boolean }) => setUserAdmin(v.userId, v.isAdmin),
        onSuccess: invalidateUsers,
    });

    // Invites.
    const invitesQuery = useQuery({ queryKey: INVITES_KEY, queryFn: fetchAdminInvites, retry: false });
    const invalidateInvites = () => queryClient.invalidateQueries({ queryKey: INVITES_KEY });
    const [grantAdmin, setGrantAdmin] = useState(false);
    const [createdToken, setCreatedToken] = useState<string | null>(null);
    const createInvite = useMutation({
        mutationFn: () => createAdminInvite({ grantsAdmin: grantAdmin }),
        onSuccess: (data) => { setCreatedToken(data.token); setGrantAdmin(false); invalidateInvites(); },
    });
    const revokeInvite = useMutation({
        mutationFn: (id: string) => revokeAdminInvite(id),
        onSuccess: invalidateInvites,
    });

    const users = query.data;
    const enabledAdminCount = users?.filter((u) => u.isAdmin && !u.isDisabled).length ?? 0;
    const busy = disabled.isPending || admin.isPending;

    return (
        <section className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Users</h2>
                <p className="text-sm text-text-muted">
                    Everyone on this deployment. Disable a user to block sign-in (their access is kept);
                    grant admin to let them manage users and backups.
                </p>
            </header>
            <Panel>
                <PanelBody>
                    {query.isPending ? (
                        <p className="text-sm text-text-muted">Loading…</p>
                    ) : query.isError ? (
                        <p className="text-sm text-text-muted">Couldn&rsquo;t load users.</p>
                    ) : !users || users.length === 0 ? (
                        <p className="text-sm text-text-muted">No users yet.</p>
                    ) : (
                        <ul className="divide-y divide-border">
                            {users.map((u) => {
                                // The deployment must keep ≥1 enabled admin, so the sole enabled
                                // admin's "Remove admin" / "Disable" are locked (the API enforces
                                // it too — this mirrors the sole-owner lock in MembersPanel).
                                const isSoleEnabledAdmin =
                                    u.isAdmin && !u.isDisabled && enabledAdminCount <= 1;
                                const lockNote = isSoleEnabledAdmin
                                    ? 'The deployment must keep at least one enabled admin.'
                                    : undefined;
                                return (
                                    <li key={u.id} className="flex items-center justify-between gap-4 py-2">
                                        <div className="min-w-0 text-sm">
                                            <div className="flex items-center gap-2">
                                                <span className="font-medium">{u.displayName}</span>
                                                {u.isAdmin ? (
                                                    <span className="rounded bg-accent/15 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-accent">
                                                        Admin
                                                    </span>
                                                ) : null}
                                                {u.isDisabled ? (
                                                    <span className="rounded bg-state-danger/15 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-state-danger">
                                                        Disabled
                                                    </span>
                                                ) : null}
                                            </div>
                                            <div className="truncate text-xs text-text-muted">
                                                {u.username ?? '—'} · {u.ledgerCount} ledger(s)
                                            </div>
                                        </div>
                                        <div className="flex shrink-0 items-center gap-2">
                                            <Button
                                                variant="secondary"
                                                size="sm"
                                                disabled={busy || isSoleEnabledAdmin}
                                                title={lockNote}
                                                onClick={() => admin.mutate({ userId: u.id, isAdmin: !u.isAdmin })}
                                            >
                                                {u.isAdmin ? 'Remove admin' : 'Make admin'}
                                            </Button>
                                            <Button
                                                variant={u.isDisabled ? 'secondary' : 'danger'}
                                                size="sm"
                                                disabled={busy || isSoleEnabledAdmin}
                                                title={lockNote}
                                                onClick={() => disabled.mutate({ userId: u.id, disabled: !u.isDisabled })}
                                            >
                                                {u.isDisabled ? 'Enable' : 'Disable'}
                                            </Button>
                                        </div>
                                    </li>
                                );
                            })}
                        </ul>
                    )}
                    {disabled.isError || admin.isError ? (
                        <p className="mt-2 text-xs text-state-danger">
                            {errorMessage(disabled.error ?? admin.error, 'Action failed.')}
                        </p>
                    ) : null}
                </PanelBody>
            </Panel>

            <div className="space-y-3">
                <header className="space-y-1">
                    <h3 className="text-sm font-semibold">Invite a user</h3>
                    <p className="text-sm text-text-muted">
                        Create a one-time link to add someone to this deployment. They register a passkey,
                        then create their own ledger. Tick <span className="font-medium">Grant admin</span>{' '}
                        to also let them manage users and backups.
                    </p>
                </header>
                <Panel>
                    <PanelBody className="space-y-3">
                        <div className="flex items-center justify-between gap-3">
                            <Checkbox
                                label="Grant admin"
                                checked={grantAdmin}
                                onChange={(e) => setGrantAdmin(e.target.checked)}
                            />
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
                            <p className="text-xs text-state-danger">
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
                        Every unredeemed invite link across the deployment — the ones you create above and
                        the ledger invites owners create from a ledger’s Members tab. Revoke kills a link so
                        it can no longer be used.
                    </p>
                </header>
                <Panel>
                    <PanelBody className="space-y-3">
                        {invitesQuery.data ? (
                            <PendingInvitesList
                                invites={invitesQuery.data}
                                onRevoke={(id) => revokeInvite.mutate(id)}
                                revoking={revokeInvite.isPending}
                                showLedger
                            />
                        ) : (
                            <p className="text-sm text-text-muted">Loading…</p>
                        )}
                        {revokeInvite.isError ? (
                            <p className="text-xs text-state-danger">
                                {errorMessage(revokeInvite.error, 'Couldn’t revoke the invite.')}
                            </p>
                        ) : null}
                    </PanelBody>
                </Panel>
            </div>

            <InviteLinkModal token={createdToken} onClose={() => setCreatedToken(null)} />
        </section>
    );
}
