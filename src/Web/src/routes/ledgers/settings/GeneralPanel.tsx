import { useEffect, useRef, useState } from 'react';

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useRouterState } from '@tanstack/react-router';

import {
    deleteLedger,
    fetchLedgerEvents,
    fetchVisibleLedgers,
    renameLedger,
    checkLedgerConsistency,
    repairProjection,
} from '@/lib/api';
import type { LedgerConsistencyReport, ProjectionConsistency, ConsistencyMismatch } from '@/lib/types';
import { errorMessage } from '@/lib/errorMessage';
import { formatLedgerDateTime } from '@/lib/dates';
import { formatCurrency } from '@/lib/money';
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

    // What the SCHEDULED monitor last found, so this panel can say so on arrival.
    //
    // The finding used to live only on the Notifications tab. That assumed the
    // reader arrives by following the link there — but the alert arrives out of
    // band (healthchecks.io, email), and someone acting on it opens Settings and
    // lands HERE, on the default tab, where the page looked entirely healthy. An
    // alert the product sends and then does not acknowledge in its own UI reads as
    // a false alarm, which is how a real finding gets ignored.
    //
    // Cheap: a bounded read of the event log, not the consistency walk. Running
    // the walk itself on mount is what the `check` param exists to avoid.
    const eventsQuery = useQuery({
        queryKey: ['ledger-events', ledgerId],
        queryFn: () => fetchLedgerEvents(ledgerId),
    });
    // resolvedAt is derived server-side from the surrounding rows, so a later
    // consistency.ok clears this without anything mutating the append-only log.
    const unresolvedDrift = eventsQuery.data?.find(
        (e) => e.eventKey === 'consistency.drift' && e.resolvedAt === null,
    );

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
                    {/* Only while unresolved. Repeating a finding the log has since
                        called healthy would train the reader to dismiss the banner,
                        which is the same way the original alert stopped landing. */}
                    {unresolvedDrift ? (
                        <div
                            role="status"
                            className="rounded border border-state-warning/40 bg-state-warning-soft p-3 text-sm text-state-warning"
                        >
                            <p className="font-medium">
                                The scheduled check reported drift on{' '}
                                <time dateTime={unresolvedDrift.occurredAt}>
                                    {formatLedgerDateTime(unresolvedDrift.occurredAt)}
                                </time>
                                , and nothing has reported it healthy since.
                            </p>
                            <p className="mt-1 text-xs">
                                {unresolvedDrift.summary} Run the check to see which
                                figures disagree — each one it names can be repaired
                                from here.
                            </p>
                        </div>
                    ) : null}
                    <div className="flex items-start justify-between gap-3">
                        <p className="text-sm text-text-muted">
                            Compare every stored figure against a fresh calculation
                            from the transactions — balances, holdings, realized
                            gains, posting counts and prices from trades. Read-only:
                            it reports what disagrees and changes nothing.
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
                            ledgerId={ledgerId}
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
    trade_prices: 'Prices from trades',
};

/**
 * What pressing Repair will actually DO, per projection.
 *
 * A report that names a disagreement and offers a button gives the reader no way to
 * judge whether pressing it is safe — "stored 20.3898, expected 0.9868" is a fact,
 * not a decision. These say which direction the write goes and what it cannot touch,
 * so the choice can be made without reading the migration.
 */
const REPAIR_EFFECT: Record<string, string> = {
    balances: 'Repair replays each account from its opening balance and rewrites the running balances. It does not change any transaction.',
    holdings: 'Repair replays the FIFO walk and rewrites quantity and cost basis for the positions listed. It does not change any transaction.',
    realized_gains: 'Repair replays the FIFO walk and rewrites the realized gain rows for the positions listed. It does not change any transaction.',
    posting_counts: 'Repair recounts the postings on each header listed. It does not change any amount.',
    trade_prices: "Repair writes the expected price — what the day's last trade implies — over the stored one. A price you entered by hand, or one from a quote feed, outranks a trade and is never overwritten. Rows saying no trade implies the price any more are NOT repaired here: some are worth keeping, so open the security and decide in its price list. It does not change any transaction.",
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
    ledgerId,
    report,
    onRepair,
    repairing,
}: {
    ledgerId: string;
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
                                    <>
                                        {/* Every disagreement the report returned,
                                            not a sample of five. Someone deciding
                                            whether to repair is deciding about ALL
                                            of them, and "…and 1 more" hides the one
                                            that might be the reason not to — the
                                            list is already bounded server-side.
                                            Scrolls rather than growing forever. */}
                                        <ul className="mt-1 max-h-fixed-256px space-y-0.5 overflow-y-auto text-xs">
                                            {p.mismatches.map((m: ConsistencyMismatch, i: number) => (
                                                <li key={`${m.scope}-${m.field}-${i}`}>
                                                    {/* Where the report can name the
                                                        transaction behind the figure,
                                                        it links to it. Judging whether
                                                        to repair means looking at that
                                                        trade, and retyping a date into
                                                        a register to find it is work
                                                        the row already has the answer
                                                        to. */}
                                                    {m.accountId && m.headerId ? (
                                                        <Link
                                                            to="/ledgers/$ledgerId/accounts/$accountId"
                                                            params={{
                                                                ledgerId,
                                                                accountId: m.accountId,
                                                            }}
                                                            search={{ focus: m.headerId }}
                                                            className="underline underline-offset-2 hover:opacity-80"
                                                        >
                                                            {m.scope}
                                                        </Link>
                                                    ) : m.securityId ? (
                                                        /* No transaction behind this one — an orphaned price.
                                                           The remedy is in the security's price list, so that
                                                           is where the row points. */
                                                        <Link
                                                            to="/ledgers/$ledgerId/securities/$securityId"
                                                            params={{
                                                                ledgerId,
                                                                securityId: m.securityId,
                                                            }}
                                                            className="underline underline-offset-2 hover:opacity-80"
                                                        >
                                                            {m.scope}
                                                        </Link>
                                                    ) : (
                                                        m.scope
                                                    )}{' '}
                                                    · {m.field}
                                                    {/* Only where there IS a difference. An orphan reports the
                                                        stored price as both, because nothing derives it — so
                                                        "stored X, expected X" would be noise dressed as a
                                                        finding. */}
                                                    {m.stored === m.expected
                                                        ? null
                                                        : `: stored ${m.stored}, expected ${m.expected}`}
                                                </li>
                                            ))}
                                        </ul>
                                        {p.mismatchedCount > p.mismatches.length ? (
                                            <p className="mt-1 text-xs">
                                                Showing {p.mismatches.length} of{' '}
                                                {p.mismatchedCount}. Repair fixes all of
                                                them, not only the ones listed.
                                            </p>
                                        ) : null}
                                        <p className="mt-2 text-xs">
                                            {REPAIR_EFFECT[p.projection] ??
                                                'Repair recalculates these figures from the transactions and stores the result.'}
                                        </p>
                                    </>
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

            {/* ADVISORY, and styled as one: neutral, below the findings, no
                count in any headline and no button. A price nothing derives may
                be perfectly correct — security_prices records THAT a trade wrote
                a row and never WHICH one, so this is inferred, and ADR-0084 D4
                keeps the price a deleted trade left behind on purpose. Reporting
                it as a defect marked a sound ledger broken with 130 rows nobody
                could act on. */}
            {report.unbackedPrices.length > 0 ? (
                <div className="rounded border border-border bg-surface-muted p-3 text-sm">
                    <p className="font-medium text-text">Prices worth a look</p>
                    <p className="mt-1 text-xs text-text-muted">
                        These securities have prices recorded as coming from a trade,
                        but no transaction currently produces them — usually because
                        the trade was deleted or edited since. They may still be
                        correct. Open a security to see its prices; editing or
                        deleting one takes it off this list.
                    </p>
                    <ul className="mt-2 max-h-fixed-256px space-y-0.5 overflow-y-auto text-xs">
                        {report.unbackedPrices.map((u) => (
                            <li key={u.securityId}>
                                <Link
                                    to="/ledgers/$ledgerId/securities/$securityId"
                                    params={{ ledgerId, securityId: u.securityId }}
                                    className="text-accent underline underline-offset-2 hover:text-accent-hover"
                                >
                                    {u.security}
                                </Link>{' '}
                                <span className="text-text-muted">
                                    · {u.count} price{u.count === 1 ? '' : 's'}
                                    {u.earliest === u.latest
                                        ? ` on ${u.earliest}`
                                        : `, ${u.earliest} to ${u.latest}`}
                                    {u.holdingValue > 0
                                        ? ` · you hold ${formatCurrency(u.holdingValue)}`
                                        : ' · no longer held'}
                                </span>
                            </li>
                        ))}
                    </ul>
                </div>
            ) : null}
        </div>
    );
}
