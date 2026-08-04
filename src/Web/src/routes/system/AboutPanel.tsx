import { useQuery } from '@tanstack/react-query';

import { fetchVersion } from '@/lib/api';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { cn } from '@/lib/cn';

// About panel (ADR-0044) — installation-wide version info, one row per layer:
//   UI  — build-time constants injected by Vite (vite-env.d.ts).
//   API — assembly attributes from GET /api/meta/version.
//   DB  — the latest applied migration, same endpoint.
// Matching UI/API build numbers confirm both were built from the same commit;
// a mismatch is a "restart the API after the merge" signal. (Was a modal off
// the sidebar's (i); now the About tab of System settings.)

const uiVersion = {
    version: __APP_VERSION__,
    build: __APP_BUILD__,
    commit: __APP_COMMIT__,
    commitDate: __APP_COMMIT_DATE__,
};

export function AboutPanel() {
    const versionQuery = useQuery({
        queryKey: ['version'],
        queryFn: fetchVersion,
        staleTime: Infinity,   // fixed for the life of the running processes
    });

    const api = versionQuery.data?.api;
    const db = versionQuery.data?.db;

    return (
        <section className="space-y-3">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">About</h2>
                <p className="text-sm text-text-muted">
                    Versions of each layer. Matching UI and API build numbers
                    confirm both were built from the same commit.
                </p>
            </header>
            <Panel>
                <PanelBody>
                    <dl className="flex flex-col gap-2 text-sm">
                        <VersionRow
                            label="UI"
                            value={buildLine(
                                uiVersion.version, uiVersion.build,
                                uiVersion.commit, uiVersion.commitDate)}
                        />
                        <VersionRow
                            label="API"
                            value={versionQuery.isPending
                                ? 'Loading…'
                                : versionQuery.isError || !api
                                  ? 'Unavailable'
                                  : buildLine(api.version, api.build, api.commit, api.commitDate)}
                            muted={versionQuery.isPending || versionQuery.isError || !api}
                        />
                        <VersionRow
                            label="DB"
                            value={versionQuery.isPending
                                ? 'Loading…'
                                : versionQuery.isError || !db
                                  ? 'Unavailable'
                                  : `schema ${db.schemaVersion} · ${db.script}`}
                            muted={versionQuery.isPending || versionQuery.isError || !db}
                        />
                    </dl>
                </PanelBody>
            </Panel>
        </section>
    );
}

// "version · build N · sha · date", dropping the date when there was no git
// checkout at build time (commitDate empty).
function buildLine(version: string, build: number, commit: string, commitDate: string): string {
    const parts = [version, `build ${build}`, commit];
    if (commitDate) parts.push(commitDate);
    return parts.join(' · ');
}

function VersionRow({ label, value, muted = false }: {
    label: string;
    value: string;
    muted?: boolean;
}) {
    return (
        <div className="flex items-baseline gap-3">
            <dt className="w-10 shrink-0 font-medium text-text-muted">{label}</dt>
            <dd className={cn('font-mono text-xs', muted ? 'text-text-subtle' : 'text-text')}>
                {value}
            </dd>
        </div>
    );
}
