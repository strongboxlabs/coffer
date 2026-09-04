import { useEffect, useRef, useState } from 'react';

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useRouterState } from '@tanstack/react-router';

import {
    deleteLedger,
    fetchVisibleLedgers,
    renameLedger,
    checkLedgerConsistency,
    repairProjection,
} from '@/lib/api';
import type { LedgerConsistencyReport, ProjectionConsistency, ConsistencyMismatch } from '@/lib/types';
import { errorMessage } from '@/lib/errorMessage';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';

/**
 * Settings → General. Ledger-level administration:
 *   • Identity — rename (stubbed; the rename API isn't built yet).
 *   • Maintenance — verify + heal balances (live). Moved here from the
 *     Bank feeds tab: drift can come from any writer, so the sweep is a
 *     ledger-wide maintenance action, not a feed-specific one.
 *   • Danger zone — delete the ledger (stubbed; the delete API isn't
 *     built yet).
 *
 * Rename / Delete are intentionally disabled placeholders so the shape of
 * the surface is visible; they wire up once their endpoints exist (ADR-0037).
 */
export function GeneralPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const navigate = useNavigate();
    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });
    const ledger = ledgersQuery.data?.find((l) => l.id === ledgerId);
    // Rename / delete are owner-only (the API enforces this too; the UI
    // disables them for editors/viewers so the affordance reads honestly).
    const isOwner = ledger?.role === 'owner';

    const [name, setName] = useState<string | null>(null);
    const effectiveName = name ?? ledger?.name ?? '';
    const [confirmingDelete, setConfirmingDelete] = useState(false);

    const renameMutation = useMutation({
        mutationFn: (next: string) => renameLedger(ledgerId, next),
        onSuccess: async () => {
            await queryClient.invalidateQueries({ queryKey: ['ledgers'] });
            setName(null); // fall back to the freshly-fetched name
        },
    });

    const deleteMutation = useMutation({
        mutationFn: () => deleteLedger(ledgerId),
        onSuccess: async () => {
            // The ledger (and its cached data) is gone — clear and land on
            // the picker, which offers Create ledger.
            queryClient.clear();
            navigate({ to: '/' });
        },
    });

    const trimmedName = effectiveName.trim();
    const nameChanged = ledger !== undefined && trimmedName !== ledger.name;
    const renameError = renameMutation.error
        ? errorMessage(renameMutation.error, 'Could not rename the ledger.')
        : null;
    const deleteError = deleteMutation.error
        ? errorMessage(deleteMutation.error, 'Could not delete the ledger.')
        : null;

    // The CHECK is read-only, so it invalidates nothing — running it must not
    // disturb an open register. It used to heal as a side effect of checking,
    // which is why checking and repairing are separate actions now.
    const consistencyMutation = useMutation({
        mutationFn: () => checkLedgerConsistency(ledgerId),
    });

    // Arriving from a drift notification runs the check once, so the finding hands
    // over its own fix instead of leaving the reader on a panel that shows nothing
    // until they press a button. The scheduled monitor already found the problem;
    // making them re-discover it by hand is the gap this closes.
    //
    // Deliberately NOT a plain mount effect. The check walks every position, so it
    // runs only when the URL asked for it, and the param is stripped immediately so a
    // refresh — or a back-navigation an hour later — does not silently re-run it.
    // Read from the LOCATION, not useSearch({ strict: false }): the typed hook resolves
    // against the nearest matched route, and hands back a search object without the
    // param wherever this panel is not itself the route component. The location is the
    // same in every case, and this panel is rendered inside a tab switch rather than
    // being routed to directly.
    const search = useRouterState({ select: (s) => s.location.search }) as {
        check?: boolean | string;
    };
    const requestedCheck = search.check === true || search.check === 'true';
    const consistencyRunRef = useRef(false);
    useEffect(() => {
        if (!requestedCheck || consistencyRunRef.current) return;
        consistencyRunRef.current = true;
        consistencyMutation.mutate();
        void navigate({ to: '.', search: {}, replace: true });
        // consistencyMutation is a stable mutation object; including it would re-run
        // this on every render of a pending mutation.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [requestedCheck, navigate]);

    // One repair per projection, because every projection the report names has a
    // repair — the UI must never show a problem with no way to fix it. A repair
    // rewrites stored rows, so the register surface refetches, and the check
    // re-runs so the user sees the result rather than being told to look again.
    const repairMutation = useMutation({
        mutationFn: (projection: string) => repairProjection(ledgerId, projection),
        onSuccess: () => {
            invalidateLedgerRegister(queryClient, ledgerId);
            consistencyMutation.mutate();
        },
    });

    return (
        <div className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">General</h2>
                <p className="text-sm text-text-muted">
                    Ledger identity, maintenance, and the danger zone.
                </p>
            </header>
            <Panel>
                <PanelHead>
                    <span className="font-medium">Ledger name</span>
                </PanelHead>
                <PanelBody className="space-y-3">
                    <div>
                        <FieldLabel htmlFor="ledger-name">Name</FieldLabel>
                        <Input
                            id="ledger-name"
                            className="mt-1"
                            value={effectiveName}
                            disabled={!isOwner || renameMutation.isPending}
                            onChange={(e) => setName(e.target.value)}
                        />
                    </div>
                    {renameError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {renameError}
                        </p>
                    ) : null}
                    <div className="flex items-center justify-between gap-3">
                        <p className="text-xs text-text-subtle">
                            {isOwner
                                ? 'Only an owner can rename this ledger.'
                                : 'Only an owner can rename this ledger — you have a non-owner role.'}
                        </p>
                        <Button
                            type="button"
                            variant="secondary"
                            disabled={
                                !isOwner ||
                                renameMutation.isPending ||
                                trimmedName.length === 0 ||
                                !nameChanged
                            }
                            onClick={() => renameMutation.mutate(trimmedName)}
                        >
                            {renameMutation.isPending ? 'Saving…' : 'Rename'}
                        </Button>
                    </div>
                </PanelBody>
            </Panel>

            <Panel>
                <PanelHead>
                    <span className="font-medium">Maintenance</span>
                </PanelHead>
                <PanelBody className="space-y-3">
                    <div className="flex items-start justify-between gap-3">
                        <p className="text-sm text-text-muted">
                            Compare every stored figure against a fresh calculation
                            from the transactions — balances, holdings, realized
                            gains and posting counts. Read-only: it reports what
                            disagrees and changes nothing.
                        </p>
                        <Button
                            type="button"
                            variant="secondary"
                            className="shrink-0"
                            onClick={() => consistencyMutation.mutate()}
                            disabled={consistencyMutation.isPending}
                        >
                            {consistencyMutation.isPending
                                ? 'Checking…'
                                : 'Check consistency'}
                        </Button>
                    </div>
                    {consistencyMutation.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(
                                consistencyMutation.error,
                                'Could not check consistency.',
                            )}
                        </p>
                    ) : null}
                    {consistencyMutation.data ? (
                        <ConsistencySummary
                            report={consistencyMutation.data}
                            onRepair={(projection) => repairMutation.mutate(projection)}
                            repairing={
                                repairMutation.isPending
                                    ? repairMutation.variables ?? null
                                    : null
                            }
                        />
                    ) : null}
                    {repairMutation.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(
                                repairMutation.error,
                                'Could not repair.',
                            )}
                        </p>
                    ) : null}
                </PanelBody>
            </Panel>

            <Panel className="border-state-danger/30">
                <PanelHead>
                    <span className="font-medium text-state-danger">Danger zone</span>
                </PanelHead>
                <PanelBody className="space-y-3">
                    <div className="flex items-center justify-between gap-3">
                        <div className="min-w-0">
                            <p className="text-sm font-medium">Delete this ledger</p>
                            <p className="mt-0.5 text-xs text-text-subtle">
                                Permanently removes the ledger and{' '}
                                <strong>all</strong> its accounts, transactions,
                                securities, backups, and history. This can't be
                                undone.
                            </p>
                        </div>
                        <Button
                            type="button"
                            variant="danger"
                            className="shrink-0"
                            disabled={!isOwner || deleteMutation.isPending}
                            title={isOwner ? undefined : 'Only an owner can delete this ledger'}
                            onClick={() => setConfirmingDelete(true)}
                        >
                            Delete ledger
                        </Button>
                    </div>
                    {deleteError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {deleteError}
                        </p>
                    ) : null}
                </PanelBody>
            </Panel>

            <ConfirmDialog
                open={confirmingDelete}
                title="Delete this ledger?"
                variant="danger"
                confirmLabel="Delete ledger"
                requireTypedConfirmation={ledger?.name}
                isConfirming={deleteMutation.isPending}
                body={
                    <>
                        This permanently deletes{' '}
                        <span className="font-medium text-text">{ledger?.name}</span>{' '}
                        and everything in it — accounts, transactions, securities,
                        snapshots, and backups. It cannot be undone.
                    </>
                }
                onConfirm={() => deleteMutation.mutate()}
                onCancel={() => setConfirmingDelete(false)}
            />
        </div>
    );
}

/** Human labels for the projection keys the API returns. */
const PROJECTION_LABELS: Record<string, string> = {
    balances: 'Running balances',
    holdings: 'Holdings and cost basis',
    realized_gains: 'Realized gains',
    posting_counts: 'Posting counts',
};

/**
 * The consistency report, one row per projection, each with its own repair.
 *
 * Every projection the report names is repairable — showing a problem with no way
 * to fix it is what left a data scrub's damage unrepaired for months while ad-hoc
 * SQL was written to look at it. Repair appears only where something disagrees, so
 * it is never the first button anyone presses.
 */
function ConsistencySummary({
    report,
    onRepair,
    repairing,
}: {
    report: LedgerConsistencyReport;
    onRepair: (projection: string) => void;
    repairing: string | null;
}) {
    return (
        <div className="space-y-2">
            {report.projections.map((p: ProjectionConsistency) => {
                const label = PROJECTION_LABELS[p.projection] ?? p.projection;
                const tone = p.healthy
                    ? 'border-state-success/40 bg-state-success-soft text-state-success'
                    : 'border-state-warning/40 bg-state-warning-soft text-state-warning';
                return (
                    <div key={p.projection} className={`rounded border p-3 text-sm ${tone}`}>
                        <div className="flex items-start justify-between gap-3">
                            <div>
                                <p className="font-medium">
                                    {label} —{' '}
                                    {p.healthy ? (
                                        <>
                                            healthy, <strong>{p.checked}</strong> checked
                                        </>
                                    ) : (
                                        <>
                                            <strong>{p.mismatchedCount}</strong> of{' '}
                                            {p.checked} disagree
                                        </>
                                    )}
                                </p>
                                {!p.healthy && p.mismatches.length > 0 ? (
                                    <ul className="mt-1 space-y-0.5 text-xs">
                                        {p.mismatches.slice(0, 5).map((m: ConsistencyMismatch, i: number) => (
                                            <li key={`${m.scope}-${m.field}-${i}`}>
                                                {m.scope} · {m.field}: stored {m.stored},
                                                expected {m.expected}
                                            </li>
                                        ))}
                                        {p.mismatchedCount > 5 ? (
                                            <li>
                                                …and {p.mismatchedCount - 5} more
                                            </li>
                                        ) : null}
                                    </ul>
                                ) : null}
                            </div>
                            {!p.healthy ? (
                                <Button
                                    type="button"
                                    variant="secondary"
                                    className="shrink-0"
                                    onClick={() => onRepair(p.projection)}
                                    disabled={repairing !== null}
                                >
                                    {repairing === p.projection
                                        ? 'Repairing…'
                                        : `Repair ${label.toLowerCase()}`}
                                </Button>
                            ) : null}
                        </div>
                    </div>
                );
            })}
        </div>
    );
}
