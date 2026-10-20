import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';

import { fetchTags } from '@/lib/api';
import { buildTagColorMap } from '@/lib/tagPalette';

// Supplies a lower-cased tag-name → colour (hex) map to descendant
// {@link TagChip}s. The register's rows carry tag NAMES only (the
// resolved view is unchanged per ADR-0076); colour is joined here from
// the ledger's tag list, fetched once via the shared React Query key
// (['tags', ledgerId]) that the Tags panel + autocomplete also use, so a
// recolor there invalidates and repaints the register too.

const TagColorsContext = createContext<ReadonlyMap<string, string>>(new Map());

export function TagColorsProvider({
    ledgerId,
    children,
}: {
    ledgerId: string;
    children: ReactNode;
}) {
    const tagsQuery = useQuery({
        queryKey: ['tags', ledgerId],
        queryFn: () => fetchTags(ledgerId),
        staleTime: 60_000,
    });
    const map = useMemo(
        () => buildTagColorMap(tagsQuery.data ?? []),
        [tagsQuery.data],
    );
    return <TagColorsContext.Provider value={map}>{children}</TagColorsContext.Provider>;
}

/**
 * Supply a colour map directly, with no ledger and no fetch.
 *
 * For the style guide and for tests: the fetching provider above needs a
 * `ledgerId` and a QueryClient, which a surface that only wants to SHOW the
 * colours has no business standing up.
 */
export function TagColorsValueProvider({
    colors,
    children,
}: {
    colors: ReadonlyArray<{ name: string; color: string | null }>;
    children: ReactNode;
}) {
    const map = useMemo(() => buildTagColorMap(colors), [colors]);
    return <TagColorsContext.Provider value={map}>{children}</TagColorsContext.Provider>;
}

/** The colour (hex) for a tag name, or `null` (→ default gray). Safe with
 *  no provider present — the default context is an empty map. */
export function useTagColor(name: string): string | null {
    const map = useContext(TagColorsContext);
    return map.get(name.toLowerCase()) ?? null;
}
