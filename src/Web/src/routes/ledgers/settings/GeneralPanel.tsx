import { useEffect, useRef, useState, type ReactNode, type RefObject } from 'react';

import { useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { Link, useNavigate, useRouterState } from '@tanstack/react-router';

import {
    ApiError,
    deleteLedger,
    fetchLedgerEvents,
    renameLedger,
    checkLedgerConsistency,
    repairProjection,
} from '@/lib/api';
import type { LedgerConsistencyReport, ProjectionConsistency, ConsistencyMismatch } from '@/lib/types';
import type { UnfitAmountAdvisory, UnfitAmountReason } from '@/lib/types/ledger';
import { monthLabel } from '@/lib/calendar';
import { errorMessage } from '@/lib/errorMessage';
import { formatLedgerDate, formatLedgerDateTime } from '@/lib/dates';
import { formatCurrency, formatShares } from '@/lib/money';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { NoRate } from '../home/Money';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';
import { PendingLabel } from '@/components/ui/PendingLabel';
import { PendingScope } from '@/components/ui/pending';
import { useFocusWithinHandOff, useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';

import { ReportingCurrencyPanel } from './ReportingCurrencyPanel';

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
    // Rename / delete are owner-only (the API enforces this too), so anyone else
    // is offered neither (ADR-0023 §L): the name reads as text, and the danger
    // zone, which holds only the delete, is not drawn.
    // A repair is an owner's or an editor's: the server refuses anyone else's
    // before it rewrites anything, so a viewer is offered none.
    const { ledger, isOwner, canEdit } = useLedgerRole(ledgerId);

    const [name, setName] = useState<string | null>(null);
    const effectiveName = name ?? ledger?.name ?? '';
    // A rename that lands leaves the name unchanged from the one stored, which
    // disables Rename with focus on it; focus goes to the name field it waits
    // on (ADR-0023 §L).
    const nameRef = useRef<HTMLInputElement>(null);
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

    // What the last check or repair found, whichever answered last. A repair answers
    // every projection checked again, so its report replaces the check's without the
    // ledger being walked a second time.
    const [report, setReport] = useState<LedgerConsistencyReport | null>(null);
    // The last repair that answered no report: its projection, and what is known of it.
    // 'unchecked': its rewrite finished and its check did not, so what it set out to
    // rewrite stands. 'failed': its rewrite failed partway, and the part it rewrote stands.
    // 'unknown': no answer came back from it, and the server runs a repair that has begun
    // to the end whatever happens to the client, so it may still be running, and it refuses
    // another repair of the ledger until it has finished. Each way the
    // report taken before the repair may no longer describe the data, and what still
    // disagrees is not known until the next check.
    const [unreportedRepair, setUnreportedRepair] = useState<
        { projection: string; outcome: 'unchecked' | 'failed' | 'unknown' } | null
    >(null);
    // What the last repair could not rewrite, with why: only the quote currencies' repair
    // names any, a security Yahoo could not be asked about or did not answer for
    // (ADR-0114 D5). Shown beside the report until the next check or repair.
    const [notRepaired, setNotRepaired] = useState<
        { projection: string; items: ConsistencyMismatch[] } | null
    >(null);
    const unreportedLabel = unreportedRepair === null
        ? ''
        : PROJECTION_LABELS[unreportedRepair.projection] ?? unreportedRepair.projection;

    // The CHECK changes no figure, so it refreshes nothing of the register — running it
    // must not disturb an open register. Checking does not heal; repairing is a separate
    // action. It records its verdict, though, as the scheduled check does, so the reads
    // that show a verdict refetch. A new check clears the old report at the press. The
    // error of a repair before it goes as the check starts: it would otherwise stay with no
    // report beside it while the check runs, and under the check's result after, with
    // nothing to say which action it came from. The check's result, a report or its own
    // error, resets the repair, so that error does not come back.
    const consistencyMutation = useMutation({
        mutationFn: () => checkLedgerConsistency(ledgerId),
        onMutate: () => {
            setReport(null);
            setUnreportedRepair(null);
            setNotRepaired(null);
        },
        onSuccess: (checked) => {
            setReport(checked);
            invalidateVerdictReads(queryClient, ledgerId);
        },
        onSettled: () => {
            repairMutation.reset();
        },
    });

    // What a consistency check last found, so this panel can say so on arrival.
    //
    // Not only on the Notifications tab: that assumes the reader arrives by
    // following the link there — but the alert arrives out of band
    // (healthchecks.io, email), and someone acting on it opens Settings and lands
    // HERE, on the default tab, which would otherwise look entirely healthy. An
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
    // repair — the UI must never show whoever can write a problem with no way to fix it.
    // A repair rewrites stored rows, so the register surface refetches. It answers every
    // projection checked again and records that verdict, so the report shown becomes
    // its report, and the reads that show a verdict refetch. A repair whose check did not
    // finish answers no report: the one on screen was taken before the repair, so it goes,
    // and the panel says what is known. A repair that fails partway answers a 500, the
    // only error the server sends once the rewrite has begun, but what it rewrote before
    // failing stands and the server clears the verdict: the report on screen goes the same
    // way, with its Repair buttons, the error stays shown, and the same reads refetch. The
    // next check brings a report, and its Repair buttons, back. Any other error is no answer
    // from the repair (the connection dropped, or a proxy answered in the server's place:
    // 502, 503, 504, or a timeout of its own), and the server carries a repair that has
    // begun on to the end: the report goes the same way, and the panel says the repair may
    // still be running and is recorded when it finishes, rather than that it did not finish.
    // A repair refused before it starts answers a 4xx (a role that cannot write, a
    // projection the server does not know, a ledger not visible, a session that has
    // expired, a date that does not bind, another repair of the ledger still running) and
    // rewrites nothing: the report on screen still describes the data, so it stays, with
    // its Repair buttons, beside the refusal. A repair stopped because another change to
    // the ledger held it past the wait answers a 422 too (ledger-write-busy); what it
    // repaired before stands, which the server's sentence says, with repairing again as
    // the way to finish, so the report stays beside that sentence the same way.
    const repairMutation = useMutation({
        mutationFn: (projection: string) => repairProjection(ledgerId, projection),
        onMutate: () => {
            setNotRepaired(null);
        },
        onSuccess: (repaired) => {
            setNotRepaired(
                repaired.notRepaired.length > 0
                    ? { projection: repaired.projection, items: repaired.notRepaired }
                    : null,
            );
            setReport(repaired.after);
            setUnreportedRepair(
                repaired.after === null
                    ? { projection: repaired.projection, outcome: 'unchecked' }
                    : null,
            );
        },
        onError: (error, projection) => {
            if (error instanceof ApiError && error.status < 500) return;
            setReport(null);
            setUnreportedRepair({
                projection,
                outcome: error instanceof ApiError && error.status === 500 ? 'failed' : 'unknown',
            });
        },
        onSettled: () => {
            invalidateLedgerRegister(queryClient, ledgerId);
            invalidateVerdictReads(queryClient, ledgerId);
        },
    });

    // A repair that fails partway, answers nothing, or whose check after it did not finish
    // takes the report away with every control in it: the Repair pressed, another Repair
    // (pending, still a Tab stop), a link to what disagrees. Focus goes from whichever of
    // them held it to Check consistency, the panel's next step, never to the page
    // (ADR-0023 §L). Only this render takes the report away, so the hand-off is here; a
    // Repair that goes while the report stays is ConsistencySummary's.
    const checkButtonRef = useRef<HTMLButtonElement>(null);
    const reportHandOff = useFocusWithinHandOff(() => checkButtonRef.current);

    return (
        <div className="space-y-4">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">General</h2>
                <p className="text-sm text-text-muted">
                    {isOwner
                        ? 'Ledger identity, reporting currency, maintenance, and the danger zone.'
                        : 'Ledger identity, reporting currency, and maintenance.'}
                </p>
            </header>
            <Panel>
                <PanelHead>
                    <span className="font-medium">Ledger name</span>
                </PanelHead>
                {isOwner ? (
                <PanelBody className="space-y-3">
                    <div>
                        <FieldLabel htmlFor="ledger-name">Name</FieldLabel>
                        <Input
                            ref={nameRef}
                            id="ledger-name"
                            className="mt-1"
                            value={effectiveName}
                            pending={renameMutation.isPending}
                            onChange={(e) => setName(e.target.value)}
                        />
                    </div>
                    {renameError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {renameError}
                        </p>
                    ) : null}
                    <div className="flex items-center justify-between gap-3">
                        <p className="text-xs text-text-subtle">Only an owner can rename this ledger.</p>
                        <Button
                            type="button"
                            variant="secondary"
                            disabled={trimmedName.length === 0 || !nameChanged}
                            pending={renameMutation.isPending}
                            fallbackFocusRef={nameRef}
                            onClick={() => renameMutation.mutate(trimmedName)}
                            pendingLabel="Saving…"
                        >
                            Rename
                        </Button>
                    </div>
                </PanelBody>
                ) : (
                    // The name, read: nothing to change it with.
                    <PanelBody>
                        <p className="text-sm" data-ledger-name>{ledger?.name ?? ''}</p>
                    </PanelBody>
                )}
            </Panel>

            <ReportingCurrencyPanel ledgerId={ledgerId} />

            <Panel>
                <PanelHead>
                    <span className="font-medium">Maintenance</span>
                </PanelHead>
                <PanelBody className="space-y-3">
                    {/* Check consistency and every Repair exclude each other: a check that
                        began while a repair rewrote would read the ledger partway through
                        the rewrite, and its report, answering after the repair, would
                        replace what the repair answered. One group, so a press of either
                        holds the other from the press (pending.ts). */}
                    <PendingScope>
                    {/* Only while unresolved. Repeating a finding the log has since
                        called healthy would train the reader to dismiss the banner,
                        which is the same way the original alert stopped landing. */}
                    {unresolvedDrift ? (
                        <div
                            role="status"
                            className="rounded border border-state-warning/40 bg-state-warning-soft p-3 text-sm text-state-warning"
                        >
                            <p className="font-medium">
                                A consistency check reported drift on{' '}
                                <time dateTime={unresolvedDrift.occurredAt}>
                                    {formatLedgerDateTime(unresolvedDrift.occurredAt)}
                                </time>
                                , and nothing has reported it healthy since.
                            </p>
                            <p className="mt-1 text-xs">
                                {unresolvedDrift.summary} Run the check to see which
                                figures disagree{canEdit ? ' — each one it names can be repaired from here' : ''}.
                            </p>
                        </div>
                    ) : null}
                    <div className="flex items-start justify-between gap-3">
                        <p className="text-sm text-text-muted">
                            Compare every stored figure with what it is derived
                            from: the running balances, holdings and cost basis,
                            realized gains, posting counts, prices from trades and
                            memos and tags shown against the transactions, each
                            brokerage&apos;s holdings currency against the
                            brokerage&apos;s own, and the currency of each fetched
                            price against the one Yahoo Finance names; and look for
                            the reminder auto-post job wherever a reminder is set to
                            auto-post. It reports what disagrees and changes no
                            figure; the Overview shows what it found.
                        </p>
                        {/* It waits on a repair in flight too, as every Repair waits on
                            the one in flight; Checking… is its own check's. */}
                        <Button
                            ref={checkButtonRef}
                            type="button"
                            variant="secondary"
                            className="shrink-0"
                            onClick={() => consistencyMutation.mutate()}
                            pending={consistencyMutation.isPending || repairMutation.isPending}
                        >
                            <PendingLabel
                                pending={consistencyMutation.isPending}
                                label="Check consistency"
                                pendingLabel="Checking…"
                            />
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
                    {report ? (
                        <ConsistencySummary
                            ledgerId={ledgerId}
                            report={report}
                            canEdit={canEdit}
                            onRepair={(projection) => repairMutation.mutate(projection)}
                            repairing={
                                repairMutation.isPending
                                    ? repairMutation.variables ?? null
                                    : null
                            }
                            containerRef={reportHandOff}
                            checkRef={checkButtonRef}
                        />
                    ) : null}
                    {notRepaired !== null ? (
                        <NotRepairedList projection={notRepaired.projection} items={notRepaired.items} ledgerId={ledgerId} />
                    ) : null}
                    {unreportedRepair !== null ? (
                        <p role="status" className="text-sm text-text-muted">
                            {unreportedRepair.outcome === 'unchecked' ? (
                                <>
                                    Repaired {unreportedLabel}.
                                    The check after the repair did not finish, so what still
                                    disagrees is not known until the ledger is checked again.
                                </>
                            ) : unreportedRepair.outcome === 'failed' ? (
                                <>
                                    The repair of {unreportedLabel.toLowerCase()} did not
                                    finish, and part of it may stand. Check consistency
                                    shows what still disagrees.
                                </>
                            ) : (
                                <>
                                    No answer came back from the repair of{' '}
                                    {unreportedLabel.toLowerCase()}. It may still be running,
                                    and it is recorded when it finishes. Check consistency
                                    shows where things stand.
                                </>
                            )}
                        </p>
                    ) : null}
                    {/* A check in flight has taken away the report or the status this
                        error stood beside, so the error goes in the render that draws
                        Checking…; the check resets the repair when it answers. */}
                    {repairMutation.isError && !consistencyMutation.isPending ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {repairMutation.error instanceof ApiError
                                && repairMutation.error.code === 'consistency-repair-needs-yahoo' ? (
                                <>
                                    This repair asks Yahoo Finance for each security&apos;s
                                    currency, and Yahoo Finance is off for you on this ledger.
                                    Nothing was changed. Turn it on in{' '}
                                    <Link
                                        to="/ledgers/$ledgerId/settings"
                                        params={{ ledgerId }}
                                        search={{ tab: 'quotes' }}
                                        className="font-medium underline underline-offset-2 hover:opacity-80"
                                    >
                                        Quotes
                                    </Link>
                                    , then repair again.
                                </>
                            ) : (
                                errorMessage(
                                    repairMutation.error,
                                    'Could not repair.',
                                )
                            )}
                        </p>
                    ) : null}
                    </PendingScope>
                </PanelBody>
            </Panel>

            {isOwner ? (
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
                            pending={deleteMutation.isPending}
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
            ) : null}

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

/**
 * The reads that show the ledger's consistency verdict, which a check and a repair
 * both record: the Overview's Problems widget (the stored verdict's "checked" time and
 * the outstanding problems), the navigation's attention dots, and this panel's event
 * log. Nothing of the register.
 */
function invalidateVerdictReads(queryClient: QueryClient, ledgerId: string): void {
    void queryClient.invalidateQueries({ queryKey: ['home', ledgerId, 'consistency-state'] });
    void queryClient.invalidateQueries({ queryKey: ['home', ledgerId, 'outstanding'] });
    void queryClient.invalidateQueries({ queryKey: ['ledger-attention'] });
    void queryClient.invalidateQueries({ queryKey: ['ledger-events', ledgerId] });
}

/** The `reason` of a finding for a row stored where the rule derives none: a running
 *  balance on a row the register skips, which the balances repair removes
 *  (`ConsistencyReasons.NotDerived` in the API). */
const NOT_DERIVED = 'extra';

/** Human labels for the projection keys the API returns. */
const PROJECTION_LABELS: Record<string, string> = {
    balances: 'Running balances',
    holdings: 'Holdings and cost basis',
    realized_gains: 'Realized gains',
    posting_counts: 'Posting counts',
    trade_prices: 'Prices from trades',
    stored_notes: 'Memos and tags shown',
    holdings_currency: 'Holdings currency',
    quote_currency: 'Fetched price currencies',
    category_amounts: 'Category amounts',
    reminder_auto_post: 'Reminder auto-post',
};

/** The kind of a fetched-price currency finding (ADR-0114 D5): a security whose
 *  currency Yahoo Finance has not confirmed since prices were all stamped USD. */
const CURRENCY_UNCONFIRMED = 'unconfirmed';

/**
 * Why the quote currencies' repair left a security as it was, by the code the server
 * names (its Yahoo lookup's, or that it could not be asked). A code not listed shows
 * the server's own words.
 */
const NOT_REPAIRED_REASONS: Record<string, string> = {
    'quote-symbol-not-public': 'its symbol is not public, so Yahoo Finance was not asked',
    'rate-limited': 'Yahoo Finance was limiting requests; repair again later',
    'fetch-failed': 'Yahoo Finance could not be reached; repair again later',
    'parse-failed': 'Yahoo Finance answered with something Coffer could not read; repair again later',
    'ticker-not-resolved': 'Yahoo Finance does not know its symbol',
    'currency-missing': 'Yahoo Finance names no currency for it',
    'currency-unknown': 'Yahoo Finance names a currency Coffer does not recognise',
    'currency-invalid': 'Yahoo Finance names a currency Coffer does not recognise',
};

/**
 * What pressing Repair will actually DO, per projection.
 *
 * A report that names a disagreement and offers a button gives the reader no way to
 * judge whether pressing it is safe — a stored figure beside an expected one is a
 * fact, not a decision. These say which direction the write goes and what it cannot touch,
 * so the choice can be made without reading the migration.
 */
const REPAIR_EFFECT: Record<string, string> = {
    balances: 'Repair replays each account from its opening balance, rewrites the running balances, and removes any running balance stored on a row the register skips. It does not change any transaction.',
    holdings: 'Repair replays the FIFO walk and rewrites quantity and cost basis for the positions listed. It does not change any transaction.',
    realized_gains: 'Repair replays the FIFO walk and rewrites the realized gain rows for the positions listed. It does not change any transaction.',
    posting_counts: 'Repair recounts the postings on each header listed. It does not change any amount.',
    trade_prices: "Repair writes the expected price — what the day's last trade implies, in the currency of the brokerage that made that trade — over the stored one. A price you entered by hand, or one from a quote feed, outranks a trade and is never overwritten. Prices no trade backs any more are not repaired here; they are listed under Prices worth a look. It does not change any transaction.",
    stored_notes: 'Repair recalculates the memo and tags each register shows for the transactions listed, from what you entered. It does not change any memo, tag or amount.',
    holdings_currency: "This happens when a brokerage's currency was changed in an earlier version. Repair records each brokerage's holdings in its own currency. Nothing is converted, and no price or transaction changes.",
    reminder_auto_post: 'This happens when a reminder set to auto-post was imported from Moneydance, or saved, by an earlier version that did not turn the job on. Repair turns the job on at its default time, as saving such a reminder does; the Reminders page changes its time. From its next run the job posts what is due, including occurrences already past due within its catch-up window that nobody posted or skipped; post or skip those on the Reminders page first if you do not want them posted. Repair itself changes no reminder or transaction.',
    quote_currency: "Repair asks Yahoo Finance for each security's currency, records it, and stamps that currency on the security's fetched prices. The prices are not changed, except that a price quoted in a minor unit, such as pence, is converted to the major unit. Prices you entered, imported or that came from a trade are not touched. It needs Yahoo Finance on for you in Settings → Quotes.",
    category_amounts: "This happens when a Moneydance import kept a category line's own figure where the account that paid it is in another currency. Repair sets each line listed to the paying account's amount, negated, as every category line now is. No account's balance changes; the category's totals then show what was paid.",
};

/**
 * What an unhealthy row counts, after its label: "N of M disagree" with what the
 * projection is derived from. The holdings currency's rows are brokerages, counted in
 * the reader's terms ("N of M brokerages out of step", "brokerage" when M is 1), and
 * each one is listed by the server as "<brokerage> · its holdings are recorded in X,
 * but the brokerage is in Y", the words the notifications use too. The reminder auto-post
 * row examines the ledger's one job, so it says the job is not set up, and the server's
 * one line says how many reminders that leaves unposted.
 */
function outOfStep(p: ProjectionConsistency): ReactNode {
    if (p.projection === 'reminder_auto_post') {
        return <>the auto-post job is not set up</>;
    }
    if (p.projection === 'quote_currency') {
        return (
            <>
                <strong>{p.mismatchedCount}</strong> of {p.checked}{' '}
                {p.checked === 1 ? 'security' : 'securities'} out of step
            </>
        );
    }
    if (p.projection === 'holdings_currency') {
        return (
            <>
                <strong>{p.mismatchedCount}</strong> of {p.checked}{' '}
                {p.checked === 1 ? 'brokerage' : 'brokerages'} out of step
            </>
        );
    }
    // ADR-0114 D15: counted in category lines, each a posting of one real line and one
    // category line.
    if (p.projection === 'category_amounts') {
        return (
            <>
                <strong>{p.mismatchedCount}</strong> of {p.checked}{' '}
                {p.checked === 1 ? 'category line' : 'category lines'} out of step
            </>
        );
    }
    return (
        <>
            <strong>{p.mismatchedCount}</strong> of {p.checked} disagree
        </>
    );
}

/**
 * The consistency report, one row per projection, each with its own repair.
 *
 * Every projection the report names is repairable — showing a problem with no way
 * to fix it leaves the damage unrepaired while ad-hoc SQL is written to look at
 * it. Repair appears only where something disagrees, so
 * it is never the first button anyone presses.
 *
 * Only for a reader who can write (`canEdit`). The server refuses anyone else's repair
 * before it rewrites anything, so a viewer is shown what disagrees, and neither a Repair
 * button nor what one would do.
 *
 * `containerRef` goes on the report itself, so the panel hands focus on when the report
 * goes holding it. `checkRef` is Check consistency, where focus goes when the last Repair
 * goes while the report stays.
 */
function ConsistencySummary({
    ledgerId,
    report,
    canEdit,
    onRepair,
    repairing,
    containerRef,
    checkRef,
}: {
    ledgerId: string;
    report: LedgerConsistencyReport;
    canEdit: boolean;
    onRepair: (projection: string) => void;
    repairing: string | null;
    containerRef: (el: HTMLElement | null) => void;
    checkRef: RefObject<HTMLElement | null>;
}) {
    // A repair that heals its projection keeps the row and takes its Repair away, with
    // the row's list of what disagrees. Focus goes from the Repair, or a link in that list,
    // to the next Repair drawn, else the previous one, else Check consistency (ADR-0023 §L).
    // The hook is here, not in the panel: this render takes a Repair away while the report
    // stays, and when the report goes this component goes with it, its layout effect does
    // not run, and only the panel's hand-off applies.
    const repairable = canEdit
        ? report.projections.filter((p) => !p.healthy).map((p) => p.projection)
        : [];
    const repairControl = useRowRemovalFocus(repairable, checkRef);
    return (
        <div ref={containerRef} className="space-y-2">
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
                                        outOfStep(p)
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
                                        <ul
                                            ref={canEdit ? repairControl(p.projection, 'companion') : undefined}
                                            className="mt-1 max-h-fixed-256px space-y-0.5 overflow-y-auto text-xs"
                                        >
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
                                                    {m.reason === NOT_DERIVED ? (
                                                        /* A running balance stored on a row the
                                                           register skips (hidden, merged away, a
                                                           reminder's template): no register can show
                                                           that row, so there is nothing to link to. */
                                                        m.scope
                                                    ) : m.accountId && m.headerId ? (
                                                        /* A hidden row is in its register's Hidden
                                                           view alone (ADR-0072 D1), so its link
                                                           opens that view. */
                                                        <Link
                                                            to="/ledgers/$ledgerId/accounts/$accountId"
                                                            params={{
                                                                ledgerId,
                                                                accountId: m.accountId,
                                                            }}
                                                            search={{
                                                                focus: m.headerId,
                                                                ...(m.hidden === true ? { status: 'hidden' as const } : {}),
                                                            }}
                                                            className="underline underline-offset-2 hover:opacity-80"
                                                        >
                                                            {m.scope}
                                                        </Link>
                                                    ) : m.securityId ? (
                                                        /* A position with no one transaction behind it (a
                                                           holdings or realized-gain row): the security's page
                                                           is where it is judged, so that is where the row
                                                           points. */
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
                                                    {/* Only where there IS a difference. A finding with no
                                                        figure to compare, such as a currency mismatch, carries
                                                        0 and 0, and "stored 0, expected 0" would be noise
                                                        dressed as a finding. */}
                                                    {m.reason === NOT_DERIVED
                                                        /* The rule derives no figure here, so
                                                           "expected 0" would read as a balance
                                                           that should be zero. */
                                                        ? `: stored ${m.stored}, none expected`
                                                        : m.stored === m.expected
                                                            ? null
                                                            : `: stored ${m.stored}, expected ${m.expected}`}
                                                </li>
                                            ))}
                                        </ul>
                                        {p.mismatches.some((m) => m.reason === CURRENCY_UNCONFIRMED) ? (
                                            <p className="mt-1 text-xs">
                                                Not confirmed: prices fetched before Coffer read
                                                each quote&apos;s currency were all recorded as USD,
                                                and Yahoo Finance has not named this
                                                security&apos;s currency since.
                                            </p>
                                        ) : null}
                                        {p.mismatchedCount > p.mismatches.length ? (
                                            <p className="mt-1 text-xs">
                                                Showing {p.mismatches.length} of{' '}
                                                {p.mismatchedCount}.
                                                {canEdit ? ' Repair fixes all of them, not only the ones listed.' : null}
                                            </p>
                                        ) : null}
                                        {canEdit ? (
                                            <p className="mt-2 text-xs">
                                                {REPAIR_EFFECT[p.projection] ??
                                                    'Repair recalculates these figures from the transactions and stores the result.'}
                                            </p>
                                        ) : null}
                                    </>
                                ) : null}

                            </div>
                            {!p.healthy && canEdit ? (
                                <Button
                                    ref={repairControl(p.projection)}
                                    type="button"
                                    variant="secondary"
                                    className="shrink-0"
                                    onClick={() => onRepair(p.projection)}
                                    pending={repairing !== null}
                                >
                                    {/* Every Repair waits on the one in flight; Repairing… is that one's. */}
                                    <PendingLabel
                                        pending={repairing === p.projection}
                                        label={`Repair ${label.toLowerCase()}`}
                                        pendingLabel="Repairing…"
                                    />
                                </Button>
                            ) : null}
                        </div>
                    </div>
                );
            })}

            {/* A FINDING without a repair (FU-097): warning-toned like the
                projections above, but with no button, because what is wrong is a
                purchase the data does not contain. Each row opens the register
                where the sale is, which is where it gets fixed. */}
            {report.oversold.length > 0 ? (
                <div className="rounded border border-state-warning/40 bg-state-warning-soft p-3 text-sm text-state-warning">
                    <p className="font-medium">
                        Positions sold beyond what they held —{' '}
                        <strong>{report.oversold.length}</strong>
                    </p>
                    <p className="mt-1 text-xs">
                        A sale took more shares than the account held at the time. The
                        gain on those shares is booked at zero cost, so it is
                        overstated. Correct the sale, or enter the purchase it is
                        missing. There is no repair: what is missing is not in the
                        data.
                    </p>
                    <ul className="mt-2 max-h-fixed-256px space-y-0.5 overflow-y-auto text-xs">
                        {report.oversold.map((o) => (
                            <li key={`${o.accountId}-${o.securityId}`}>
                                <Link
                                    to="/ledgers/$ledgerId/accounts/$accountId"
                                    params={{ ledgerId, accountId: o.accountId }}
                                    className="underline underline-offset-2 hover:opacity-80"
                                >
                                    {o.account} · {o.security}
                                </Link>{' '}
                                · sold {formatShares(o.sharesShort)} more shares than were held
                            </li>
                        ))}
                    </ul>
                </div>
            ) : null}

            {/* ADVISORY, and styled as one: neutral, below the findings, no
                count in any headline and no button. A price nothing derives may
                be perfectly correct — security_prices records THAT a trade wrote
                a row and never WHICH one, so this is inferred, and ADR-0084 D4
                keeps the price a deleted trade left behind on purpose. Reporting
                it as a defect would mark a sound ledger broken with a count of
                rows nobody can act on. */}
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
                                    {/* Calendar dates (YYYY-MM-DD), printed as every
                                        other ledger date is. */}
                                    {u.earliest === u.latest
                                        ? ` on ${formatLedgerDate(u.earliest)}`
                                        : `, ${formatLedgerDate(u.earliest)} to ${formatLedgerDate(u.latest)}`}
                                    {/* In the reporting currency, converted; a value that
                                        needs a missing rate says so, not a figure. */}
                                    {u.holdingValue === null
                                        ? <> · still held, <NoRate missing={u.rateUnavailable} /></>
                                        : u.holdingValue <= 0
                                            ? ' · no longer held'
                                            : ` · you hold ${formatCurrency(u.holdingValue, u.currencyCode)}`}
                                </span>
                            </li>
                        ))}
                    </ul>
                </div>
            ) : null}

            {/* ADVISORIES (ADR-0114 D15), styled as the one above: neutral, no count and
                no button. A figure with more places than its currency uses, and a code no
                longer accepted, are kept as they are and keep working; an edit that makes
                one fit takes it off its list. Each line links to where that edit is made.
                Not every such figure predates each currency's own places (a restored merge
                loser after a relabel is one that does not), so the text does not say when
                it was saved. */}
            {(report.unfitAmounts ?? []).length > 0 ? (
                <div className="rounded border border-border bg-surface-muted p-3 text-sm">
                    <p className="font-medium text-text">Amounts with more decimals than their currency</p>
                    <p className="mt-1 text-xs text-text-muted">
                        These have more places than their currency uses. They are kept as they
                        are. Editing one to fit its currency takes it off this list.
                    </p>
                    <ul className="mt-2 max-h-fixed-256px space-y-0.5 overflow-y-auto text-xs">
                        {(report.unfitAmounts ?? []).map((u, i) => (
                            <li key={`${u.kind}-${u.accountId}-${u.headerId ?? u.month ?? u.reminderId ?? ''}-${i}`}>
                                <UnfitAmountLink ledgerId={ledgerId} item={u} />
                                {/* A category line the Category amounts repair cannot set
                                    says why, muted, after where it is. */}
                                {u.reason && UNFIT_REASON_TEXT[u.reason] !== undefined ? (
                                    <>
                                        {' '}
                                        <span className="text-text-muted">· {UNFIT_REASON_TEXT[u.reason]}</span>
                                    </>
                                ) : null}
                            </li>
                        ))}
                    </ul>
                </div>
            ) : null}
            {(report.refusedCurrencies ?? []).length > 0 ? (
                <div className="rounded border border-border bg-surface-muted p-3 text-sm">
                    <p className="font-medium text-text">Currencies Coffer no longer accepts</p>
                    <p className="mt-1 text-xs text-text-muted">
                        These are kept in a code that is not an ISO 4217 currency Coffer
                        accepts. They keep working at 2 decimals, as before. Pick an accepted
                        currency in the account&apos;s settings to change one; a new account
                        cannot use these codes.
                    </p>
                    <ul className="mt-2 max-h-fixed-256px space-y-0.5 overflow-y-auto text-xs">
                        {(report.refusedCurrencies ?? []).map((c) => (
                            <li key={`${c.kind}-${c.accountId ?? ''}-${c.currencyCode}`}>
                                {c.kind === 'account' && c.accountId !== null ? (
                                    <Link
                                        to="/ledgers/$ledgerId/accounts"
                                        params={{ ledgerId }}
                                        search={{ edit: c.accountId }}
                                        className={ADVISORY_LINK}
                                    >
                                        {c.account ?? '—'} · {c.currencyCode}
                                    </Link>
                                ) : (
                                    // The ledger's own: its Reporting currency is on this page.
                                    <span className="text-text-muted">Reporting currency · {c.currencyCode}</span>
                                )}
                            </li>
                        ))}
                    </ul>
                </div>
            ) : null}
        </div>
    );
}

/** An advisory line's link: the look of the unbacked prices' links. */
const ADVISORY_LINK = 'text-accent underline underline-offset-2 hover:text-accent-hover';

/**
 * Why the Category amounts repair cannot set a category line it leaves in "Amounts with
 * more decimals than their currency" (ADR-0114 D15), shown muted after the line. A reason
 * this SPA does not know shows nothing, as a line with none does.
 */
const UNFIT_REASON_TEXT: Partial<Record<UnfitAmountReason, string>> = {
    'category-line-between-categories': 'both lines are on categories, so Repair cannot set it',
    'category-line-many-lines': 'more than one account or category line, so Repair cannot set it',
};

/**
 * One line of "Amounts with more decimals than their currency" (ADR-0114 D15), linked to
 * where its figure is edited: the register at the transaction (in its Hidden view, for a
 * hidden one), the account's editor for
 * an opening balance or a loan's terms, the budget at the target's month, the reminder.
 * The figure itself is not sent, so the line names only where it lives. A category line
 * the Category amounts repair leaves is listed here too, by the transaction (or reminder)
 * it is on, and the list gives its reason after the link.
 */
function UnfitAmountLink({ ledgerId, item }: { ledgerId: string; item: UnfitAmountAdvisory }) {
    switch (item.kind) {
        case 'transaction': {
            const text = [item.account, item.date ? formatLedgerDate(item.date) : null, item.payee ?? null]
                .filter((part): part is string => part !== null && part !== '')
                .join(' · ');
            // A hidden row is in its register's Hidden view alone (ADR-0072 D1), so its
            // link opens that view, or the register would have no row to focus.
            return (
                <Link
                    to="/ledgers/$ledgerId/accounts/$accountId"
                    params={{ ledgerId, accountId: item.accountId }}
                    search={item.headerId
                        ? { focus: item.headerId, ...(item.hidden === true ? { status: 'hidden' as const } : {}) }
                        : {}}
                    className={ADVISORY_LINK}
                >
                    {text}
                </Link>
            );
        }
        case 'opening_balance':
        case 'loan_terms':
            return (
                <Link
                    to="/ledgers/$ledgerId/accounts"
                    params={{ ledgerId }}
                    search={{ edit: item.accountId }}
                    className={ADVISORY_LINK}
                >
                    {item.account} · {item.kind === 'opening_balance' ? 'opening balance' : 'loan terms'}
                </Link>
            );
        case 'budget_target': {
            // The month as its first day, `YYYY-MM-DD`; the budget is opened at `YYYY-MM`.
            const month = item.month ? /^(\d{4})-(\d{2})/.exec(item.month) : null;
            return month ? (
                <Link
                    to="/ledgers/$ledgerId/budget"
                    params={{ ledgerId }}
                    search={{ month: `${month[1]}-${month[2]}` }}
                    className={ADVISORY_LINK}
                >
                    {item.account} · {monthLabel(Number(month[1]), Number(month[2]))} target
                </Link>
            ) : (
                <Link to="/ledgers/$ledgerId/budget" params={{ ledgerId }} className={ADVISORY_LINK}>
                    {item.account}
                </Link>
            );
        }
        case 'reminder':
            return (
                <Link
                    to="/ledgers/$ledgerId/reminders"
                    params={{ ledgerId }}
                    search={item.reminderId ? { reminder: item.reminderId } : {}}
                    className={ADVISORY_LINK}
                >
                    {item.payee || item.account} · reminder
                </Link>
            );
        default:
            return <span className="text-text-muted">{item.account}</span>;
    }
}

/**
 * What a repair could not rewrite, each with why (ProjectionRepairResult.notRepaired):
 * only the quote currencies' repair names any (ADR-0114 D5). Warning-toned like a
 * finding, because each one still disagrees; the next check lists it again.
 */
function NotRepairedList({ projection, items, ledgerId }: {
    projection: string;
    items: readonly ConsistencyMismatch[];
    ledgerId: string;
}) {
    const label = PROJECTION_LABELS[projection] ?? projection;
    return (
        <div role="status" className="rounded border border-state-warning/40 bg-state-warning-soft p-3 text-sm text-state-warning">
            <p className="font-medium">
                {label} — <strong>{items.length}</strong> not repaired
            </p>
            <ul className="mt-1 max-h-fixed-256px space-y-0.5 overflow-y-auto text-xs">
                {items.map((m, i) => (
                    <li key={`${m.securityId ?? m.scope}-${i}`}>
                        {m.securityId ? (
                            <Link
                                to="/ledgers/$ledgerId/securities/$securityId"
                                params={{ ledgerId, securityId: m.securityId }}
                                className="underline underline-offset-2 hover:opacity-80"
                            >
                                {m.scope}
                            </Link>
                        ) : (
                            m.scope
                        )}{' '}
                        · {NOT_REPAIRED_REASONS[m.reason ?? ''] ?? m.field}
                    </li>
                ))}
            </ul>
        </div>
    );
}
