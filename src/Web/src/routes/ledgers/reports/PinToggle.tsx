import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Pin, PinOff } from 'lucide-react';

import {
    fetchReportPins,
    pinCatalogReport,
    pinSavedReport,
    unpinCatalogReport,
    unpinSavedReport,
} from '@/lib/api';
import { IconButton } from '@/components/ui/IconButton';

/**
 * Pin or unpin a report (ADR-0104 D2).
 *
 * TWO TARGETS, because pinning must not require naming. A saved report is
 * pinned by id; a canned one by its catalog KEY, which is what lets the report
 * you always read reach the rail without you inventing a title for it first.
 *
 * A catalog report is pinned from its CARD, not from the open view: once the
 * controls have been adjusted the view is no longer that preset, and a pin
 * made there would quietly point at the preset rather than at what is on
 * screen. Saving is the way to keep an adjusted question.
 */
export function usePinned(ledgerId: string) {
    const pins = useQuery({
        queryKey: ['report-pins', ledgerId],
        queryFn: () => fetchReportPins(ledgerId),
    });
    return {
        pins: pins.data ?? [],
        loading: pins.isPending,
        isSavedPinned: (id: string) => (pins.data ?? []).some((p) => p.savedReportId === id),
        isCatalogPinned: (key: string) => (pins.data ?? []).some((p) => p.catalogKey === key),
    };
}

/**
 * The pin toggle, on a card in the Reports landing — for BOTH kinds.
 *
 * It lived in the report view for saved reports and on the card for canned
 * ones, which made one action look like two and put the saved half somewhere
 * the canned half could never be. Pinning is a decision about the list, so it
 * belongs where the list is.
 */
export function ReportPinToggle({
    ledgerId, target, name,
}: {
    ledgerId: string;
    /** Which kind, and which one. Exactly as the two routes take it. */
    target: { kind: 'saved'; id: string } | { kind: 'catalog'; key: string };
    name: string;
}) {
    const queryClient = useQueryClient();
    const { isCatalogPinned, isSavedPinned, loading } = usePinned(ledgerId);
    const pinned = target.kind === 'saved'
        ? isSavedPinned(target.id)
        : isCatalogPinned(target.key);

    const toggle = useMutation({
        mutationFn: () => {
            if (target.kind === 'saved') {
                return pinned
                    ? unpinSavedReport(ledgerId, target.id)
                    : pinSavedReport(ledgerId, target.id);
            }
            return pinned
                ? unpinCatalogReport(ledgerId, target.key)
                : pinCatalogReport(ledgerId, target.key);
        },
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['report-pins', ledgerId] }),
    });

    return (
        <IconButton
            aria-label={pinned ? `Unpin ${name}` : `Pin ${name} to sidebar`}
            aria-pressed={pinned}
            disabled={toggle.isPending || loading}
            onClick={() => toggle.mutate()}
            className={pinned ? 'text-accent' : undefined}
        >
            {pinned
                ? <PinOff aria-hidden className="size-icon-sm" />
                : <Pin aria-hidden className="size-icon-sm" />}
        </IconButton>
    );
}
