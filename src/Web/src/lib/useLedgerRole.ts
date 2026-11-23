import { useQuery } from '@tanstack/react-query';

import { fetchVisibleLedgers } from '@/lib/api';
import { canWriteLedger, isLedgerOwner } from '@/lib/ledgerRole';

/**
 * The reader's role on one ledger, from the same ['ledgers'] read the rail and every
 * destination already share, so asking costs no request of its own (ADR-0111 D3).
 *
 * The one reading of the reader's role: the rail, the home screen, both registers, the
 * Structure tabs and the ledger's settings ask this hook rather than the ledger list.
 * `canEdit` is {@link canWriteLedger}'s answer and `isOwner` {@link isLedgerOwner}'s,
 * so each rule has one definition. Both are false until the ledger list has answered:
 * a control that appears a moment late is a smaller fault than one a reader can press
 * and have the server refuse.
 */
export function useLedgerRole(ledgerId: string | undefined) {
    const ledgersQuery = useQuery({ queryKey: ['ledgers'], queryFn: fetchVisibleLedgers });
    const ledger = ledgerId === undefined ? undefined : ledgersQuery.data?.find((l) => l.id === ledgerId);
    const role = ledger?.role;
    return {
        ledger,
        role,
        canEdit: canWriteLedger(role),
        isOwner: isLedgerOwner(role),
    };
}
