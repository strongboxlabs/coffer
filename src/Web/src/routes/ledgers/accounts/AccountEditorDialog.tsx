import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useMutation } from '@/lib/mutation';

import {
    ApiError, createAccount, fetchAccount, fetchAccounts, loanPaymentPreview, setupPaymentReminder, updateAccount,
} from '@/lib/api';
import type { AccountSummary, LoanPaymentPreviewResponse, LoanTermsInput, RelabelMergedInto } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Modal } from '@/components/ui/Modal';
import { PendingLabel } from '@/components/ui/PendingLabel';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { TwoAmountDone } from '@/components/TwoAmountDone';
import { cn } from '@/lib/cn';
import { currencyChoices } from '@/lib/currencies';
import { formatLedgerDate } from '@/lib/dates';
import { errorMessage } from '@/lib/errorMessage';
import { amountText, formatCurrency, roundToMinorUnits } from '@/lib/money';
import { MoneyInput } from '@/components/ui/MoneyInput';
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

const sectionClass = 'text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle';
/** Kind values as they read to a person. Also covers an unrecognised value by
 *  falling through to the raw string, so a category created by an older or newer
 *  build never renders a blank field. */
const CATEGORY_KIND_LABELS: Record<string, string> = {
    income: 'Income',
    expense: 'Expense',
    adjustment: 'Adjustment',
};

const inputClass =
    'mt-1 w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text ' +
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';
/** A money input's look: the field's own, with the top margin on MoneyInput's wrapper,
 *  so the symbol centres on the field and not on the margin above it. */
const moneyInputClass = cn(inputClass, 'mt-0');

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

/** The transactions a refused currency change names (`mergedInto`, ADR-0114 D13): a
 *  transfer whose two amounts would differ in one currency is on a row merged into each,
 *  which no register shows. Empty for any other error. */
function mergedIntoOf(error: unknown): readonly RelabelMergedInto[] {
    return error instanceof ApiError && error.code === 'relabel-pair-not-representable' ? error.mergedInto : [];
}

/** A link to one transaction a refused pair's row was merged into: its register, at it,
 *  in the Hidden view for a hidden one (ADR-0072 D1). Reads as the consistency check's
 *  transaction lines do: account · date · payee. */
function MergedIntoLink({ ledgerId, item }: { ledgerId: string; item: RelabelMergedInto }) {
    const text = [item.account, formatLedgerDate(item.date), item.payee]
        .filter((part): part is string => part !== null && part !== '')
        .join(' · ');
    return (
        <Link
            to="/ledgers/$ledgerId/accounts/$accountId"
            params={{ ledgerId, accountId: item.accountId }}
            search={{ focus: item.headerId, ...(item.hidden ? { status: 'hidden' as const } : {}) }}
            className="font-medium underline underline-offset-2 hover:opacity-80"
        >
            {text}
        </Link>
    );
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
    const [error, setErrorText] = useState<string | null>(null);
    // The transactions a refused currency change's pair was merged into, linked under the
    // error (ADR-0114 D13); replaced with the error, so a later one never keeps them.
    const [mergedInto, setMergedInto] = useState<readonly RelabelMergedInto[]>([]);
    const setError = (message: string | null, links: readonly RelabelMergedInto[] = []) => {
        setErrorText(message);
        setMergedInto(links);
    };
    // How many transfers the saved currency change left keeping two amounts (ADR-0114
    // D13); above 0, the dialog shows that line and Done in place of its form.
    const [becameTwoAmount, setBecameTwoAmount] = useState(0);
    // Managed payment reminder (ADR-0050 ext) — loan accounts, edit mode only.
    const [remSource, setRemSource] = useState('');
    const [remStart, setRemStart] = useState(() => new Date().toISOString().slice(0, 10));
    const [remError, setRemError] = useState<string | null>(null);
    // The money texts the detail seeded, as seeded; a create seeds none.
    const seeded = useRef<{ openingBalance?: string; principal?: string; escrow?: string; fixed?: string }>({});

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
        // Money is seeded at the account currency's places, with every digit a stored
        // figure carries beyond them (ADR-0114 D15).
        const openingText = amountText(d.openingBalance ?? 0, d.currencyCode);
        setOpeningBalance(openingText);
        seeded.current = { openingBalance: openingText };
        setOpenedOn(d.openedOn ?? '');
        setTaxStatus(d.taxStatus ?? '');
        if (d.loanTerms) {
            const lt = d.loanTerms;
            const principalText = amountText(lt.originalPrincipal, d.currencyCode);
            const escrowText = amountText(lt.escrowAmount, d.currencyCode);
            const fixedText = lt.fixedPayment != null ? amountText(lt.fixedPayment, d.currencyCode) : '';
            setLtPrincipal(principalText);
            setLtRate(String(lt.annualInterestRate));
            setLtCount(String(lt.paymentCount));
            setLtPpy(String(lt.paymentsPerYear));
            setLtEscrow(escrowText);
            setLtFirstPayment(lt.firstPaymentDate ?? '');
            setLtInterest(lt.interestAccountId ?? '');
            setLtEscrowAcct(lt.escrowAccountId ?? '');
            setLtComputed(lt.paymentIsComputed);
            setLtFixed(fixedText);
            seeded.current = { ...seeded.current, principal: principalText, escrow: escrowText, fixed: fixedText };
        }
    }, [detailQuery.data]);

    // A figure the person typed is sent at the dialog's Currency field's places, which
    // may have changed after it was typed; a seeded figure left alone is sent as stored,
    // so the server can compare it with the stored one (ADR-0114 D15).
    const atPlaces = (text: string, seededText: string | undefined): number =>
        text === seededText ? toNum(text) : roundToMinorUnits(toNum(text), currency);

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
                // The payment rounds at the loan's currency's places, so the server
                // needs it (ADR-0114 D15).
                currencyCode: currency,
                originalPrincipal: principal, annualInterestRate: rate,
                paymentCount: count, paymentsPerYear: ppy, escrowAmount: toNum(ltEscrow),
                paymentIsComputed: ltComputed, fixedPayment: ltComputed ? null : fixed,
            })
                .then((r) => { if (!cancelled) setPreview(r); })
                .catch(() => { if (!cancelled) setPreview(null); });
        }, 300);
        return () => { cancelled = true; clearTimeout(handle); };
    }, [isLoan, currency, ltPrincipal, ltRate, ltCount, ltPpy, ltEscrow, ltComputed, ltFixed, ledgerId]);

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
        originalPrincipal: atPlaces(ltPrincipal, seeded.current.principal),
        annualInterestRate: toNum(ltRate),
        points: 0,
        paymentCount: toInt(ltCount),
        paymentsPerYear: toInt(ltPpy),
        firstPaymentDate: ltFirstPayment || null,
        escrowAmount: atPlaces(ltEscrow, seeded.current.escrow),
        interestAccountId: ltInterest || null,
        escrowAccountId: ltEscrowAcct || null,
        paymentIsComputed: ltComputed,
        fixedPayment: ltComputed ? null : atPlaces(ltFixed, seeded.current.fixed),
    });

    // The preview prints in the loan's currency, at its places (ADR-0114 D14).
    const money = (n: number) => formatCurrency(n, currency);

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
            openingBalance: isCategory ? 0 : atPlaces(openingBalance, seeded.current.openingBalance),
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
            categoryKind: isCategory ? categoryKind : undefined,
            openingBalance: isCategory ? 0 : atPlaces(openingBalance, seeded.current.openingBalance),
            // The editor owns the field; a blank value clears the date.
            openedOn: openedOn || undefined,
            clearOpenedOn: openedOn ? undefined : true,
            loanTerms: isLoan ? buildTerms() : undefined,
            // Tax treatment: categories never carry one; '' clears, a value sets.
            taxStatus: isCategory ? undefined : taxStatus,
        }),
        onSuccess: (result) => {
            invalidate();
            // The editor prefills from the ['account', id] detail query via a
            // capture-once useEffect; invalidateLedgerRegister refreshes the
            // plural ['accounts'] list + register but NOT this singular detail
            // key, so reopening the same account within the cache window re-seeded
            // stale metadata / loan terms. Invalidate it too (edit mode only —
            // a create has no detail). Mirrors setupPaymentReminder's onSuccess.
            void queryClient.invalidateQueries({ queryKey: ['account', ledgerId, account!.id] });
            onSaved();
            // A currency change that left transfers keeping two amounts says so, and
            // the dialog stays open on that line until Done (ADR-0114 D13, decided by
            // the owner, 2026-11-18); otherwise it closes, as every save did.
            if (result.becameTwoAmount > 0) setBecameTwoAmount(result.becameTwoAmount);
            else onClose();
        },
        onError: (e) => setError(errorMessage(e, 'Could not save the account.'), mergedIntoOf(e)),
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
    // A failed read leaves the fields holding their blank defaults, and a save sends every text field
    // in full (an empty one clears it), so saving then would erase the account number, the notes and
    // the rest of what was never shown. Until the details are read, there is nothing safe to send.
    const detailFailed = isEdit && detailQuery.isError;
    const submitting = createMut.isPending || updateMut.isPending;
    const currencyOptions = currencyChoices(currency);
    const numberLabel = accountType === 'credit_card' ? 'Card number' : 'Account number';

    // An error stays shown until this save's result replaces it or the dialog
    // closes: cleared at the press, the alert above Save would go and move Save
    // from under the pointer (ADR-0023 §L).
    function handleSave() {
        if (name.trim() === '') { setError('Name is required.'); return; }
        if (isLoan && !loanTermsValid()) {
            setError('Loan terms are incomplete: principal, rate, term, and payments per year are required.');
            return;
        }
        if (isEdit) updateMut.mutate(); else createMut.mutate();
    }

    const title = isEdit ? `Edit ${account!.name}` : 'New account';

    // The saved currency change left transfers keeping two amounts: the same dialog, its
    // title as it was, with the line and Done in place of the form (ADR-0114 D13).
    if (becameTwoAmount > 0) {
        return (
            <Modal open onClose={onClose} titleId="account-editor-title" className="max-w-md">
                <div className="relative flex max-h-[90vh] flex-col overflow-y-auto p-5">
                    <h2 id="account-editor-title" className="mb-3 text-base font-semibold text-text">
                        {title}
                    </h2>
                    <TwoAmountDone count={becameTwoAmount} onDone={onClose} />
                </div>
            </Modal>
        );
    }

    return (
        <Modal open onClose={onClose} titleId="account-editor-title" className="max-w-md">
            <div className="relative flex max-h-[90vh] flex-col overflow-y-auto p-5">
                <h2 id="account-editor-title" className="mb-3 text-base font-semibold text-text">
                    {title}
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
                            {isEdit ? (
                                // READ-ONLY once the category exists (ADR-0017).
                                // Changing a kind does not edit a label: it moves
                                // every posting in the category between the spending
                                // and income totals, for all of its history, with no
                                // record. The server refuses it. Showing an editable
                                // control that always fails would be worse than
                                // showing none — so this matches the Type field above,
                                // which is immutable for the same reason.
                                <input
                                    id="acct-kind"
                                    className={cn(inputClass, 'cursor-not-allowed text-text-muted')}
                                    value={CATEGORY_KIND_LABELS[categoryKind ?? ''] ?? categoryKind ?? ''}
                                    title={"A category's kind can't be changed. Create one of the kind "
                                        + 'you want and merge this into it.'}
                                    readOnly disabled
                                />
                            ) : (
                                <select id="acct-kind" className={inputClass} value={categoryKind ?? 'expense'}
                                    onChange={(e) => setCategoryKind(e.target.value)}>
                                    <option value="expense">Expense</option>
                                    <option value="income">Income</option>
                                    {/* mig 224 / ADR-0017 — neither income nor
                                        expense: a house marked up, a retirement
                                        balance reconciled. Falls out of all three
                                        reporting measures by construction. */}
                                    <option value="adjustment">Adjustment</option>
                                </select>
                            )}
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
                    {/* No "Active" box: whether an account is active is changed from its row menu and the
                        sidebar, where Deactivate asks first (ADR-0111 D4). Saving here never changes it. */}
                    <div className="block">
                        <FieldLabel htmlFor="acct-currency">Currency</FieldLabel>
                        <select id="acct-currency" className={inputClass} value={currency}
                            onChange={(e) => setCurrency(e.target.value)}>
                            {currencyOptions.map((c) => <option key={c} value={c}>{c}</option>)}
                        </select>
                    </div>
                    {!isCategory ? (
                        <div className="flex items-end gap-3">
                            <div className="block flex-1">
                                <FieldLabel htmlFor="acct-opening-balance">Opening balance</FieldLabel>
                                <MoneyInput id="acct-opening-balance" currency={currency} value={openingBalance}
                                    wrapperClassName="mt-1" className={moneyInputClass}
                                    onValueChange={setOpeningBalance} />
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
                                    <MoneyInput id="acct-lt-principal" currency={currency} value={ltPrincipal}
                                        wrapperClassName="mt-1" className={moneyInputClass}
                                        onValueChange={setLtPrincipal} />
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
                                    {/* Escrow is the loan's money, in its currency (ADR-0114 D14). */}
                                    <MoneyInput id="acct-lt-escrow" currency={currency} value={ltEscrow}
                                        wrapperClassName="mt-1" className={moneyInputClass}
                                        onValueChange={setLtEscrow} />
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
                                        <MoneyInput currency={currency} value={ltFixed}
                                            wrapperClassName="w-fixed-128px" className={moneyInputClass}
                                            aria-label="Fixed payment amount"
                                            onValueChange={setLtFixed} />
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
                                                disabled={!remSource || !remStart}
                                                pending={setupReminderMut.isPending}
                                                pendingLabel="Setting up…"
                                                onClick={() => setupReminderMut.mutate()}>
                                                Set up scheduled payment
                                            </Button>
                                        </div>
                                    )}
                                </div>
                            ) : null}
                        </div>
                    </>
                ) : null}

                {detailFailed ? (
                    <div role="alert" className="mt-2 flex items-center justify-between gap-2 text-xs text-state-danger">
                        <span>Could not load this account’s details, so it cannot be saved until they have been.</span>
                        <Button type="button" variant="secondary" size="sm" pending={detailQuery.isFetching}
                            onClick={() => void detailQuery.refetch()}>
                            Try again
                        </Button>
                    </div>
                ) : null}

                {error !== null ? (
                    <p role="alert" className="mt-2 text-xs text-state-danger">
                        {error}
                        {mergedInto.length > 0 ? (
                            <span className="mt-1 block">
                                Merged into:{' '}
                                {mergedInto.map((m, i) => (
                                    <span key={m.headerId}>
                                        {i > 0 ? ', ' : null}
                                        <MergedIntoLink ledgerId={ledgerId} item={m} />
                                    </span>
                                ))}
                            </span>
                        ) : null}
                    </p>
                ) : null}

                <div className="mt-4 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose} pending={submitting}>
                        Cancel
                    </Button>
                    {/* Pending while the details load too, reading Save then. */}
                    <Button type="button" variant="primary" size="sm" onClick={handleSave}
                        disabled={detailFailed} pending={submitting || detailLoading}>
                        <PendingLabel pending={submitting} label={isEdit ? 'Save' : 'Create'} pendingLabel="Saving…" />
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
