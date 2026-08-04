import { Link2 } from 'lucide-react';

import type { PendingInvite } from '@/lib/api';
import { Button } from '@/components/ui/Button';

/**
 * The list of outstanding (unredeemed) invite links, each with a Revoke action
 * (ADR-0083 slice B). Shared by the owner (Members panel) and admin (Users tab)
 * surfaces. Every row is one link; the link-icon + "Invite link" subtext keep it
 * visually distinct from the "create a new invite" control above it. <c>showLedger</c>
 * spells out the target ledger — useful on the admin view (which lists invites across
 * every ledger), redundant on a single ledger's Members panel.
 */
export function PendingInvitesList({
    invites,
    onRevoke,
    revoking,
    showLedger = false,
}: {
    invites: PendingInvite[];
    onRevoke: (id: string) => void;
    revoking: boolean;
    showLedger?: boolean;
}) {
    if (invites.length === 0) {
        return <p className="text-sm text-text-muted">No outstanding invite links.</p>;
    }
    return (
        <ul className="divide-y divide-border">
            {invites.map((inv) => (
                <li key={inv.id} className="flex items-center justify-between gap-4 py-2">
                    <div className="flex min-w-0 items-center gap-2.5 text-sm">
                        <Link2 className="size-4 shrink-0 text-text-muted" aria-hidden />
                        <div className="min-w-0">
                            <div className="font-medium">{describeScope(inv, showLedger)}</div>
                            <div className="truncate text-xs text-text-muted">
                                Invite link · expires {new Date(inv.expiresAt).toLocaleDateString()}
                            </div>
                        </div>
                    </div>
                    <Button
                        variant="danger"
                        size="sm"
                        disabled={revoking}
                        onClick={() => onRevoke(inv.id)}
                    >
                        Revoke
                    </Button>
                </li>
            ))}
        </ul>
    );
}

function describeScope(inv: PendingInvite, showLedger: boolean): string {
    const role = inv.role ? inv.role[0].toUpperCase() + inv.role.slice(1) : null;
    // Owner (Members) view: the invite is always for this ledger, so just name the role.
    if (!showLedger) return role ? `${role} role` : 'Member';
    // Admin (Users) view: spell out ledger-vs-account, the role, and any admin grant.
    const parts: string[] = [inv.ledgerId ? (inv.ledgerName ?? 'A ledger') : 'Account only'];
    if (role) parts.push(`${role} role`);
    if (inv.grantsAdmin) parts.push('admin');
    return parts.join(' · ');
}
