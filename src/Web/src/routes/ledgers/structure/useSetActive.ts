import { useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import { setAccountActive } from '@/lib/api';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';

/**
 * Switch an account or a category between active and inactive. Both are rows of the same table
 * with the same flag, so one mutation serves the Accounts and the Categories tabs.
 *
 * An inactive one leaves the pickers and new transactions but keeps its history; reactivating
 * undoes it. Every list that draws it is refreshed: the rail, the tabs, the pickers, the
 * Overview.
 */
export function useSetActive(ledgerId: string) {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: (args: { accountId: string; active: boolean }) =>
            setAccountActive(ledgerId, args.accountId, args.active),
        // The register seam refreshes the Categories tab's read too.
        onSuccess: () => invalidateLedgerRegister(queryClient, ledgerId),
    });
}
