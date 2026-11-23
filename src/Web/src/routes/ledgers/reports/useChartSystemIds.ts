import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';

import { fetchAccounts } from '@/lib/api';
import { systemCategoryIds } from '@/lib/categoryChip';

/** No category treated as the ledger's Uncategorized. */
export const NO_SYSTEM_IDS: ReadonlySet<string> = new Set();

/**
 * The ledger's Uncategorized, for a category chart's colours, or null while a
 * category chart still waits for it.
 *
 * A category's chart colour is its chip's, and the ledger's Uncategorized has the
 * neutral chip, which only the accounts list identifies. A series never changes
 * colour once drawn, so a category chart waits for the list (in the usual case
 * already cached) and keeps, per ledger, the first answer it got: the list's
 * Uncategorized or, when the list could not be read, no category treated as
 * Uncategorized. A later refetch, whatever it returns, does not recolour what is on
 * screen. Every surface that draws a report chart (Reports, a home tile) reads it
 * here, so the two cannot colour one category two ways.
 *
 * `categoryChart` is whether the chart on screen is grouped by category; nothing
 * is read otherwise.
 */
export function useChartSystemIds(ledgerId: string, categoryChart: boolean): ReadonlySet<string> | null {
    const accounts = useQuery({
        queryKey: ['accounts', ledgerId],
        queryFn: () => fetchAccounts(ledgerId),
        enabled: categoryChart,
    });
    const known = useMemo<ReadonlySet<string> | null>(
        () => (accounts.data !== undefined
            ? systemCategoryIds(accounts.data)
            : accounts.isError ? NO_SYSTEM_IDS : null),
        [accounts.data, accounts.isError],
    );
    const [fixedIds, setFixedIds] = useState<{ ledgerId: string; ids: ReadonlySet<string> } | null>(null);
    const systemIds = fixedIds?.ledgerId === ledgerId ? fixedIds.ids : known;
    useEffect(() => {
        if (known !== null && fixedIds?.ledgerId !== ledgerId) setFixedIds({ ledgerId, ids: known });
    }, [known, fixedIds, ledgerId]);
    return systemIds;
}
