import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    createAccount, fetchAccount, fetchAccounts, loanPaymentPreview, setupPaymentReminder, updateAccount,
} from '@/lib/api';
import type { AccountSummary, LoanPaymentPreviewResponse, LoanTermsInput } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Modal } from '@/components/ui/Modal';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { cn } from '@/lib/cn';
import { errorMessage } from '@/lib/errorMessage';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';

// Account editor (ADR-0050) — create + edit, all real account types. Modeled
// on ConfirmDialog's overlay (controlled visibility, Esc + backdrop dismiss);
// modern-web idioms per ADR-0023 (sectioned, labels above inputs, inline
// error, [Cancel] [Save] footer). On edit it fetches the full account detail
// to prefill the metadata the list omits. Slice 3 adds opening balance +
// opened-on, and a Loan Terms block (required on loan accounts) with a live
// amortization preview computed server-side (single source of truth).

const ACCOUNT_TYPES: ReadonlyArray<{ value: string; label: string }> = [
    { value: 'bank', label: 'Bank' },
    { value: 'credit_card', label: 'Credit card' },
    { value: 'investment', label: 'Investment' },
    { value: 'asset', label: 'Asset' },
    { value: 'liability', label: 'Liability' },
    { value: 'loan', label: 'Loan' },
];

const CURRENCIES: ReadonlyArray<string> = [
    'USD', 'EUR', 'GBP', 'CAD', 'AUD', 'JPY', 'CHF', 'CNY', 'INR', 'MXN', 'BRL', 'SEK', 'NOK', 'NZD',
];

const sectionClass = 'text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle';
const inputClass =
    'mt-1 w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text ' +
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

const toNum = (s: string): number => {
    const n = Number.parseFloat(s);
    return Number.isFinite(n) ? n : 0;
};
const toInt = (s: string): number => {
    const n = Number.parseInt(s, 10);
    return Number.isFinite(n) ? n : 0;
};

/** Best-effort human cadence from a loan reminder's RRULE. */
function cadenceLabel(rrule: string | null): string {
    if (!rrule) return 'Scheduled';
    if (rrule.includes('FREQ=WEEKLY') && rrule.includes('INTERVAL=2')) return 'Every 2 weeks';
    if (rrule.includes('FREQ=WEEKLY')) return 'Weekly';
    if (rrule.includes('INTERVAL=3')) return 'Quarterly';
    const day = /BYMONTHDAY=(\d+)/.exec(rrule)?.[1];
    return day ? `Monthly (day ${day})` : 'Monthly';
}

export interface AccountEditorDialogProps {
    ledgerId: string;
    /** null = create mode; a summary = edit mode. */
    account: AccountSummary | null;
    onClose: () => void;
    /** Fired after a successful save (the dialog already invalidated the
     *  accounts cache; this lets the host react). */
    onSaved: () => void;
}

export function AccountEditorDialog({ ledgerId, account, onClose, onSaved }: AccountEditorDialogProps) {
    const isEdit = account !== null;
    const queryClient = useQueryClient();

    // Summary fields are available immediately (edit) / blank (create).
    const [name, setName] = useState(account?.name ?? '');
    const [accountType, setAccountType] = useState(account?.accountType ?? 'bank');
    const [categoryKind, setCategoryKind] = useState(account?.categoryKind ?? 'expense');
    const [institution, setInstitution] = useState(account?.institutionName ?? '');
    const [currency, setCurrency] = useState(account?.currencyCode ?? 'USD');
    const [isActive, setIsActive] = useState(account?.isActive ?? true);
    // Metadata not on the summary — fetched on edit, blank on create.
    const [accountNumber, setAccountNumber] = useState('');
    const [routingNumber, setRoutingNumber] = useState('');
    const [website, setWebsite] = useState('');
    const [notes, setNotes] = useState('');
    const [openingBalance, setOpeningBalance] = useState('0');
    const [openedOn, setOpenedOn] = useState('');
    // Tax treatment (ADR-0066) — prefilled from the detail fetch in edit mode.
    const [taxStatus, setTaxStatus] = useState('');
    // Loan terms (slice 3) — required on loan accounts.
    const [ltPrincipal, setLtPrincipal] = useState('');
    const [ltRate, setLtRate] = useState('');
    const [ltCount, setLtCount] = useState('');
    const [ltPpy, setLtPpy] = useState('12');
    const [ltEscrow, setLtEscrow] = useState('0');
    const [ltFirstPayment, setLtFirstPayment] = useState('');
    const [ltInterest, setLtInterest] = useState('');
    const [ltEscrowAcct, setLtEscrowAcct] = useState('');
    const [ltComputed, setLtComputed] = useState(true);
    const [ltFixed, setLtFixed] = useState('');
    const [preview, setPreview] = useState<LoanPaymentPreviewResponse | null>(null);
    const [error, setError] = useState<string | null>(null);
    // Managed payment reminder (ADR-0050 ext) — loan accounts, edit mode only.
    const [remSource, setRemSource] = useState('');
    const [remStart, setRemStart] = useState(() => new Date().toISOString().slice(0, 10));
    const [remError, setRemError] = useState<string | null>(null);

    const isBank = accountType === 'bank';
    const isCategory = accountType === 'category';
    const isLoan = accountType === 'loan';

    // Edit mode: fetch the full detail to prefill the metadata fields.
    const detailQuery = useQuery({
        queryKey: ['account', ledgerId, account?.id],
        queryFn: () => fetchAccount(ledgerId, account!.id),
        enabled: isEdit,
    });
    useEffect(() => {
        const d = detailQuery.data;
        if (!d) return;
        setAccountNumber(d.accountNumber ?? '');
        setRoutingNumber(d.routingNumber ?? '');
        setWebsite(d.accountUrl ?? '');
        setNotes(d.notes ?? '');
        setOpeningBalance(String(d.openingBalance ?? 0));
        setOpenedOn(d.openedOn ?? '');
        setTaxStatus(d.taxStatus ?? '');
        if (d.loanTerms) {
            const lt = d.loanTerms;
            setLtPrincipal(String(lt.originalPrincipal));
            setLtRate(String(lt.annualInterestRate));
            setLtCount(String(lt.paymentCount));
            setLtPpy(String(lt.paymentsPerYear));
            setLtEscrow(String(lt.escrowAmount));
            setLtFirstPayment(lt.firstPaymentDate ?? '');
            setLtInterest(lt.interestAccountId ?? '');
            setLtEscrowAcct(lt.escrowAccountId ?? '');
            setLtComputed(lt.paymentIsComputed);
            setLtFixed(lt.fixedPayment != null ? String(lt.fixedPayment) : '');
        }
    }, [detailQuery.data]);

    // Ledger accounts feed the interest / escrow pickers (loan only).
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId],
        queryFn: () => fetchAccounts(ledgerId),
        enabled: isLoan,
    });
    const pickerAccounts = accountsQuery.data ?? [];
    const managedReminder = detailQuery.data?.managedReminder ?? null;

    // Live amortization preview (debounced) — server is the single source of
    // truth for the math, so the SPA never duplicates the formula.
    useEffect(() => {
        const principal = toNum(ltPrincipal);
        const rate = toNum(ltRate);
        const count = toInt(ltCount);
        const ppy = toInt(ltPpy);
        const fixed = toNum(ltFixed);
        const valid = isLoan && principal > 0 && rate >= 0 && count > 0 && ppy > 0 && (ltComputed || fixed > 0);
        if (!valid) { setPreview(null); return; }
        let cancelled = false;
        const handle = setTimeout(() => {
            loanPaymentPreview(ledgerId, {
                originalPrincipal: principal, annualInterestRate: rate,
                paymentCount: count, paymentsPerYear: ppy, escrowAmount: toNum(ltEscrow),
                paymentIsComputed: ltComputed, fixedPayment: ltComputed ? null : fixed,
            })
                .then((r) => { if (!cancelled) setPreview(r); })
                .catch(() => { if (!cancelled) setPreview(null); });
        }, 300);
        return () => { cancelled = true; clearTimeout(handle); };
    }, [isLoan, ltPrincipal, ltRate, ltCount, ltPpy, ltEscrow, ltComputed, ltFixed, ledgerId]);

    // Account saves ripple into the register: a rename changes counterparty
    // labels shown in OTHER accounts' rows, and an opening-balance edit shifts
    // this account's running balances. Refresh the whole register surface
    // (ADR-0079 canonical key + accounts / buckets / holdings), not just the
    // accounts list.
    const invalidate = () => invalidateLedgerRegister(queryClient, ledgerId);

    const loanTermsValid = () =>
        toNum(ltPrincipal) > 0 && toNum(ltRate) >= 0 && toInt(ltCount) > 0 && toInt(ltPpy) > 0
        && (ltComputed || toNum(ltFixed) > 0);

    const buildTerms = (): LoanTermsInput => ({
        originalPrincipal: toNum(ltPrincipal),
        annualInterestRate: toNum(ltRate),
        points: 0,
        paymentCount: toInt(ltCount),
        paymentsPerYear: toInt(ltPpy),
        firstPaymentDate: ltFirstPayment || null,
        escrowAmount: toNum(ltEscrow),
        interestAccountId: ltInterest || null,
        escrowAccountId: ltEscrowAcct || null,
        paymentIsComputed: ltComputed,
        fixedPayment: ltComputed ? null : toNum(ltFixed),
    });

    const money = (n: number) =>
        new Intl.NumberFormat('en-US', { style: 'currency', currency: currency || 'USD' }).format(n);

    const createMut = useMutation({
        mutationFn: () => createAccount(ledgerId, {
            name: name.trim(),
            accountType,
            categoryKind: isCategory ? categoryKind : null,
            currencyCode: currency.trim() || 'USD',
            institutionName: institution.trim() || null,
            accountNumber: accountNumber.trim() || null,
            routingNumber: isBank ? (routingNumber.trim() || null) : null,
            accountUrl: website.trim() || null,
            notes: notes.trim() || null,
            isActive,
            openingBalance: isCategory ? 0 : toNum(openingBalance),
            openedOn: openedOn || null,
            loanTerms: isLoan ? buildTerms() : undefined,
        }),
        onSuccess: () => { invalidate(); onSaved(); onClose(); },
        onError: (e) => setError(errorMessage(e, 'Could not create the account.')),
    });

    const updateMut = useMutation({
        mutationFn: () => updateAccount(ledgerId, account!.id, {
            // Text fields send the full value (incl. "") so the server can
            // set or clear; routing only when it's shown (bank).
            name: name.trim(),
            currencyCode: currency.trim() || 'USD',
            institutionName: institution,
            accountNumber,
            routingNumber: isBank ? routingNumber : undefined,
            accountUrl: website,
            notes,
            isActive,
            categoryKind: isCategory ? categoryKind : undefined,
            openingBalance: isCategory ? 0 : toNum(openingBalance),
            // The editor owns the field; a blank value clears the date.
            openedOn: openedOn || undefined,
            clearOpenedOn: openedOn ? undefined : true,
            loanTerms: isLoan ? buildTerms() : undefined,
            // Tax treatment: categories never carry one; '' clears, a value sets.
            taxStatus: isCategory ? undefined : taxStatus,
        }),
        onSuccess: () => {
            invalidate();
            // The editor prefills from the ['account', id] detail query via a
            // capture-once useEffect; invalidateLedgerRegister refreshes the
            // plural ['accounts'] list + register but NOT this singular detail
            // key, so reopening the same account within the cache window re-seeded
            // stale metadata / loan terms. Invalidate it too (edit mode only —
            // a create has no detail). Mirrors setupPaymentReminder's onSuccess.
            void queryClient.invalidateQueries({ queryKey: ['account', ledgerId, account!.id] });
            onSaved();
            onClose();
        },
        onError: (e) => setError(errorMessage(e, 'Could not save the account.')),
    });

    // Managed payment reminder setup (loan, edit mode). Amounts aren't sent —
    // the server derives the split from the saved loan terms + balance.
    const setupReminderMut = useMutation({
        mutationFn: () => setupPaymentReminder(ledgerId, account!.id, {
            sourceAccountId: remSource,
            startDate: remStart,
        }),
        onSuccess: () => {
            void queryClient.invalidateQueries({ queryKey: ['account', ledgerId, account!.id] });
            void queryClient.invalidateQueries({ queryKey: ['reminders', ledgerId] });
            setRemError(null);
        },
        onError: (e) => setRemError(errorMessage(e, 'Could not set up the scheduled payment.')),
    });

    const detailLoading = isEdit && detailQuery.isPending;
    const submitting = createMut.isPending || updateMut.isPending;
    const currencyOptions = CURRENCIES.includes(currency) ? CURRENCIES : [currency, ...CURRENCIES];
    const numberLabel = accountType === 'credit_card' ? 'Card number' : 'Account number';

    function handleSave() {
        setError(null);
        if (name.trim() === '') { setError('Name is required.'); return; }
        if (isLoan && !loanTermsValid()) {
            setError('Loan terms are incomplete: principal, rate, term, and payments per year are required.');
            return;
        }
        if (isEdit) updateMut.mutate(); else createMut.mutate();
    }

    return (
        <Modal open onClose={onClose} titleId="account-editor-title" className="max-w-md">
            <div className="flex max-h-[90vh] flex-col overflow-y-auto p-5">
                <h2 id="account-editor-title" className="mb-3 text-base font-semibold text-text">
                    {isEdit ? `Edit ${account!.name}` : 'New account'}
                </h2>

                <p className={sectionClass}>Identity</p>
                <div className="mt-1 mb-3 space-y-3">
                    <div className="block">
                        <FieldLabel htmlFor="acct-name">Name</FieldLabel>
                        <input id="acct-name" className={inputClass} value={name}
                            onChange={(e) => setName(e.target.value)} />
                    </div>

                    <div className="block">
                        <FieldLabel htmlFor="acct-type">Type</FieldLabel>
                        {isEdit ? (
                            <input
                                id="acct-type"
                                className={cn(inputClass, 'cursor-not-allowed text-text-muted')}
                                value={ACCOUNT_TYPES.find((t) => t.value === accountType)?.label ?? accountType}
                                title="Account type can't be changed after creation"
                                readOnly disabled
                            />
                        ) : (
                            <select id="acct-type" className={inputClass} value={accountType}
                                onChange={(e) => setAccountType(e.target.value)}>
                                {ACCOUNT_TYPES.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
                            </select>
                        )}
                    </div>

                    {isCategory ? (
                        <div className="block">
                            <FieldLabel htmlFor="acct-kind">Kind</FieldLabel>
                            <select id="acct-kind" className={inputClass} value={categoryKind ?? 'expense'}
                                onChange={(e) => setCategoryKind(e.target.value)}>
                                <option value="expense">Expense</option>
                                <option value="income">Income</option>
                            </select>
                        </div>
                    ) : (
                        <>
                            <div className="block">
                                <FieldLabel htmlFor="acct-institution">Institution</FieldLabel>
                                <input id="acct-institution" className={inputClass} value={institution}
                                    placeholder="(optional)" onChange={(e) => setInstitution(e.target.value)} />
                            </div>
                            <div className="block">
                                <FieldLabel htmlFor="acct-number">{numberLabel}</FieldLabel>
                                <input id="acct-number" className={inputClass} value={accountNumber}
                                    placeholder="(optional)" onChange={(e) => setAccountNumber(e.target.value)} />
                            </div>
                            {isBank ? (
                                <div className="block">
                                    <FieldLabel htmlFor="acct-routing">Routing number</FieldLabel>
                                    <input id="acct-routing" className={inputClass} value={routingNumber}
                                        placeholder="(optional)" onChange={(e) => setRoutingNumber(e.target.value)} />
                                </div>
                            ) : null}
                            {isEdit ? (
                                <div className="block">
                                    <FieldLabel htmlFor="acct-tax-status">Tax treatment</FieldLabel>
                                    <select id="acct-tax-status" className={inputClass} value={taxStatus}
                                        onChange={(e) => setTaxStatus(e.target.value)}>
                                        <option value="">(unspecified)</option>
                                        <option value="taxable">Taxable</option>
                                        <option value="tax_deferred">Tax-deferred (401k, IRA)</option>
                                        <option value="tax_free">Tax-free (Roth)</option>
                                        <option value="other">Other (529, HSA)</option>
                                    </select>
                                </div>
                            ) : null}
                        </>
                    )}
                </div>

                <p className={sectionClass}>Details</p>
                <div className="mt-1 mb-1 space-y-3">
                    <div className="flex items-end gap-3">
                        <div className="block flex-1">
                            <FieldLabel htmlFor="acct-currency">Currency</FieldLabel>
                            <select id="acct-currency" className={inputClass} value={currency}
                                onChange={(e) => setCurrency(e.target.value)}>
                                {currencyOptions.map((c) => <option key={c} value={c}>{c}</option>)}
                            </select>
                        </div>
                        <label className="flex items-center gap-2 pb-2">
                            <input type="checkbox" checked={isActive}
                                onChange={(e) => setIsActive(e.target.checked)} />
                            <span className="text-sm text-text">Active</span>
                        </label>
                    </div>
                    {!isCategory ? (
                        <div className="flex items-end gap-3">
                            <div className="block flex-1">
                                <FieldLabel htmlFor="acct-opening-balance">Opening balance</FieldLabel>
                                <input id="acct-opening-balance" className={inputClass} inputMode="decimal" value={openingBalance}
                                    onChange={(e) => setOpeningBalance(e.target.value)} />
                            </div>
                            <div className="block flex-1">
                                <FieldLabel htmlFor="acct-opened-on">Opened on</FieldLabel>
                                <input id="acct-opened-on" type="date" className={inputClass} value={openedOn}
                                    onChange={(e) => setOpenedOn(e.target.value)} />
                            </div>
                        </div>
                    ) : null}
                    {!isCategory ? (
                        <div className="block">
                            <FieldLabel htmlFor="acct-website">Website</FieldLabel>
                            <input id="acct-website" className={inputClass} value={website}
                                placeholder="https://" onChange={(e) => setWebsite(e.target.value)} />
                        </div>
                    ) : null}
                    <div className="block">
                        <FieldLabel htmlFor="acct-notes">Notes</FieldLabel>
                        <textarea id="acct-notes" className={inputClass} value={notes} rows={2}
                            placeholder="(optional)" onChange={(e) => setNotes(e.target.value)} />
                    </div>
                </div>

                {isLoan ? (
                    <>
                        <p className={cn(sectionClass, 'mt-3')}>Loan terms</p>
                        <div className="mt-1 mb-1 space-y-3">
                            <div className="flex gap-3">
                                <div className="block flex-1">
                                    <FieldLabel htmlFor="acct-lt-principal">Original principal</FieldLabel>
                                    <input id="acct-lt-principal" className={inputClass} inputMode="decimal" value={ltPrincipal}
                                        onChange={(e) => setLtPrincipal(e.target.value)} />
                                </div>
                                <div className="block flex-1">
                                    <FieldLabel htmlFor="acct-lt-rate">Annual rate %</FieldLabel>
                                    <input id="acct-lt-rate" className={inputClass} inputMode="decimal" value={ltRate}
                                        onChange={(e) => setLtRate(e.target.value)} />
                                </div>
                            </div>
                            <div className="flex gap-3">
                                <div className="block flex-1">
                                    <FieldLabel htmlFor="acct-lt-count">Term (payments)</FieldLabel>
                                    <input id="acct-lt-count" className={inputClass} inputMode="numeric" value={ltCount}
                                        onChange={(e) => setLtCount(e.target.value)} />
                                </div>
                                <div className="block flex-1">
                                    <FieldLabel htmlFor="acct-lt-ppy">Payments / year</FieldLabel>
                                    <input id="acct-lt-ppy" className={inputClass} inputMode="numeric" value={ltPpy}
                                        onChange={(e) => setLtPpy(e.target.value)} />
                                </div>
                            </div>
                            <div className="flex gap-3">
                                <div className="block flex-1">
                                    <FieldLabel htmlFor="acct-lt-first-payment">First payment</FieldLabel>
                                    <input id="acct-lt-first-payment" type="date" className={inputClass} value={ltFirstPayment}
                                        onChange={(e) => setLtFirstPayment(e.target.value)} />
                                </div>
                                <div className="block flex-1">
                                    <FieldLabel htmlFor="acct-lt-escrow">Escrow amount</FieldLabel>
                                    <input id="acct-lt-escrow" className={inputClass} inputMode="decimal" value={ltEscrow}
                                        onChange={(e) => setLtEscrow(e.target.value)} />
                                </div>
                            </div>
                            <AccountCategoryPicker
                                accounts={pickerAccounts}
                                isEligible={(a) => a.accountType === 'category'}
                                valueId={ltInterest || null}
                                onChangeId={(id) => setLtInterest(id ?? '')}
                                label="Interest category"
                                placeholder="Pick a category…"
                            />
                            <AccountCategoryPicker
                                accounts={pickerAccounts}
                                isEligible={(a) => !a.isSystem}
                                valueId={ltEscrowAcct || null}
                                onChangeId={(id) => setLtEscrowAcct(id ?? '')}
                                label="Escrow account"
                                placeholder="Pick an account…"
                            />
                            <div>
                                <FieldLabel>Payment</FieldLabel>
                                <div className="mt-1 flex flex-wrap items-center gap-x-4 gap-y-2 text-sm text-text">
                                    <label className="flex items-center gap-1.5">
                                        <input type="radio" name="loan-payment-mode" checked={ltComputed}
                                            onChange={() => setLtComputed(true)} />
                                        <span>Computed (amortized)</span>
                                    </label>
                                    <label className="flex items-center gap-1.5">
                                        <input type="radio" name="loan-payment-mode" checked={!ltComputed}
                                            onChange={() => setLtComputed(false)} />
                                        <span>Fixed amount</span>
                                    </label>
                                    {!ltComputed ? (
                                        <input className={cn(inputClass, 'mt-0 w-32')} inputMode="decimal"
                                            value={ltFixed} placeholder="0.00"
                                            aria-label="Fixed payment amount"
                                            onChange={(e) => setLtFixed(e.target.value)} />
                                    ) : null}
                                </div>
                            </div>
                            {preview ? (
                                <p className="text-xs text-text-muted">
                                    Estimated payment:{' '}
                                    <span className="font-semibold text-text">{money(preview.totalPayment)}</span>
                                    /period ({money(preview.periodicPayment)} P&amp;I
                                    {preview.escrowAmount > 0 ? <> + {money(preview.escrowAmount)} escrow</> : null})
                                </p>
                            ) : null}

                            {isEdit ? (
                                <div className="border-t border-border pt-3">
                                    <p className={sectionClass}>Scheduled payment</p>
                                    {managedReminder ? (
                                        <div className="mt-1 rounded-md border border-border bg-surface-muted/40 p-2 text-xs">
                                            <span className="font-medium text-text">{cadenceLabel(managedReminder.rrule)}</span>
                                            {managedReminder.nextDue ? (
                                                <span className="text-text-muted"> · next {managedReminder.nextDue}</span>
                                            ) : null}
                                            <span className="ml-1.5 rounded bg-accent-soft px-1 py-px text-[0.625rem] font-medium uppercase tracking-wide text-accent">
                                                Managed
                                            </span>
                                            <p className="mt-1 text-text-muted">
                                                Each payment’s split (principal · interest · escrow) is computed from these
                                                loan terms. Manage or delete it on the Reminders page.
                                            </p>
                                        </div>
                                    ) : (
                                        <div className="mt-1 space-y-2">
                                            <p className="text-xs text-text-muted">
                                                No scheduled auto-payment yet. Set one up and Coffer computes each
                                                payment’s split from the terms above.
                                            </p>
                                            <AccountCategoryPicker
                                                accounts={pickerAccounts}
                                                isEligible={(a) => ['bank', 'credit_card', 'cash', 'asset', 'liability'].includes(a.accountType)}
                                                valueId={remSource || null}
                                                onChangeId={(id) => setRemSource(id ?? '')}
                                                label="Pays from"
                                                placeholder="Pick a bank account…"
                                            />
                                            <div className="block">
                                                <FieldLabel htmlFor="acct-rem-start">First payment date</FieldLabel>
                                                <input id="acct-rem-start" type="date" className={inputClass}
                                                    value={remStart} onChange={(e) => setRemStart(e.target.value)} />
                                            </div>
                                            {remError !== null ? (
                                                <p role="alert" className="text-xs text-state-danger">{remError}</p>
                                            ) : null}
                                            <Button type="button" variant="secondary" size="sm"
                                                disabled={!remSource || !remStart || setupReminderMut.isPending}
                                                onClick={() => { setRemError(null); setupReminderMut.mutate(); }}>
                                                {setupReminderMut.isPending ? 'Setting up…' : 'Set up scheduled payment'}
                                            </Button>
                                        </div>
                                    )}
                                </div>
                            ) : null}
                        </div>
                    </>
                ) : null}

                {error !== null ? (
                    <p role="alert" className="mt-2 text-xs text-state-danger">{error}</p>
                ) : null}

                <div className="mt-4 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose} disabled={submitting}>
                        Cancel
                    </Button>
                    <Button type="button" variant="primary" size="sm" onClick={handleSave}
                        disabled={submitting || detailLoading}>
                        {submitting ? 'Saving…' : isEdit ? 'Save' : 'Create'}
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
