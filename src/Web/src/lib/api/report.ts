import type {
    PinnedReport,
    ReportResult,
    ReportSpec,
    SavedReport,
    SavedReportVisibility,
} from '../../types/report';
import { request } from './_request';

/**
 * Serialize a spec into the query string the summary endpoint reads.
 *
 * Only what is SET is sent. Omitting a defaulted field keeps the URL readable
 * and keeps a saved spec from pinning a default it never chose — the next time
 * a default changes, a report that never expressed an opinion should move with
 * it.
 */
export function specToQuery(spec: ReportSpec): string {
    const p = new URLSearchParams();
    p.set('measure', spec.measure);
    p.set('groupBy', spec.groupBy);
    if (spec.bucket !== 'None') p.set('bucket', spec.bucket);

    if (spec.dateMode === 'Sliding') {
        p.set('dateMode', 'Sliding');
        if (spec.windowCount !== undefined) p.set('windowCount', String(spec.windowCount));
        if (spec.windowUnit) p.set('windowUnit', spec.windowUnit);
        if (spec.windowAlign) p.set('windowAlign', 'true');
    } else {
        if (spec.from) p.set('from', spec.from);
        if (spec.to) p.set('to', spec.to);
    }

    if (spec.tagId) p.set('tagId', spec.tagId);
    if (spec.rollup) p.set('rollup', 'true');
    if (spec.topN !== undefined) p.set('topN', String(spec.topN));
    return p.toString();
}

/** Run a report. A GET, so the result is linkable and reloadable. */
export function fetchReportSummary(
    ledgerId: string,
    spec: ReportSpec,
): Promise<ReportResult> {
    return request<ReportResult>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/reports/summary?${specToQuery(spec)}`,
    );
}

const savedBase = (ledgerId: string) =>
    `/api/ledgers/${encodeURIComponent(ledgerId)}/reports/saved`;

/** Every memorized report the acting user can see — their own, plus shared. */
export function fetchSavedReports(ledgerId: string): Promise<SavedReport[]> {
    return request<SavedReport[]>(savedBase(ledgerId));
}

export function createSavedReport(
    ledgerId: string,
    name: string,
    spec: ReportSpec,
    visibility: SavedReportVisibility = 'private',
): Promise<SavedReport> {
    return request<SavedReport>(savedBase(ledgerId), {
        method: 'POST',
        body: { name, spec, visibility },
    });
}

/**
 * Patch a saved report. Omitted fields are left alone server-side, which is
 * what lets a rename and a re-share be separate acts without either resending
 * the spec.
 */
export function patchSavedReport(
    ledgerId: string,
    reportId: string,
    patch: { name?: string; spec?: ReportSpec; visibility?: SavedReportVisibility },
): Promise<SavedReport> {
    return request<SavedReport>(`${savedBase(ledgerId)}/${encodeURIComponent(reportId)}`, {
        method: 'PATCH',
        body: patch,
    });
}

export function deleteSavedReport(ledgerId: string, reportId: string): Promise<void> {
    return request<void>(`${savedBase(ledgerId)}/${encodeURIComponent(reportId)}`, {
        method: 'DELETE',
    });
}

const pinsBase = (ledgerId: string) =>
    `/api/ledgers/${encodeURIComponent(ledgerId)}/reports/pins`;

/** The acting user's own rail for this ledger. Pins are never shared. */
export function fetchReportPins(ledgerId: string): Promise<PinnedReport[]> {
    return request<PinnedReport[]>(pinsBase(ledgerId));
}

// All four are idempotent, like the toggles that call them. Saved and catalog
// pins take separate routes because a catalog key is a slug and a saved report
// is a GUID — one path would have to guess which it had.

export function pinSavedReport(ledgerId: string, reportId: string): Promise<void> {
    return request<void>(`${pinsBase(ledgerId)}/saved/${encodeURIComponent(reportId)}`, {
        method: 'PUT',
    });
}

export function unpinSavedReport(ledgerId: string, reportId: string): Promise<void> {
    return request<void>(`${pinsBase(ledgerId)}/saved/${encodeURIComponent(reportId)}`, {
        method: 'DELETE',
    });
}

export function pinCatalogReport(ledgerId: string, catalogKey: string): Promise<void> {
    return request<void>(`${pinsBase(ledgerId)}/catalog/${encodeURIComponent(catalogKey)}`, {
        method: 'PUT',
    });
}

export function unpinCatalogReport(ledgerId: string, catalogKey: string): Promise<void> {
    return request<void>(`${pinsBase(ledgerId)}/catalog/${encodeURIComponent(catalogKey)}`, {
        method: 'DELETE',
    });
}
