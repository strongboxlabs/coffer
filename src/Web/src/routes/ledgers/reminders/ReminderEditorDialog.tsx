import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    ApiError,
    createReminderBank, createReminderInvestment,
    fetchAccounts, fetchPayees, fetchReminderDetail,
    updateReminderBank, updateReminderInvestment,
} from '@/lib/api';
import type {
    CreateInvestmentTransactionRequest, CreateTransactionRequest,
} from '@/lib/types';
import { buildRrule, parseRrule } from '@/lib/recurrence';
import { errorMessage } from '@/lib/errorMessage';
import { buildAccountPathMap } from '@/lib/accountPath';
import { todayInputValue } from '@/lib/dates';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { TxnRowEdit, type TxnRowNewPrefill } from '@/routes/ledgers/TxnRowEdit';
import { BANK_COLS } from '@/routes/ledgers/register/bank/columns';
import { InvestmentTxnRowEdit } from '@/routes/ledgers/investment-edit/InvestmentTxnRowEdit';
import type { InvestmentTxnDraft } from '@/routes/ledgers/investment-edit/validation';
import { INVESTMENT_FORM_COLS } from '@/routes/ledgers/register/investment/columns';

import { RecurrenceBuilder, defaultSchedule, type ScheduleValue } from './RecurrenceBuilder';
import { ReminderDialogShell } from './ReminderDialogShell';
import { reminderBankPrefill, reminderInvestmentDraft } from './reminderOccurrenceDraft';

// Reminder series editor (ADR-0051 slice B). Create + edit a reminder, bank OR
// investment. The KIND is derived from the source account (no manual toggle):
// an investment account → the investment editor, anything else → the bank
// editor. The transaction shape REUSES the existing register editors
// (TxnRowEdit / InvestmentTxnRowEdit), exactly as the occurrence dialog does;
// this component wraps them with a Schedule section (RecurrenceBuilder) and maps
// the editor's emitted transaction + the schedule into one create/edit call.

const sectionClass = 'text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle';

export interface ReminderEditorDialogProps {
    ledgerId: string;
    /** Edit an existing series (fetches its detail). Null/undefined = create. */
    reminderId?: string | null;
    /** Create-from-transaction seed (bank register): the source account + the
     *  transaction's postings prefilled. The schedule is left blank for the
     *  user to fill (ADR-0051 D4). */
    fromTransaction?: { sourceAccountId: string; prefill: TxnRowNewPrefill } | null;
    /** Create-from-transaction seed (investment register): the brokerage source
     *  account + the transaction's draft prefilled. The schedule is left blank
     *  for the user to fill (ADR-0051 D4). */
    fromInvestmentTransaction?: { sourceAccountId: string; draft: InvestmentTxnDraft } | null;
    onClose: () => void;
    onSaved: () => void;
}

export function ReminderEditorDialog({
    ledgerId, reminderId, fromTransaction, fromInvestmentTransaction, onClose, onSaved,
}: ReminderEditorDialogProps) {
    const isEdit = reminderId != null;
    const queryClient = useQueryClient();

    const accountsQuery = useQuery({ queryKey: ['accounts', ledgerId], queryFn: () => fetchAccounts(ledgerId) });
    const payeesQuery = useQuery({
        queryKey: ['payees', ledgerId], queryFn: () => fetchPayees(ledgerId), staleTime: 30_000,
    });
    const detailQuery = useQuery({
        queryKey: ['reminders', 'detail', ledgerId, reminderId],
        queryFn: () => fetchReminderDetail(ledgerId, reminderId as string),
        enabled: isEdit,
    });

    const accounts = useMemo(() => accountsQuery.data ?? [], [accountsQuery.data]);
    const accountPaths = useMemo(() => buildAccountPathMap(accounts), [accounts]);
    const detail = detailQuery.data;

    // Source account: fixed on edit (from the series) / from-transaction; the
    // user picks it on a from-scratch create.
    const [picked, setPicked] = useState<string | null>(
        fromTransaction?.sourceAccountId ?? fromInvestmentTransaction?.sourceAccountId ?? null,
    );
    const sourceId = isEdit ? (detail?.sourceAccountId ?? null) : picked;
    const sourceAccount = useMemo(() => accounts.find((a) => a.id === sourceId) ?? null, [accounts, sourceId]);

    // Kind derives from the source account's type (no toggle). On edit it's the
    // series' own kind (the source might be inactive / unusual, so trust detail).
    const kind: 'bank' | 'investment' | null =
        isEdit ? (detail?.kind ?? null)
        : sourceAccount ? (sourceAccount.accountType === 'investment' ? 'investment' : 'bank')
        : null;

    // Schedule: default on create; seeded from the series once (on edit).
    const [schedule, setSchedule] = useState<ScheduleValue>(() => defaultSchedule(todayInputValue()));
    const [scheduleSeeded, setScheduleSeeded] = useState(false);
    useEffect(() => {
        if (!isEdit || !detail || scheduleSeeded) return;
        setSchedule({
            recurrence: parseRrule(detail.rrule) ?? defaultSchedule(detail.startDate).recurrence,
            startDate: detail.startDate,
            endDate: detail.endDate,
            autoCommitDaysBefore: detail.autoCommitDaysBefore,
        });
        setScheduleSeeded(true);
    }, [isEdit, detail, scheduleSeeded]);

    const [error, setError] = useState<string | null>(null);

    // An imported reminder whose RRULE the builder can't represent ("custom" /
    // unsupported pattern) seeds the default schedule — so saving would REPLACE
    // its real schedule. Warn explicitly rather than silently rewrite it.
    const customSchedule = isEdit && detail != null && parseRrule(detail.rrule) === null;

    // Prefill for the embedded editors (from-transaction or edit).
    const bankPrefill = useMemo<{ sourceAccountId: string; prefill: TxnRowNewPrefill } | null>(() => {
        if (fromTransaction) return fromTransaction;
        if (isEdit && detail && detail.kind === 'bank') return reminderBankPrefill(detail);
        return null;
    }, [fromTransaction, isEdit, detail]);
    const investmentDraft = useMemo(() => {
        if (fromInvestmentTransaction) return fromInvestmentTransaction;
        if (isEdit && detail && detail.kind === 'investment') {
            return reminderInvestmentDraft(detail, detail.startDate);
        }
        return null;
    }, [fromInvestmentTransaction, isEdit, detail]);

    const invalidate = () => {
        queryClient.invalidateQueries({ queryKey: ['reminders', ledgerId] });
        queryClient.invalidateQueries({ queryKey: ['reminders', 'upcoming', ledgerId] });
        // Refresh the per-series detail too, so reopening a just-saved reminder
        // re-seeds the editor from fresh data instead of the stale cache.
        queryClient.invalidateQueries({ queryKey: ['reminders', 'detail', ledgerId] });
    };

    function scheduleError(): string | null {
        if (!schedule.startDate) return 'A start date is required.';
        if (schedule.endDate !== null && schedule.endDate < schedule.startDate) {
            return 'The end date must be on or after the start date.';
        }
        return null;
    }
    const recurrenceFields = () => ({
        rrule: buildRrule(schedule.recurrence),
        startDate: schedule.startDate,
        endDate: schedule.endDate,
        autoCommitDaysBefore: schedule.autoCommitDaysBefore,
    });

    const saveBank = useMutation({
        mutationFn: (body: CreateTransactionRequest) => {
            const r = recurrenceFields();
            if (isEdit) {
                return updateReminderBank(ledgerId, reminderId as string, {
                    rrule: r.rrule, startDate: r.startDate,
                    clearEndDate: r.endDate === null, endDate: r.endDate,
                    clearAutoCommit: r.autoCommitDaysBefore === null, autoCommitDaysBefore: r.autoCommitDaysBefore,
                    payee: body.payee, memo: body.memo, checkNumber: body.checkNumber,
                    postings: { sourceAccountId: body.sourceAccountId, items: body.postings },
                });
            }
            return createReminderBank(ledgerId, {
                rrule: r.rrule, startDate: r.startDate, endDate: r.endDate,
                autoCommitDaysBefore: r.autoCommitDaysBefore,
                payee: body.payee, memo: body.memo, checkNumber: body.checkNumber,
                sourceAccountId: body.sourceAccountId, postings: body.postings,
            });
        },
        onSuccess: () => { invalidate(); onSaved(); onClose(); },
        onError: (e) => setError(errorMessage(e, 'Could not save the reminder.')),
    });

    const saveInvestment = useMutation({
        mutationFn: (req: CreateInvestmentTransactionRequest) => {
            const r = recurrenceFields();
            if (isEdit) {
                return updateReminderInvestment(ledgerId, reminderId as string, {
                    rrule: r.rrule, startDate: r.startDate,
                    clearEndDate: r.endDate === null, endDate: r.endDate,
                    clearAutoCommit: r.autoCommitDaysBefore === null, autoCommitDaysBefore: r.autoCommitDaysBefore,
                    transaction: req,
                });
            }
            return createReminderInvestment(ledgerId, {
                rrule: r.rrule, startDate: r.startDate, endDate: r.endDate,
                autoCommitDaysBefore: r.autoCommitDaysBefore, transaction: req,
            });
        },
        onSuccess: () => { invalidate(); onSaved(); onClose(); },
        onError: (e) => setError(errorMessage(e, 'Could not save the reminder.')),
    });

    // The embedded editor's Save fires these; the schedule is validated first so
    // an incomplete schedule blocks the create/edit (the editor already ran its
    // own posting validation before calling us).
    function onBankSubmit(body: CreateTransactionRequest) {
        if (saveBank.isPending) return;   // guard double-submit
        setError(null);
        const se = scheduleError();
        if (se !== null) { setError(se); return; }
        saveBank.mutate(body);
    }
    function onInvestmentSubmit(req: CreateInvestmentTransactionRequest) {
        // The embedded investment editor ('fire' mode) has no in-flight flag of
        // its own, so guard re-entrant submits here to avoid duplicate series.
        if (saveInvestment.isPending) return;
        setError(null);
        const se = scheduleError();
        if (se !== null) { setError(se); return; }
        saveInvestment.mutate(req);
    }

    const submitting = saveBank.isPending || saveInvestment.isPending;
    const loading = accountsQuery.isPending || payeesQuery.isPending || (isEdit && detailQuery.isPending);
    const loadError = accountsQuery.isError || payeesQuery.isError || (isEdit && detailQuery.isError);
    const currency = sourceAccount?.currencyCode ?? 'USD';
    const saveError = (saveBank.error ?? saveInvestment.error) instanceof ApiError
        ? ((saveBank.error ?? saveInvestment.error) as ApiError).detail : null;

    // The source picker shows only on a from-scratch create (edit + from-txn fix
    // the source). Until a source is chosen there, the schedule + editor wait.
    const showSourcePicker = !isEdit && fromTransaction == null && fromInvestmentTransaction == null;

    return (
        <ReminderDialogShell
            ariaLabel={isEdit ? 'Edit reminder' : 'New reminder'}
            title={
                <>
                    {isEdit ? 'Edit reminder' : 'New reminder'}
                    {sourceAccount !== null ? (
                        <span className="ml-2 text-sm font-normal text-text-muted">{sourceAccount.name}</span>
                    ) : null}
                </>
            }
            onClose={onClose}
            bodyClassName="space-y-4"
        >
            {error !== null ? (
                <p role="alert" className="text-xs text-state-danger">{error}</p>
            ) : null}

            {loading ? (
                <p className="py-6 text-center text-sm text-text-subtle">Loading…</p>
            ) : loadError ? (
                <p role="alert" className="py-6 text-center text-sm text-state-danger">
                    Could not load accounts or the reminder.
                </p>
            ) : (
                <>
                    {showSourcePicker ? (
                        <div>
                            <p className={sectionClass}>Account</p>
                            <div className="mt-1">
                                <AccountCategoryPicker
                                    accounts={accounts}
                                    isEligible={(a) => !a.isSystem && a.accountType !== 'category'}
                                    valueId={picked}
                                    onChangeId={(id) => setPicked(id)}
                                    label="Posts from"
                                    placeholder="Pick the account this reminder posts from…"
                                />
                            </div>
                            <p className="mt-1 text-[0.6875rem] text-text-muted">
                                An investment account makes an investment reminder; anything else, a bank reminder.
                            </p>
                        </div>
                    ) : null}

                    {sourceId !== null && kind !== null ? (
                        <>
                            <div>
                                <p className={sectionClass}>Schedule</p>
                                {customSchedule ? (
                                    <p className="mt-1 rounded bg-state-warning-soft px-2 py-1 text-[0.6875rem] text-text-muted">
                                        This reminder had a custom schedule that can't be shown here; saving
                                        will replace it with the schedule below.
                                    </p>
                                ) : null}
                                <div className="mt-1">
                                    <RecurrenceBuilder value={schedule} onChange={setSchedule} disabled={submitting} />
                                </div>
                            </div>

                            <div>
                                <p className={sectionClass}>Transaction</p>
                                <div className="mt-1">
                                    {kind === 'bank' ? (
                                        <TxnRowEdit
                                            ledgerId={ledgerId}
                                            mode={{ kind: 'new', sourceAccountId: sourceId, prefill: bankPrefill?.prefill }}
                                            payees={payeesQuery.data ?? []}
                                            accounts={accounts}
                                            accountPaths={accountPaths}
                                            currency={currency}
                                            cols={BANK_COLS}
                                            onSaveCreate={onBankSubmit}
                                            onCancel={onClose}
                                            isSaving={saveBank.isPending}
                                            saveError={saveError}
                                            cancelOnOutsideClick={false}
                                        />
                                    ) : (
                                        <InvestmentTxnRowEdit
                                            ledgerId={ledgerId}
                                            brokerageAccountId={sourceId}
                                            accounts={accounts}
                                            isTradeCommission={sourceAccount?.isTradeCommission ?? false}
                                            cols={INVESTMENT_FORM_COLS}
                                            onCancel={onClose}
                                            mode={{
                                                kind: 'fire',
                                                initialDraft: investmentDraft?.draft,
                                                onSubmit: onInvestmentSubmit,
                                            }}
                                        />
                                    )}
                                </div>
                            </div>
                        </>
                    ) : showSourcePicker ? null : (
                        <p role="alert" className="py-6 text-center text-sm text-state-danger">
                            This reminder has no source account and can't be edited here.
                        </p>
                    )}
                </>
            )}
        </ReminderDialogShell>
    );
}
