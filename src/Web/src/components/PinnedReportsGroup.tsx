import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';

import { fetchReportPins } from '@/lib/api';
import { cn } from '@/lib/cn';
import { findCatalogReport } from '@/routes/ledgers/reports/catalog';
import type { PinnedReport, ReportSpec } from '@/types/report';

/**
 * Pinned reports in the rail, beneath the Reports destination (ADR-0104 D2).
 *
 * TWO LISTS, because the two kinds are not the same thing. A pinned CANNED
 * report sits directly under Reports — it is one of the reports, and reads as
 * a destination. A pinned SAVED report is a question someone wrote down, so
 * those group under "Saved": there can be many, and they belong to the reader
 * rather than to the app.
 *
 * WHY PINS AND NOT EVERY SAVED REPORT. Listing them all was the first proposal
 * and does not scale: the list is user-grown and unbounded, and this rail must
 * always have room for the account list below it. Pinning makes the rail's
 * length a decision rather than a consequence, so no cap has to exist.
 *
 * A CATALOG PIN CARRIES ONLY A KEY. Name and spec are resolved here, from the
 * same catalog the landing renders, so a preset's definition is never frozen
 * into a pin row — and a key this build no longer knows renders nothing, which
 * is the right end for a retired preset.
 *
 * THE DISCLOSURE IS ON THE REPORTS ROW, not here. A "Pinned" header of its
 * own was one label too many: the rail already says Reports, and a second word
 * underneath it to say "these are the pinned ones" spends a line explaining
 * what the indent shows. The caller owns the open state and renders this only
 * when expanded. "Saved" survives as a label because it separates two KINDS of
 * entry, which the indent cannot say.
 *
 * Renders nothing when there are no pins, so a ledger with none shows no
 * arrow at all rather than an empty disclosure. The caller remembers the open
 * state per browser, as it does the rest of the rail's state (ADR-0057).
 */
export function PinnedReportsGroup({
    ledgerId, pathname,
}: {
    ledgerId: string;
    pathname: string;
}) {
    const pins = useQuery({
        queryKey: ['report-pins', ledgerId],
        queryFn: () => fetchReportPins(ledgerId),
    });

    const all: PinnedReport[] = pins.data ?? [];

    // A catalog pin whose key this build does not know is dropped rather than
    // rendered as an entry that opens a retired report.
    const canned = all.flatMap((pin) => {
        if (pin.catalogKey === null) return [];
        const entry = findCatalogReport(pin.catalogKey);
        return entry ? [{ key: pin.catalogKey, entry }] : [];
    });

    const saved = all.filter((p) => p.savedReportId !== null && p.spec !== null);

    if (canned.length === 0 && saved.length === 0) return null;

    return (
        <div className="space-y-px">
            {canned.map(({ key, entry }) => (
                <RailLink
                    key={`c:${key}`}
                    ledgerId={ledgerId}
                    pathname={pathname}
                    label={entry.name}
                    spec={entry.spec}
                    indent="pl-8"
                />
            ))}

            {/* "Saved" is a LABEL, not a second toggle. One disclosure, on the
                Reports row itself, is the whole of this control. */}
            {saved.length > 0 ? (
                <>
                    <p className="py-0.5 pr-2 pl-8 text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-subtle">
                        Saved
                    </p>
                    <ul>
                        {saved.map((pin) => (
                            <li key={`s:${pin.savedReportId}`}>
                                <RailLink
                                    ledgerId={ledgerId}
                                    pathname={pathname}
                                    label={pin.name ?? 'Report'}
                                    spec={pin.spec!}
                                    savedId={pin.savedReportId!}
                                    indent="pl-10"
                                />
                            </li>
                        ))}
                    </ul>
                </>
            ) : null}
        </div>
    );
}

function RailLink({
    ledgerId, pathname, label, spec, savedId, indent,
}: {
    ledgerId: string;
    pathname: string;
    label: string;
    spec: ReportSpec;
    savedId?: string;
    indent: string;
}) {
    // Active only when this exact report is open. The view keeps a saved
    // report's identity in `saved`, so the path alone cannot tell two pinned
    // entries apart.
    const onView = pathname.startsWith(`/ledgers/${ledgerId}/reports/view`);
    const query = typeof window === 'undefined' ? null : new URLSearchParams(window.location.search);
    const current = query?.get('saved') ?? null;
    // A canned report is also told apart by its kind: two catalog pins are both
    // "no saved id", and only the one whose report is open is the current one.
    // No `kind` in the URL is the aggregation.
    const openKind = query?.get('kind') ?? null;
    const active = onView && (savedId === undefined
        ? current === null && openKind === (spec.kind ?? null)
        : current === savedId);

    return (
        <Link
            to="/ledgers/$ledgerId/reports/view"
            params={{ ledgerId }}
            // The whole question goes into the URL, exactly as the landing
            // does it: a link carrying only an identity would open the default
            // report under a pinned name.
            search={savedId === undefined ? { ...spec } : { ...spec, saved: savedId, name: label }}
            title={label}
            className={cn(
                'block truncate rounded py-0.5 pr-2 text-xs hover:bg-surface-hover',
                indent,
                active ? 'bg-accent-soft text-accent-soft-text' : 'text-text-muted',
            )}
        >
            {label}
        </Link>
    );
}

/**
 * Whether this ledger has any pins the rail could show. Lets the Reports row
 * draw its disclosure arrow only when there is something behind it — an arrow
 * that opens nothing is worse than no arrow.
 *
 * The rail has no ledger yet on a page outside one until the ledger list
 * arrives, and none at all when there are no ledgers; it then asks nothing,
 * since a request without a ledger id is one the server can only refuse.
 */
export function useHasReportPins(ledgerId: string | undefined): boolean {
    const pins = useQuery({
        queryKey: ['report-pins', ledgerId],
        queryFn: () => fetchReportPins(ledgerId!),
        enabled: ledgerId !== undefined,
    });
    return (pins.data ?? []).length > 0;
}
