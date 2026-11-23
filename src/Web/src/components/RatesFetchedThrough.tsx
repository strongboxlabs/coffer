import { fetchedThroughText } from '@/lib/fxCoverage';
import { useFxRatesView } from '@/lib/marketSeriesQueries';
import type { RateUnavailable } from '@/lib/types';

/**
 * How far the rates a "no rate" figure needed have been fetched, as the missing-rate
 * line says it after naming the missing pair and day ({@link fetchedThroughText}).
 *
 * Read from the rates view (`['fx-rates', ledgerId]`), which any member may read. Mount
 * it only where a rate is missing: it is the only thing that asks for that view on the
 * screens that carry it. Draws nothing until the view has answered, when it cannot be
 * read, or when it lists none of the missing pairs.
 */
export function RatesFetchedThrough({ ledgerId, missing }: { ledgerId: string; missing: readonly RateUnavailable[] }) {
    const view = useFxRatesView(ledgerId);
    if (!view.data) return null;
    const text = fetchedThroughText(missing, view.data.pairs);
    return text === null ? null : <>{text}</>;
}
