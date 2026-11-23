// Which report a home tile shows (ADR-0104 D2, as amended): the tile's widget id
// names it, and this resolves the name against the ledger's saved reports or the
// report catalog. The layout stores nothing else about the report, so a renamed
// report reads its new name and a deleted one is known to be gone.

import { useQuery } from '@tanstack/react-query';

import { fetchSavedReports } from '@/lib/api';
import type { ReportSpec, SavedReport } from '@/types/report';

import { CATALOG as REPORT_CATALOG, findCatalogReport } from '../reports/catalog';
import type { ReportSearch } from '../reports/ReportView';
import { parseReportTileId, reportTileId } from './dashboardCatalog';

/** The saved reports the reader can see, under the Reports page's own key. */
export function useSavedReports(ledgerId: string, enabled = true) {
    return useQuery({
        queryKey: ['saved-reports', ledgerId],
        queryFn: () => fetchSavedReports(ledgerId),
        enabled,
    });
}

/** A report a tile can show: its name, its question, and how Reports opens it. */
export interface TileReport {
    /** The tile's widget id. */
    id: string;
    name: string;
    spec: ReportSpec;
    /** The Reports view's search for this report, as Reports itself links to it. */
    search: ReportSearch;
}

/**
 * What a tile's id names: the report, `missing` when it names a report that no
 * longer exists (or never did), or `unknown` while the saved reports it would be
 * among have not been read.
 */
export type TileResolution =
    | { state: 'found'; report: TileReport }
    | { state: 'missing' }
    | { state: 'unknown' };

export function resolveTile(id: string, saved: readonly SavedReport[] | undefined): TileResolution {
    const ref = parseReportTileId(id);
    if (!ref) return { state: 'missing' };
    if (ref.kind === 'catalog') {
        const report = findCatalogReport(ref.key);
        return report ? { state: 'found', report: catalogTile(report) } : { state: 'missing' };
    }
    if (saved === undefined) return { state: 'unknown' };
    const report = saved.find((r) => r.id.toLowerCase() === ref.id);
    return report ? { state: 'found', report: savedTile(report) } : { state: 'missing' };
}

function savedTile(report: SavedReport): TileReport {
    return {
        id: reportTileId({ kind: 'saved', id: report.id }),
        name: report.name,
        spec: report.spec,
        // As a saved card links: the saved id, so Save there updates it, and its name.
        search: { ...report.spec, saved: report.id, name: report.name },
    };
}

function catalogTile(report: (typeof REPORT_CATALOG)[number]): TileReport {
    return {
        id: reportTileId({ kind: 'catalog', key: report.key }),
        name: report.name,
        spec: report.spec,
        // As the catalog card links: no name, so the view names itself.
        search: { ...report.spec },
    };
}

/** Every report a tile can show, in the order Reports lists them: saved, then the catalog. */
export function tileChoices(saved: readonly SavedReport[]): TileReport[] {
    return [...saved.map(savedTile), ...REPORT_CATALOG.map(catalogTile)];
}
