import { useEffect, useMemo, useState, type RefObject } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import {
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
import type { SecondAmountSide } from '@/lib/secondAmount';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { EditorDialog } from '@/components/ui/EditorDialog';
import { TxnRowEdit, type TxnRowNewPrefill } from '@/routes/ledgers/TxnRowEdit';
import { InvestmentTxnRowEdit } from '@/routes/ledgers/investment-edit/InvestmentTxnRowEdit';
import type { InvestmentTxnDraft } from '@/routes/ledgers/investment-edit/validation';

import { RecurrenceBuilder, defaultSchedule, type ScheduleValue } from './RecurrenceBuilder';
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
    /** Where focus goes on close when nothing on the page opened the editor (EditorDialog). */
    fallbackFocusRef?: RefObject<HTMLElement | null>;
}

export function ReminderEditorDialog({
    ledgerId, reminderId, fromTransaction, fromInvestmentTransaction, onClose, onSaved, fallbackFocusRef,
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
    // The source's currency, which every figure in the editor is in. A deactivated
    // source (an edit of its series) is not in the account list, so it is read from
    // the rail's list, which keeps inactive accounts; it is asked only then.
    const everyAccountQuery = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: true }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: true }),
        enabled: sourceId !== null && accountsQuery.isSuccess && sourceAccount === null,
    });
    const currency = sourceAccount?.currencyCode
        ?? everyAccountQuery.data?.find((a) => a.id === sourceId)?.currencyCode
        ?? null;

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
            recurrence: parseRrule(detail.rrule, detail.startDate) ?? defaultSchedule(detail.startDate).recurrence,
            startDate: detail.startDate,
            endDate: detail.endDate,
            autoCommitDaysBefore: detail.autoCommitDaysBefore,
            // Mig 220. Seeded from the stored series so opening the editor shows what is
            // configured rather than resetting it to "as entered" — and so saving without
            // touching this section does not silently turn estimating off.
            estimateSampleCount: detail.estimate?.requestedSampleCount ?? null,
        });
        setScheduleSeeded(true);
    }, [isEdit, detail, scheduleSeeded]);

    const [error, setError] = useState<string | null>(null);

    // The embedded bank editor's one posting, while it is a transfer to a real account in
    // another currency: an estimate then sets that side from each due date's rate, which
    // the Schedule says (ADR-0114 D13). The editor tells it as the posting changes.
    const [secondSide, setSecondSide] = useState<SecondAmountSide | null>(null);

    // An imported reminder whose RRULE the builder can't represent ("custom" /
    // unsupported pattern) seeds the default schedule — so saving would REPLACE
    // its real schedule. Warn explicitly rather than silently rewrite it.
    const customSchedule = isEdit && detail != null && parseRrule(detail.rrule, detail.startDate) === null;

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
    // Mig 220: only a single-posting, non-loan BANK series can estimate.
    //
    // A loan reminder's amount is computed from its terms and balance — real
    // information an average would degrade — and a split has no single amount to
    // estimate. The server rejects both regardless; this only decides whether to offer
    // the control, so a user is not shown an option that would be refused.
    const estimateEligible =
        kind === 'bank'
        && !(detail?.isLoanReminder ?? false)
        && (bankPrefill?.prefill.postings?.length ?? 1) <= 1;

    const recurrenceFields = () => ({
        rrule: buildRrule(schedule.recurrence),
        startDate: schedule.startDate,
        endDate: schedule.endDate,
        autoCommitDaysBefore: schedule.autoCommitDaysBefore,
        estimateSampleCount: schedule.estimateSampleCount,
    });

    const saveBank = useMutation({
        mutationFn: (body: CreateTransactionRequest) => {
            const r = recurrenceFields();
            if (isEdit) {
                return updateReminderBank(ledgerId, reminderId as string, {
                    rrule: r.rrule, startDate: r.startDate,
                    clearEndDate: r.endDate === null, endDate: r.endDate,
                    clearAutoCommit: r.autoCommitDaysBefore === null, autoCommitDaysBefore: r.autoCommitDaysBefore,
                    // Mig 220. A null estimateSampleCount means "unchanged" on this PATCH,
                    // so turning estimation OFF needs the explicit clear — otherwise
                    // switching back to "As entered" would silently leave it on.
                    clearEstimate: r.estimateSampleCount === null,
                    estimateSampleCount: r.estimateSampleCount,
                    payee: body.payee, checkNumber: body.checkNumber,
                    postings: { sourceAccountId: body.sourceAccountId, items: body.postings },
                    // The embedded editor compares the transaction's values
                    // against the template it was seeded from, and restates
                    // every line's (see bank-edit/notesBody.ts).
                    notes: body.notes,
                });
            }
            return createReminderBank(ledgerId, {
                rrule: r.rrule, startDate: r.startDate, endDate: r.endDate,
                autoCommitDaysBefore: r.autoCommitDaysBefore,
                estimateSampleCount: r.estimateSampleCount,
                payee: body.payee, checkNumber: body.checkNumber,
                sourceAccountId: body.sourceAccountId, postings: body.postings,
                notes: body.notes,
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
    // own posting validation before calling us). An error stays shown until the
    // new result replaces it or the dialog closes, never cleared at the press
    // (ADR-0023 §L).
    function onBankSubmit(body: CreateTransactionRequest) {
        if (saveBank.isPending) return;   // guard double-submit
        const se = scheduleError();
        if (se !== null) { setError(se); return; }
        saveBank.mutate(body);
    }
    function onInvestmentSubmit(req: CreateInvestmentTransactionRequest) {
        // The embedded investment editor ('fire' mode) has no in-flight flag of
        // its own, so guard re-entrant submits here to avoid duplicate series.
        if (saveInvestment.isPending) return;
        const se = scheduleError();
        if (se !== null) { setError(se); return; }
        saveInvestment.mutate(req);
    }

    const submitting = saveBank.isPending || saveInvestment.isPending;
    // A source whose currency no list has yet keeps the editor loading: it prints
    // no figure in a currency it is not in. One no list holds is a failed load.
    const currencyPending = sourceId !== null && currency === null && !everyAccountQuery.isFetched;
    const loading = accountsQuery.isPending || payeesQuery.isPending || (isEdit && detailQuery.isPending)
        || currencyPending;
    const loadError = accountsQuery.isError || payeesQuery.isError || (isEdit && detailQuery.isError)
        || (sourceId !== null && currency === null && !currencyPending);

    // The source picker shows only on a from-scratch create (edit + from-txn fix
    // the source). Until a source is chosen there, the schedule + editor wait.
    const showSourcePicker = !isEdit && fromTransaction == null && fromInvestmentTransaction == null;

    return (
        <EditorDialog
            ariaLabel={isEdit ? 'Edit reminder' : 'New reminder'}
            title={isEdit ? 'Edit reminder' : 'New reminder'}
            subtitle={sourceAccount?.name}
            onClose={onClose}
            bodyClassName="space-y-4"
            fallbackFocusRef={fallbackFocusRef}
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

                    {sourceId !== null && kind !== null && currency !== null ? (
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
                                    <RecurrenceBuilder
                                        value={schedule}
                                        onChange={setSchedule}
                                        pending={submitting}
                                        estimateEligible={estimateEligible}
                                        // Its line names the money received in, or sent
                                        // from, the other account.
                                        transferAcrossCurrencies={
                                            kind === 'bank' && secondSide !== null
                                                ? {
                                                    accountName: secondSide.accountName,
                                                    fromCurrency: currency,
                                                    toCurrency: secondSide.currency,
                                                    direction: secondSide.direction,
                                                }
                                                : null}
                                    />
                                </div>
                            </div>

                            <div>
                                <p className={sectionClass}>Transaction</p>
                                <div className="mt-1">
                                    {kind === 'bank' ? (
                                        <TxnRowEdit
                                            ledgerId={ledgerId}
                                            mode={{
                                                kind: 'new',
                                                sourceAccountId: sourceId,
                                                prefill: bankPrefill?.prefill,
                                                // On an edit the prefill IS the stored
                                                // template: a save sends only what
                                                // differs from it.
                                                prefillIsStored: isEdit,
                                            }}
                                            payees={payeesQuery.data ?? []}
                                            accounts={accounts}
                                            accountPaths={accountPaths}
                                            currency={currency}
                                            layout="dialog"
                                            onSaveCreate={onBankSubmit}
                                            onCancel={onClose}
                                            isSaving={saveBank.isPending}
                                            // A refused save is shown once, in
                                            // this dialog's alert above, as for
                                            // the investment editor.
                                            saveError={null}
                                            cancelOnOutsideClick={false}
                                            onSecondAmountSideChange={setSecondSide}
                                        />
                                    ) : (
                                        <InvestmentTxnRowEdit
                                            ledgerId={ledgerId}
                                            brokerageAccountId={sourceId}
                                            accounts={accounts}
                                            currency={currency}
                                            isTradeCommission={sourceAccount?.isTradeCommission ?? false}
                                            layout="dialog"
                                            onCancel={onClose}
                                            isSaving={saveInvestment.isPending}
                                            mode={{
                                                kind: 'fire',
                                                initialDraft: investmentDraft?.draft,
                                                // As for the bank editor: on an
                                                // edit the draft IS the stored
                                                // template.
                                                initialIsStored: isEdit,
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
        </EditorDialog>
    );
}
