import type {
    PinnedReport,
    ReportResult,
    ReportSpec,
    ReturnsSeriesResult,
    SavedReport,
    SavedReportVisibility,
} from '../../types/report';
import { request } from './_request';
import { withToday } from './_today';

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
    if (spec.kind === 'returns') {
        // The returns endpoint's own parameters: an interval (never a day), one
        // account or the ledger, and whether each account is drawn beside it.
        p.set('interval', returnsInterval(spec.bucket));
        if (spec.accountId) p.set('accountId', spec.accountId);
        p.set('includeAccounts', String(!spec.accountId && spec.includeAccounts !== false));
        // The benchmark rides with the spec whatever the view: the Gain view hides the
        // control and keeps the pick (ADR-0114 D1).
        if (spec.benchmark) p.set('benchmark', spec.benchmark);
    } else {
        p.set('measure', spec.measure);
        p.set('groupBy', spec.groupBy);
        if (spec.bucket !== 'None') p.set('bucket', spec.bucket);
    }

    if (spec.dateMode === 'Sliding') {
        p.set('dateMode', 'Sliding');
        if (spec.windowCount !== undefined) p.set('windowCount', String(spec.windowCount));
        if (spec.windowUnit) p.set('windowUnit', spec.windowUnit);
        if (spec.windowAlign) p.set('windowAlign', 'true');
    } else {
        if (spec.from) p.set('from', spec.from);
        if (spec.to) p.set('to', spec.to);
    }
    if (spec.kind === 'returns') return p.toString();

    if (spec.tagId) p.set('tagId', spec.tagId);
    if (spec.rollup) p.set('rollup', 'true');
    if (spec.topN !== undefined) p.set('topN', String(spec.topN));
    return p.toString();
}

/**
 * Run a report. A GET, so the result is linkable and reloadable. The user's local
 * date rides along, never in the spec a save persists: a sliding window then ends
 * where the register's Scheduled rows begin.
 */
export function fetchReportSummary(
    ledgerId: string,
    spec: ReportSpec,
): Promise<ReportResult> {
    const query = withToday(new URLSearchParams(specToQuery(spec)));
    return request<ReportResult>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/reports/summary?${query.toString()}`,
    );
}

/** A returns interval from the spec's bucket. The report has no day and no "none": anything else is a month. */
function returnsInterval(bucket: ReportSpec['bucket']): 'month' | 'quarter' | 'year' {
    return bucket === 'Quarter' ? 'quarter' : bucket === 'Year' ? 'year' : 'month';
}

/**
 * Run the Returns report: a GET like the summary, with the user's today beside it.
 * A `benchmark` that is not a symbol's shape is 400; a well-formed symbol the ledger
 * no longer holds is answered, with `benchmark_not_in_ledger` (ADR-0114 D1).
 */
export function fetchReturnsSeries(
    ledgerId: string,
    spec: ReportSpec,
): Promise<ReturnsSeriesResult> {
    const query = withToday(new URLSearchParams(specToQuery(spec)));
    return request<ReturnsSeriesResult>(
        `/api/ledgers/${encodeURIComponent(ledgerId)}/reports/returns-series?${query.toString()}`,
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
 *
 * A spec whose `benchmark` is neither absent, null nor a symbol's shape is refused
 * with 400 on create and patch alike; it is not checked against the ledger's list.
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
