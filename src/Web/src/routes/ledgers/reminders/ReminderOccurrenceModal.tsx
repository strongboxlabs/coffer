import { useMemo } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import {
    ApiError, fetchAccounts, fetchPayees, fetchReminderDetail,
    fireReminderBank, fireReminderInvestment, skipReminder,
} from '@/lib/api';
import type {
    UpcomingOccurrence, CreateTransactionRequest, CreateInvestmentTransactionRequest,
} from '@/lib/types';
import { buildAccountPathMap } from '@/lib/accountPath';
import { formatLedgerDate } from '@/lib/dates';
import { formatSignedAmount } from '@/lib/money';
import { estimateTitle, isEstimating } from '@/lib/reminderEstimate';
import { errorMessage } from '@/lib/errorMessage';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';
import { Button } from '@/components/ui/Button';
import { EditorDialog } from '@/components/ui/EditorDialog';
import { PendingScope } from '@/components/ui/pending';
import { PendingLabel } from '@/components/ui/PendingLabel';
import { Skeleton } from '@/components/ui/Skeleton';
import { TxnRowEdit } from '@/routes/ledgers/TxnRowEdit';
import { buildNotes } from '@/routes/ledgers/bank-edit/notesBody';
import { prefillToDraft } from '@/routes/ledgers/bank-edit/postingDraft';
import { InvestmentTxnRowEdit } from '@/routes/ledgers/investment-edit/InvestmentTxnRowEdit';

import { reminderBankPrefill, reminderInvestmentDraft } from './reminderOccurrenceDraft';

/**
 * Adjust-at-post dialog (ADR-0049): left-click a reminder chip → one modal that
 * shows the occurrence's transaction PREFILLED + fully editable (reusing the
 * live bank / investment editors), with Post (commit the edits) and Skip. The
 * catch-up line is shown inline (no second dialog). Post routes to
 * /fire/bank or /fire/investment; Skip to /skip.
 */
export function ReminderOccurrenceModal({ ledgerId, occ, onClose, onActed }: {
    ledgerId: string;
    occ: UpcomingOccurrence;
    onClose: () => void;
    /** Report a post-action notice (e.g. catch-up count) back to the panel. */
    onActed: (notice: string | null) => void;
}) {
    const queryClient = useQueryClient();

    // For THIS occurrence: a managed loan's split is priced as of the payment's own
    // date (ADR-0078), so two occurrences of one series can split differently.
    const detailQuery = useQuery({
        queryKey: ['reminders', 'detail', ledgerId, occ.reminderId, occ.date],
        queryFn: () => fetchReminderDetail(ledgerId, occ.reminderId, occ.date),
    });
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId],
        queryFn: () => fetchAccounts(ledgerId),
    });
    const payeesQuery = useQuery({
        queryKey: ['payees', ledgerId],
        queryFn: () => fetchPayees(ledgerId),
        staleTime: 30_000,
    });

    // Shared post-action: refresh the reminders list / calendar, report the
    // catch-up tally, close. Only a FIRE commits a transaction, so the register /
    // accounts / holdings refresh lives in the fire mutations below (a skip
    // changes nothing on the register). Every series' detail is refreshed too, for
    // every occurrence: acting moves the series' cursor, and a fired loan payment
    // moves the balance a later occurrence's split is priced on (ADR-0078).
    function afterAct(verb: string, skippedEarlierCount: number, skippedEarlierFrom: string | null) {
        queryClient.invalidateQueries({ queryKey: ['reminders', 'upcoming', ledgerId] });
        queryClient.invalidateQueries({ queryKey: ['reminders', ledgerId] });
        queryClient.invalidateQueries({ queryKey: ['reminders', 'detail', ledgerId] });
        const back = skippedEarlierFrom ? ` (back to ${formatLedgerDate(skippedEarlierFrom)})` : '';
        const notice = skippedEarlierCount > 0
            ? `${verb}. Also marked ${skippedEarlierCount} earlier occurrence`
              + `${skippedEarlierCount === 1 ? '' : 's'} as skipped${back}.`
            : null;
        onActed(notice);
        onClose();
    }

    const fireBank = useMutation({
        mutationFn: (body: CreateTransactionRequest) => fireReminderBank(ledgerId, occ.reminderId, {
            occurrenceDate: occ.date,
            sourceAccountId: body.sourceAccountId,
            postings: body.postings,
            payee: body.payee,
            checkNumber: body.checkNumber,
            notes: body.notes,
            postedDate: body.postedAt.slice(0, 10),
        }),
        onSuccess: (data) => {
            // A fired occurrence commits a real transaction — refresh the register
            // surface (rows via the ADR-0079 canonical key, plus accounts + holdings).
            invalidateLedgerRegister(queryClient, ledgerId);
            afterAct('Posted', data.skippedEarlierCount, data.skippedEarlierFrom);
        },
    });
    const fireInvestment = useMutation({
        mutationFn: (transaction: CreateInvestmentTransactionRequest) =>
            fireReminderInvestment(ledgerId, occ.reminderId, { occurrenceDate: occ.date, transaction }),
        onSuccess: (data) => {
            invalidateLedgerRegister(queryClient, ledgerId);
            afterAct('Posted', data.skippedEarlierCount, data.skippedEarlierFrom);
        },
    });
    const skipMutation = useMutation({
        mutationFn: () => skipReminder(ledgerId, occ.reminderId, { occurrenceDate: occ.date }),
        onSuccess: (data) => afterAct('Skipped', data.skippedEarlierCount, data.skippedEarlierFrom),
    });

    // Any of the dialog's requests in flight holds every action in it: Skip,
    // Post, Cancel and an editor's Enter-to-save alike. A post sent while the
    // skip of the same occurrence is unanswered (or the reverse) is a second,
    // conflicting answer to one occurrence. The editors' labels still say
    // "Posting…" only while a post is the request in flight.
    const busy = fireBank.isPending || fireInvestment.isPending || skipMutation.isPending;
    const actionError = fireBank.error ?? fireInvestment.error ?? skipMutation.error;

    // Skip is rendered INSIDE the editor's footer (the leading slot) so it sits
    // as a left-aligned peer of the editor's Cancel / Save — one consistent
    // action row, not a stranded extra footer.
    const skipButton = (
        <Button type="button" variant="ghost" size="sm" pending={busy}
            // A 44px target in a narrow dialog, beside the editor's own.
            className="@max-3xl/dialog:h-control-44px"
            onClick={() => skipMutation.mutate()}>
            Skip this occurrence
        </Button>
    );
    const detail = detailQuery.data;
    const accounts = useMemo(() => accountsQuery.data ?? [], [accountsQuery.data]);
    const accountPaths = useMemo(() => buildAccountPathMap(accounts), [accounts]);

    // Bank prefill + investment draft (memoized — the editors read the seed once). The
    // occurrence's own estimate seeds its amount (ADR-0049 D10), and both sides of an
    // estimating transfer between currencies (ADR-0114 D13).
    const occurrenceEstimate = occ.estimate ?? null;
    const bank = useMemo(
        () => (detail && detail.kind === 'bank' ? reminderBankPrefill(detail, occurrenceEstimate) : null),
        [detail, occurrenceEstimate]);
    const investment = useMemo(
        () => (detail && detail.kind === 'investment' ? reminderInvestmentDraft(detail, occ.date) : null),
        [detail, occ.date]);

    // The source account (which register this posts to) — shown in the title so
    // the user sees WHICH account the occurrence hits (it's fixed, not editable
    // here; consistent with the reminder editor's title).
    const sourceAccountId = bank?.sourceAccountId ?? investment?.brokerageAccountId ?? null;
    const sourceName = sourceAccountId !== null
        ? (accounts.find((a) => a.id === sourceAccountId)?.name ?? null) : null;
    // Every figure here is the source account's: the occurrence carries its
    // currency (a deactivated source keeps it), else the account list does.
    // Null while neither has it: no figure is printed until it is known.
    const currency = occ.currencyCode
        ?? (sourceAccountId !== null ? accounts.find((a) => a.id === sourceAccountId)?.currencyCode : undefined)
        ?? null;

    // Catch-up: acting will cascade-skip earlier occurrences iff the series'
    // next-due cursor is before this occurrence.
    const hasBacklog = occ.seriesNextDue !== null && occ.seriesNextDue < occ.date;

    const loading = detailQuery.isPending || accountsQuery.isPending || payeesQuery.isPending;
    const loadError = detailQuery.isError || accountsQuery.isError || payeesQuery.isError;

    // One group (pending.ts): a press of Skip, Post or an editor's save holds
    // every action in the dialog from the press, before `busy` has rendered.
    return (
        <PendingScope>
        <EditorDialog
            ariaLabel={`${occ.payee ?? 'Reminder'} — ${formatLedgerDate(occ.date)}`}
            title={occ.payee ?? 'Reminder'}
            subtitle={
                <>
                    {formatLedgerDate(occ.date)}
                    {/* A placeholder while the reads that say the currency are in
                        flight; nothing once they have answered without one (the
                        body says why). */}
                    {currency !== null
                        ? <>
                            {' · '}
                            {/* An estimating series' amount is not the bill (mig 220). */}
                            {isEstimating(occ) ? <span title={estimateTitle(occ.estimate)}>≈</span> : null}
                            {formatSignedAmount(occ.amount, currency)}
                        </>
                        : loading
                            ? <> · <Skeleton className="inline-block h-fixed-12px w-fixed-80px align-middle" /></>
                            : null}
                    {sourceName !== null ? ` · ${sourceName}` : ''}
                </>
            }
            onClose={onClose}
            bodyClassName="space-y-3"
        >
            {hasBacklog ? (
                <p className="rounded bg-state-warning-soft px-3 py-2 text-xs text-text-muted">
                    Posting or skipping also marks earlier un-acted occurrences
                    {occ.seriesNextDue ? ` (back to ${formatLedgerDate(occ.seriesNextDue)})` : ''} as skipped.
                </p>
            ) : null}

            {actionError !== null ? (
                <p role="alert" className="text-xs text-state-danger">
                    {errorMessage(actionError, 'Action failed.')}
                </p>
            ) : null}

            {loading ? (
                <p className="py-6 text-center text-sm text-text-subtle">Loading…</p>
            ) : loadError || !detail || (sourceAccountId !== null && currency === null) ? (
                // Loaded without the source's currency (no occurrence code, and
                // a source the account list does not hold) is no better than not
                // loaded: every figure below is in it.
                <p role="alert" className="py-6 text-center text-sm text-state-danger">
                    Could not load this reminder.
                </p>
            ) : detail.kind === 'bank' && detail.isLoanReminder && bank && currency !== null ? (
                <div className="space-y-3">
                    <p className="rounded bg-accent-soft px-3 py-2 text-xs text-accent">
                        <span className="font-semibold">Managed loan payment.</span>{' '}
                        The principal / interest / escrow split is computed from the loan
                        terms + the loan's balance on this payment's date, so it's shown
                        read-only. Change the schedule
                        from the loan account, or delete it on the Reminders page.
                    </p>
                    <ul className="divide-y divide-border rounded border border-border text-sm">
                        {(bank.prefill.postings ?? []).map((p, i) => (
                            <li key={i} className="flex items-center justify-between gap-2 px-3 py-1.5">
                                {/* accountPaths, not the bare name: the legs of a
                                    loan payment are the liability account plus the
                                    interest and escrow CATEGORIES, and a category's
                                    leaf is ambiguous in a flat list. The map walks
                                    parentId, so a real account — which has none —
                                    resolves to its plain name and is unaffected. */}
                                <span
                                    className="min-w-0 truncate text-text"
                                    title={p.counterpartyAccountId !== null && p.counterpartyAccountId !== undefined
                                        ? accountPaths.get(p.counterpartyAccountId)
                                        : undefined}
                                >
                                    {(p.counterpartyAccountId !== null && p.counterpartyAccountId !== undefined
                                        ? accountPaths.get(p.counterpartyAccountId)
                                        : undefined)
                                        ?? accounts.find((a) => a.id === p.counterpartyAccountId)?.name
                                        ?? 'Account'}
                                </span>
                                <span className="shrink-0 font-mono tabular-nums text-text">
                                    {formatSignedAmount(p.amount, currency)}
                                </span>
                            </li>
                        ))}
                        <li className="flex items-center justify-between gap-2 px-3 py-1.5 font-medium">
                            <span className="text-text-muted">Total</span>
                            <span className="shrink-0 font-mono tabular-nums text-text">
                                {formatSignedAmount((bank.prefill.postings ?? []).reduce((s, p) => s + p.amount, 0), currency)}
                            </span>
                        </li>
                    </ul>
                    <div className="flex items-center justify-between gap-2">
                        {skipButton}
                        <div className="flex gap-2">
                            <Button type="button" variant="secondary" size="sm" onClick={onClose} pending={busy}
                                className="@max-3xl/dialog:h-control-44px">
                                Cancel
                            </Button>
                            <Button type="button" variant="primary" size="sm" pending={busy}
                                className="@max-3xl/dialog:h-control-44px"
                                onClick={() => {
                                    // Amounts are placeholders here — the server recomputes the
                                    // managed split on fire; we just carry the counterparties.
                                    const postings = (bank.prefill.postings ?? [])
                                        .filter((p) => p.counterpartyAccountId !== null);
                                    fireBank.mutate({
                                        postedAt: occ.date,
                                        sourceAccountId: bank.sourceAccountId,
                                        postings: postings.map((p) => ({
                                            counterpartyAccountId: p.counterpartyAccountId!,
                                            amount: p.amount,
                                        })),
                                        payee: bank.prefill.payee,
                                        checkNumber: bank.prefill.checkNumber,
                                        // The template's values, as a create of the
                                        // occurrence sends them.
                                        notes: buildNotes({
                                            side: bank.sourceAccountId,
                                            isNew: true,
                                            memo: bank.prefill.memo ?? '',
                                            tags: bank.prefill.tags ?? [],
                                            postings: postings.map((p) => prefillToDraft(p, currency)),
                                            baseline: { memo: '', tags: [] },
                                        }),
                                    });
                                }}>
                                {/* Posting… only while the post is the request in flight. */}
                                <PendingLabel pending={fireBank.isPending} label="Post" pendingLabel="Posting…" />
                            </Button>
                        </div>
                    </div>
                </div>
            ) : detail.kind === 'bank' && bank && currency !== null ? (
                <TxnRowEdit
                    ledgerId={ledgerId}
                    mode={{
                        kind: 'new',
                        sourceAccountId: bank.sourceAccountId,
                        prefill: bank.prefill,
                        postedAt: occ.date,
                    }}
                    payees={payeesQuery.data ?? []}
                    accounts={accounts}
                    accountPaths={accountPaths}
                    currency={currency}
                    layout="dialog"
                    onSaveCreate={(body) => fireBank.mutate(body)}
                    onCancel={onClose}
                    submitLabel="Post"
                    submittingLabel="Posting…"
                    // While a skip is pending Post waits on it, and reads Post.
                    submitting={fireBank.isPending}
                    isSaving={busy}
                    saveError={fireBank.error instanceof ApiError ? fireBank.error : null}
                    cancelOnOutsideClick={false}
                    footerLeading={skipButton}
                />
            ) : detail.kind === 'investment' && investment && currency !== null ? (
                <InvestmentTxnRowEdit
                    ledgerId={ledgerId}
                    brokerageAccountId={investment.brokerageAccountId}
                    accounts={accounts}
                    currency={currency}
                    isTradeCommission={
                        accounts.find((a) => a.id === investment.brokerageAccountId)?.isTradeCommission ?? false}
                    layout="dialog"
                    footerLeading={skipButton}
                    submitLabel="Post"
                    submittingLabel="Posting…"
                    // While a skip is pending Post waits on it, and reads Post.
                    submitting={fireInvestment.isPending}
                    onCancel={onClose}
                    isSaving={busy}
                    mode={{
                        kind: 'fire',
                        initialDraft: investment.draft,
                        onSubmit: (req) => fireInvestment.mutate(req),
                    }}
                />
            ) : (
                <p role="alert" className="py-6 text-center text-sm text-state-danger">
                    This reminder can't be edited (no source account / unsupported shape).
                </p>
            )}
        </EditorDialog>
        </PendingScope>
    );
}
