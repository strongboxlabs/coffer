import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';

import {
    deleteLedger,
    fetchVisibleLedgers,
    renameLedger,
    verifyBalanceHealth,
} from '@/lib/api';
import type { BalanceHealthReport } from '@/lib/types';
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

    // Verify-and-heal balance sweep (POST /balances/health). One round-trip
    // per account; the recompute side-effect heals any drift in place. Sits
    // on the ledger scope, not per-connection — drift can come from any writer.
    const balanceHealthMutation = useMutation({
        mutationFn: () => verifyBalanceHealth(ledgerId),
        onSuccess: () => {
            // Drift heal rewrote balance rows — refresh the register surface. A
            // mounted register now reloads immediately via the ADR-0079 canonical
            // key (this already invalidated ['register', ledgerId], but nothing
            // honored it, so an open register stayed stale), and account balances
            // refetch too.
            invalidateLedgerRegister(queryClient, ledgerId);
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
                            Verify every stored balance against a fresh recompute
                            and heal any drift in place. Ledger-wide; safe to run
                            anytime.
                        </p>
                        <Button
                            type="button"
                            variant="secondary"
                            className="shrink-0"
                            onClick={() => balanceHealthMutation.mutate()}
                            disabled={balanceHealthMutation.isPending}
                        >
                            {balanceHealthMutation.isPending
                                ? 'Verifying…'
                                : 'Verify balances'}
                        </Button>
                    </div>
                    {balanceHealthMutation.isError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(
                                balanceHealthMutation.error,
                                'Could not verify balances.',
                            )}
                        </p>
                    ) : null}
                    {balanceHealthMutation.data ? (
                        <BalanceHealthSummary report={balanceHealthMutation.data} />
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

function BalanceHealthSummary({ report }: { report: BalanceHealthReport }) {
    // verify-and-heal result. Healthy → green badge with the rows checked.
    // Drift detected → warning palette with the per-row breakdown so the user
    // can see WHICH balance row was stale and by how much. The recompute
    // side-effect already healed; this panel is informational + diagnostic.
    if (report.healthy) {
        return (
            <div className="rounded border border-state-success/40 bg-state-success-soft p-3 text-sm text-state-success">
                <p className="font-medium">
                    Balances healthy — <strong>{report.rowsChecked}</strong>{' '}
                    row{report.rowsChecked === 1 ? '' : 's'} verified across{' '}
                    <strong>{report.accountsChecked}</strong> account
                    {report.accountsChecked === 1 ? '' : 's'}.
                </p>
            </div>
        );
    }
    return (
        <div className="rounded border border-state-warning/40 bg-state-warning-soft p-3 text-sm text-state-warning">
            <p className="font-medium">
                Healed <strong>{report.driftedCount}</strong> drifted balance
                row{report.driftedCount === 1 ? '' : 's'} of{' '}
                {report.rowsChecked} checked.
            </p>
            <p className="mt-1 text-xs">
                The values below were stored incorrectly and have now been
                recomputed in place.
            </p>
            <table className="mt-2 w-full text-xs">
                <thead className="text-left opacity-80">
                    <tr>
                        <th className="pr-3 font-medium">Account</th>
                        <th className="pr-3 font-medium">Posted</th>
                        <th className="pr-3 text-right font-medium">Stored</th>
                        <th className="pr-3 text-right font-medium">Corrected</th>
                        <th className="text-right font-medium">Diff</th>
                    </tr>
                </thead>
                <tbody>
                    {report.drifted.map((d) => (
                        <tr key={d.headerId}>
                            <td className="pr-3">{d.accountName}</td>
                            <td className="pr-3">{d.postedAt.slice(0, 10)}</td>
                            <td className="pr-3 text-right tabular-nums">
                                {d.storedBefore.toFixed(2)}
                            </td>
                            <td className="pr-3 text-right tabular-nums">
                                {d.recomputedAfter.toFixed(2)}
                            </td>
                            <td className="text-right tabular-nums">
                                {d.diff > 0 ? '+' : ''}
                                {d.diff.toFixed(2)}
                            </td>
                        </tr>
                    ))}
                </tbody>
            </table>
        </div>
    );
}
