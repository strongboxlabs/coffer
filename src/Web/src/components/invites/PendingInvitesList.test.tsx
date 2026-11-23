import { useRef, useState } from 'react';
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import type { PendingInvite } from '@/lib/api';

import { PendingInvitesList } from './PendingInvitesList';

// ADR-0023 §L: a revoked link takes its row's Revoke, which held focus through
// the request, with it. Focus goes to the next row's Revoke, else the previous
// row's, else the host's Create invite link.

const invite = (id: string, role: string): PendingInvite => ({
    id, role, ledgerId: 'l1', ledgerName: null, grantsAdmin: false,
    expiresAt: '2026-12-01T00:00:00Z', createdAt: '2026-11-01T00:00:00Z',
} as unknown as PendingInvite);

function Host({ initial }: { initial: PendingInvite[] }) {
    const [invites, setInvites] = useState(initial);
    const createRef = useRef<HTMLButtonElement | null>(null);
    return (
        <>
            <button ref={createRef} type="button">Create invite link</button>
            <PendingInvitesList
                invites={invites}
                onRevoke={(id) => setInvites((all) => all.filter((i) => i.id !== id))}
                revoking={false}
                addRef={createRef}
            />
        </>
    );
}

const revokes = () => screen.queryAllByRole('button', { name: 'Revoke' });

describe('PendingInvitesList — focus after a revoke', () => {
    const three = [invite('i1', 'viewer'), invite('i2', 'editor'), invite('i3', 'owner')];

    it('goes to the next row\'s Revoke', async () => {
        render(<Host initial={three} />);
        await userEvent.click(revokes()[0]!);
        expect(screen.queryByText('Viewer role')).toBeNull();
        expect(revokes()[0]).toHaveFocus();
        expect(revokes()[0]!.closest('li')).toHaveTextContent('Editor role');
    });

    it('goes to the previous row\'s when the last row goes', async () => {
        render(<Host initial={three} />);
        await userEvent.click(revokes()[2]!);
        expect(revokes()[1]).toHaveFocus();
        expect(revokes()[1]!.closest('li')).toHaveTextContent('Editor role');
    });

    it('goes to Create invite link when no row is left', async () => {
        render(<Host initial={[three[0]!]} />);
        await userEvent.click(revokes()[0]!);
        expect(await screen.findByText('No outstanding invite links.')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Create invite link' })).toHaveFocus();
    });
});
