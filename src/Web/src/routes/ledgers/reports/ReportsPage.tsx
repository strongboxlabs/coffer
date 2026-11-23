import { useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { Link, useParams } from '@tanstack/react-router';

import { deleteSavedReport, fetchSavedReports, fetchVisibleLedgers } from '@/lib/api';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { IconButton } from '@/components/ui/IconButton';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';
import { Trash2 } from 'lucide-react';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';
import type { ReportSpec, SavedReport } from '@/types/report';

import { CATALOG } from './catalog';
import { ReportPinToggle } from './PinToggle';
import { describeRange } from './RangePicker';
import { specFromSearch } from './ReportView';
import { describeReturnsSpec, returnsSpecFromSearch } from './returnsModel';

function describeAggregationSpec(spec: ReportSpec): string {
    return `${spec.measure} · ${spec.groupBy.toLowerCase()} · ${describeRange(spec).toLowerCase()}`;
}

/**
 * `/ledgers/:ledgerId/reports` — the Reports destination (ADR-0104 D1).
 *
 * SAVED FIRST, catalog below. The catalog is a thing you read twice to learn
 * what exists; a memorized report is one you come back to every month, and
 * putting the two in one undifferentiated list makes the frequent case pay for
 * the rare one.
 *
 * The rejected alternative was listing every saved report in the RAIL, the way
 * accounts are listed. The reasoning held — a named, returned-to report is the
 * same kind of object as an account — but the mechanism does not scale: the
 * list is user-grown and unbounded, and the rail must always show accounts.
 * ADR-0104 D2 keeps the benefit by making pinning an explicit act instead.
 */
export function ReportsPage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };

    const ledgersQuery = useQuery({ queryKey: ['ledgers'], queryFn: fetchVisibleLedgers });
    const ledger = ledgersQuery.data?.find((l) => l.id === ledgerId);

    const savedQuery = useQuery({
        queryKey: ['saved-reports', ledgerId],
        queryFn: () => fetchSavedReports(ledgerId),
    });
    const saved = savedQuery.data ?? [];
    // A deleted report takes its card's Delete with it: focus goes to the next
    // card's Delete, else the previous card's, else the saved reports' own
    // region, which stays on the page when the last card goes: the list has no
    // add control (ADR-0023 §L).
    const savedRef = useRef<HTMLDivElement | null>(null);
    const deleteControl = useRowRemovalFocus(saved.map((r) => r.id), savedRef);

    // Creating something you cannot remove is a trap, so delete ships with
    // Save rather than after it.
    const [pendingDelete, setPendingDelete] = useState<SavedReport | null>(null);
    const queryClient = useQueryClient();
    const remove = useMutation({
        mutationFn: (id: string) => deleteSavedReport(ledgerId, id),
        onSuccess: () => {
            void queryClient.invalidateQueries({ queryKey: ['saved-reports', ledgerId] });
            setPendingDelete(null);
        },
    });

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    items={[
                        {
                            label: ledger?.name ?? 'Ledger',
                            node: (
                                <Link to="/ledgers/$ledgerId" params={{ ledgerId }} className="hover:underline">
                                    {ledger?.name ?? 'Ledger'}
                                </Link>
                            ),
                        },
                        { label: 'Reports' },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                <h1 className="text-xl font-semibold tracking-tight">Reports</h1>
                <div
                    ref={savedRef}
                    role="group"
                    aria-label="Saved reports"
                    tabIndex={-1}
                    className="focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                >
                <p className="mb-4 text-sm text-text-muted">
                    {saved.length === 0
                        ? 'Open a report, adjust it, and save it to keep it.'
                        : `${saved.length} saved`}
                </p>

                {saved.length > 0 ? (
                    <Section title="Saved">
                        {saved.map((report) => (
                            <SavedCard
                                key={report.id}
                                ledgerId={ledgerId}
                                report={report}
                                onDelete={() => setPendingDelete(report)}
                                deleteRef={deleteControl(report.id)}
                            />
                        ))}
                    </Section>
                ) : null}
                </div>

                <ConfirmDialog
                    open={pendingDelete !== null}
                    title="Delete this report?"
                    body={
                        pendingDelete?.visibility === 'ledger'
                            // Worth saying: a shared report is gone for
                            // everyone granted the ledger, not just its author.
                            ? `"${pendingDelete.name}" is shared — deleting it removes it for everyone on this ledger. The transactions it reports on are untouched.`
                            : `"${pendingDelete?.name}" will be removed. The transactions it reports on are untouched.`
                    }
                    confirmLabel="Delete"
                    variant="danger"
                    isConfirming={remove.isPending}
                    onConfirm={() => { if (pendingDelete) remove.mutate(pendingDelete.id); }}
                    onCancel={() => setPendingDelete(null)}
                />

                <Section title="Reports">
                    {CATALOG.map((report) => (
                        <div key={report.key} className="relative">
                        <Link
                            to="/ledgers/$ledgerId/reports/view"
                            params={{ ledgerId }}
                            // No `name`: the view names itself. A preset name
                            // carried in the URL went stale the moment the
                            // reader changed a control, so the heading kept
                            // claiming a report they had already left.
                            search={{ ...report.spec }}
                            className="flex min-w-0 flex-col gap-0.5 rounded border border-border bg-surface px-2.5 py-2 hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        >
                            <span className="truncate text-xs font-semibold text-text">{report.name}</span>
                            <span className="truncate text-[0.6875rem] text-text-subtle">{report.blurb}</span>
                        </Link>
                        {/* Outside the Link: a button nested in an anchor is
                            invalid, and clicking it would follow the link. */}
                        <span className="absolute top-1 right-1">
                            <ReportPinToggle
                                ledgerId={ledgerId}
                                target={{ kind: 'catalog', key: report.key }}
                                name={report.name}
                            />
                        </span>
                        </div>
                    ))}
                </Section>

                <p className="mt-6 max-w-prose text-[0.6875rem] leading-[1.5] text-text-subtle">
                    Net worth, account balances, asset allocation, cost basis and capital gains
                    are not here yet — they are served by a different part of the reporting
                    layer, which has no web surface so far. They will appear when it does.
                </p>
                </div>
            </MainPane>
        </MainArea>
    );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
    return (
        <section className="mb-5">
            <h2 className="mb-1.5 text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-text-muted">
                {title}
            </h2>
            <div className="grid gap-1.5 sm:grid-cols-2 lg:grid-cols-3">{children}</div>
        </section>
    );
}

function SavedCard({
    ledgerId, report, onDelete, deleteRef,
}: {
    ledgerId: string;
    report: SavedReport;
    onDelete: () => void;
    /** Given the card's Delete, which takes the card away (useRowRemovalFocus). */
    deleteRef: (el: HTMLElement | null) => void;
}) {
    // A returns row has no measure or group-by to print; it says what it shows.
    const subtitle = report.spec.kind === 'returns'
        ? describeReturnsSpec(returnsSpecFromSearch(report.spec))
        : describeAggregationSpec(specFromSearch(report.spec));
    return (
        <div className="relative">
        <Link
            to="/ledgers/$ledgerId/reports/view"
            params={{ ledgerId }}
            search={{ ...report.spec, saved: report.id, name: report.name }}
            className="flex min-w-0 flex-col gap-0.5 rounded border border-accent-soft bg-surface px-2.5 py-2 hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
            <span className="truncate text-xs font-semibold text-text">{report.name}</span>
            <span className="truncate text-[0.6875rem] text-text-subtle">{subtitle}</span>
            {/* Whether other people can see it is the only state worth showing
                before the report is opened — everything else is in the report. */}
            {report.visibility === 'ledger' ? (
                <span className="text-[0.625rem] font-semibold uppercase leading-[1.5] tracking-wider text-accent-soft-text">
                    Shared
                </span>
            ) : null}
        </Link>
        {/* Outside the Link, not inside it: a button nested in an anchor is
            invalid, and clicking it would also follow the link. */}
        <span className="absolute top-1 right-1 flex items-center">
            <ReportPinToggle
                ledgerId={ledgerId}
                target={{ kind: 'saved', id: report.id }}
                name={report.name}
            />
            <IconButton ref={deleteRef} aria-label={`Delete ${report.name}`} onClick={onDelete}>
                <Trash2 aria-hidden className="size-icon-sm" />
            </IconButton>
        </span>
        </div>
    );
}
