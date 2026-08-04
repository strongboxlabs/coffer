import { useMemo } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

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
import { errorMessage } from '@/lib/errorMessage';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';
import { Button } from '@/components/ui/Button';
import { TxnRowEdit } from '@/routes/ledgers/TxnRowEdit';
import { BANK_COLS } from '@/routes/ledgers/register/bank/columns';
import { InvestmentTxnRowEdit } from '@/routes/ledgers/investment-edit/InvestmentTxnRowEdit';
import { INVESTMENT_FORM_COLS } from '@/routes/ledgers/register/investment/columns';

import { ReminderDialogShell } from './ReminderDialogShell';
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

    const detailQuery = useQuery({
        queryKey: ['reminders', 'detail', ledgerId, occ.reminderId],
        queryFn: () => fetchReminderDetail(ledgerId, occ.reminderId),
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
    // changes nothing on the register).
    function afterAct(verb: string, skippedEarlierCount: number, skippedEarlierFrom: string | null) {
        queryClient.invalidateQueries({ queryKey: ['reminders', 'upcoming', ledgerId] });
        queryClient.invalidateQueries({ queryKey: ['reminders', ledgerId] });
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
            memo: body.memo,
            checkNumber: body.checkNumber,
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

    const busy = fireBank.isPending || fireInvestment.isPending || skipMutation.isPending;
    const actionError = fireBank.error ?? fireInvestment.error ?? skipMutation.error;

    // Skip is rendered INSIDE the editor's footer (the leading slot) so it sits
    // as a left-aligned peer of the editor's Cancel / Save — one consistent
    // action row, not a stranded extra footer.
    const skipButton = (
        <Button type="button" variant="ghost" size="sm" disabled={busy}
            onClick={() => skipMutation.mutate()}>
            Skip this occurrence
        </Button>
    );
    const detail = detailQuery.data;
    const accounts = useMemo(() => accountsQuery.data ?? [], [accountsQuery.data]);
    const accountPaths = useMemo(() => buildAccountPathMap(accounts), [accounts]);

    // Bank prefill + investment draft (memoized — the editors read the seed once).
    const bank = useMemo(
        () => (detail && detail.kind === 'bank' ? reminderBankPrefill(detail) : null),
        [detail]);
    const investment = useMemo(
        () => (detail && detail.kind === 'investment' ? reminderInvestmentDraft(detail, occ.date) : null),
        [detail, occ.date]);

    // The source account (which register this posts to) — shown in the title so
    // the user sees WHICH account the occurrence hits (it's fixed, not editable
    // here; consistent with the reminder editor's title).
    const sourceAccountId = bank?.sourceAccountId ?? investment?.brokerageAccountId ?? null;
    const sourceName = sourceAccountId !== null
        ? (accounts.find((a) => a.id === sourceAccountId)?.name ?? null) : null;

    // Catch-up: acting will cascade-skip earlier occurrences iff the series'
    // next-due cursor is before this occurrence.
    const hasBacklog = occ.seriesNextDue !== null && occ.seriesNextDue < occ.date;

    const loading = detailQuery.isPending || accountsQuery.isPending || payeesQuery.isPending;
    const loadError = detailQuery.isError || accountsQuery.isError || payeesQuery.isError;

    return (
        <ReminderDialogShell
            ariaLabel={`${occ.payee ?? 'Reminder'} — ${formatLedgerDate(occ.date)}`}
            title={
                <>
                    {occ.payee ?? 'Reminder'}
                    <span className="ml-2 text-sm font-normal text-text-muted">
                        {formatLedgerDate(occ.date)} · {formatSignedAmount(occ.amount)}
                        {sourceName !== null ? ` · ${sourceName}` : ''}
                    </span>
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
            ) : loadError || !detail ? (
                <p role="alert" className="py-6 text-center text-sm text-state-danger">
                    Could not load this reminder.
                </p>
            ) : detail.kind === 'bank' && detail.isLoanReminder && bank ? (
                <div className="space-y-3">
                    <p className="rounded bg-accent-soft px-3 py-2 text-xs text-accent">
                        <span className="font-semibold">Managed loan payment.</span>{' '}
                        The principal / interest / escrow split is computed from the loan
                        terms + current balance, so it's shown read-only. Change the schedule
                        from the loan account, or delete it on the Reminders page.
                    </p>
                    <ul className="divide-y divide-border rounded border border-border text-sm">
                        {(bank.prefill.postings ?? []).map((p, i) => (
                            <li key={i} className="flex items-center justify-between gap-2 px-3 py-1.5">
                                <span className="min-w-0 truncate text-text">
                                    {accounts.find((a) => a.id === p.counterpartyAccountId)?.name ?? 'Account'}
                                </span>
                                <span className="shrink-0 font-mono tabular-nums text-text">
                                    {formatSignedAmount(p.amount)}
                                </span>
                            </li>
                        ))}
                        <li className="flex items-center justify-between gap-2 px-3 py-1.5 font-medium">
                            <span className="text-text-muted">Total</span>
                            <span className="shrink-0 font-mono tabular-nums text-text">
                                {formatSignedAmount((bank.prefill.postings ?? []).reduce((s, p) => s + p.amount, 0))}
                            </span>
                        </li>
                    </ul>
                    <div className="flex items-center justify-between gap-2">
                        {skipButton}
                        <div className="flex gap-2">
                            <Button type="button" variant="secondary" size="sm" onClick={onClose} disabled={busy}>
                                Cancel
                            </Button>
                            <Button type="button" variant="primary" size="sm" disabled={busy}
                                onClick={() => fireBank.mutate({
                                    postedAt: occ.date,
                                    sourceAccountId: bank.sourceAccountId,
                                    // Amounts are placeholders here — the server recomputes the
                                    // managed split on fire; we just carry the counterparties.
                                    postings: (bank.prefill.postings ?? [])
                                        .filter((p) => p.counterpartyAccountId !== null)
                                        .map((p) => ({
                                            counterpartyAccountId: p.counterpartyAccountId!,
                                            amount: p.amount,
                                            legMemo: p.legMemo,
                                        })),
                                    payee: bank.prefill.payee,
                                    memo: bank.prefill.memo,
                                    checkNumber: bank.prefill.checkNumber,
                                })}>
                                {fireBank.isPending ? 'Posting…' : 'Post'}
                            </Button>
                        </div>
                    </div>
                </div>
            ) : detail.kind === 'bank' && bank ? (
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
                    currency={accounts.find((a) => a.id === bank.sourceAccountId)?.currencyCode ?? 'USD'}
                    cols={BANK_COLS}
                    onSaveCreate={(body) => fireBank.mutate(body)}
                    onCancel={onClose}
                    submitLabel="Post"
                    submittingLabel="Posting…"
                    isSaving={fireBank.isPending}
                    saveError={fireBank.error instanceof ApiError ? fireBank.error.detail : null}
                    cancelOnOutsideClick={false}
                    footerLeading={skipButton}
                />
            ) : detail.kind === 'investment' && investment ? (
                <InvestmentTxnRowEdit
                    ledgerId={ledgerId}
                    brokerageAccountId={investment.brokerageAccountId}
                    accounts={accounts}
                    isTradeCommission={
                        accounts.find((a) => a.id === investment.brokerageAccountId)?.isTradeCommission ?? false}
                    cols={INVESTMENT_FORM_COLS}
                    footerLeading={skipButton}
                    submitLabel="Post"
                    submittingLabel="Posting…"
                    onCancel={onClose}
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
        </ReminderDialogShell>
    );
}
