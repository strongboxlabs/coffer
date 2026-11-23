// Cash ahead's read: the projection.

import { useQuery } from '@tanstack/react-query';

import { fetchBalanceForecast } from '@/lib/api';

/**
 * The projection over `days`, for every account a reminder is paid from: always the
 * whole scope, since the widget's account setting picks only which chart it draws.
 * Under the home screen's prefix, which every write in the ledger refreshes.
 */
export function useBalanceForecast(ledgerId: string, today: string, days: number) {
    return useQuery({
        queryKey: ['home', ledgerId, 'balance-forecast', { days, today }],
        queryFn: () => fetchBalanceForecast(ledgerId, { days }),
    });
}
