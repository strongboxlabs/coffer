import { useMemo } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { Link } from '@tanstack/react-router';

import { ActionFooter } from '@/components/ui/ActionFooter';
import { EditorDialog } from '@/components/ui/EditorDialog';
import { fetchAccounts, fetchHeaderLegs, fetchPayees, fetchRegister } from '@/lib/api';
import { buildAccountPathMap } from '@/lib/accountPath';
import { MIXED_LABEL, MIXED_TITLE } from '@/lib/conversion';
import { formatLedgerDate } from '@/lib/dates';
import { formatSignedAmount } from '@/lib/money';
import { invalidateLedgerRegister } from '@/lib/registerInvalidation';
import type {
    AccountSummary,
    BankRow,
    LedgerEntry,
    PatchTransactionRequest,
    RegisterRow,
} from '@/lib/types';
import { TxnRowEdit } from '@/routes/ledgers/TxnRowEdit';
import { saveRefusal } from '@/routes/ledgers/saveErrorWords';
import { bankEditSeed } from '@/routes/ledgers/bank-edit/editSeed';
import { saveBankEdit } from '@/routes/ledgers/bank-edit/save';
import { investmentEditContext } from '@/routes/ledgers/investment-edit/editContext';
import { InvestmentTxnRowEdit } from '@/routes/ledgers/investment-edit/InvestmentTxnRowEdit';

import { reviewNote } from './roundedNote';
import { shownAmount, shownRow } from './rows';

/**
 * What Review… opens on: one entry of the home screen's entries read (GET
 * …/transactions/entries), a register entry with the account whose register shows
 * it and that account's currency.
 */
export type ReviewDialogItem = LedgerEntry;

/** The footer of a state with no editor: a rule above it, as the editors draw theirs. */
const NO_EDITOR_FOOTER = 'border-t border-border/30 pt-3';

/**
 * The account a read-only row's transaction was entered from: the row's other
 * side, which for an investment event's cash leg is the brokerage that owns
 * the holdings account it faces.
 */
function owningAccount(row: RegisterRow, accounts: readonly AccountSummary[]): AccountSummary | null {
    const otherSide = row.counterpartyAccountId;
    if (otherSide === null) return null;
    return accounts.find((a) => a.holdingsAccountId === otherSide)
        ?? accounts.find((a) => a.id === otherSide)
        ?? null;
}

/**
 * Review… from the home screen's To review list: the row's own register editor
 * in a dialog (ADR-0109), so a row is accepted, folded into a match or edited
 * without leaving the home screen.
 *
 * It re-reads the entry when it opens, with the register's own read for that
 * row on that account, and mounts the editor only once a read made since it
 * opened settles: the editors seed their drafts once, so a seed from the list,
 * or from a read cached by an earlier open, could not be corrected later. The
 * investment editor's leg read is held to the same rule. The read is the home
 * screen's (`['home', …]`), never the
 * register's window key, so opening it reloads no register.
 *
 * Bank accounts get TxnRowEdit, investment accounts InvestmentTxnRowEdit, both
 * in their dialog layout. A row still awaiting review saves as its Accept, with
 * the Similar and Merge strips; one accepted meanwhile opens as a plain edit.
 * A row this register shows read-only opens no editor: it says where the
 * transaction was entered and links there. A failed read offers Retry; a read
 * that answered without the row (deleted or merged away since the list was
 * drawn) says the row is no longer in its register. Every state without an
 * editor ends in a footer Close.
 *
 * `onClose` must be stable across renders.
 */
export function ReviewDialog({ ledgerId, item, onClose }: {
    ledgerId: string;
    item: ReviewDialogItem;
    onClose: () => void;
}) {
    const queryClient = useQueryClient();
    const listed = shownRow(item.entry);
    const headerId = listed.headerId;

    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: true }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: true }),
    });
    const payeesQuery = useQuery({
        queryKey: ['payees', ledgerId],
        queryFn: () => fetchPayees(ledgerId),
        staleTime: 30_000,
    });
    // The row as its register shows it now. Not refetched on focus: the editor
    // seeds from it once. Only a read settled since this dialog opened counts
    // (`isFetchedAfterMount`): the cache can hold one from an earlier open,
    // which TanStack Query returns at once while it refetches, and a seed from
    // it could not be corrected later.
    const freshQuery = useQuery({
        queryKey: ['home', ledgerId, 'review-entry', item.accountId, headerId],
        queryFn: () => fetchRegister({
            ledgerId, accountId: item.accountId, startingAtHeaderId: headerId, limit: 1,
        }),
        staleTime: 0,
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
    });
    const freshSettled = freshQuery.isFetchedAfterMount;

    const accounts = useMemo(() => accountsQuery.data ?? [], [accountsQuery.data]);
    const accountPaths = useMemo(() => buildAccountPathMap(accounts), [accounts]);
    const account = accounts.find((a) => a.id === item.accountId) ?? null;
    const isInvestment = account?.accountType === 'investment';

    const freshEntry = freshSettled ? freshQuery.data?.entries[0] ?? null : null;
    const fresh = freshEntry !== null && shownRow(freshEntry).headerId === headerId ? freshEntry : null;
    const freshRow = fresh === null ? null : shownRow(fresh);
    const editable = freshRow !== null && !freshRow.isReadOnly;

    // The title: the row as its register shows it now, else as the list did.
    const titleRow = freshRow ?? listed;
    const payee = titleRow.payee ?? '(no payee)';
    const amount = shownAmount(fresh ?? item.entry);
    // "mixed" carries its hover here as everywhere it stands in for a total (ADR-0114 D7, D14).
    const subtitle = (
        <>
            {formatLedgerDate(titleRow.postedAt)} · {amount === null
                ? <span title={MIXED_TITLE}>{MIXED_LABEL}</span>
                : formatSignedAmount(amount, item.currencyCode)}
            {account !== null ? ` · ${account.name}` : ''}
        </>
    );

    // The investment editor seeds from the transaction's full leg set, read
    // since this dialog opened for the same reason as the row: the register's
    // editor caches the same key.
    const legsQuery = useQuery({
        queryKey: ['header-legs', ledgerId, headerId],
        queryFn: () => fetchHeaderLegs(ledgerId, headerId),
        enabled: isInvestment && editable,
        staleTime: 0,
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
    });
    const legsSettled = legsQuery.isFetchedAfterMount;

    // Everything a review can change: the registers and the home screen, and
    // the editors' own caches, which nothing else refetches.
    function afterSave() {
        invalidateLedgerRegister(queryClient, ledgerId);
        void queryClient.invalidateQueries({ queryKey: ['merge-candidates', ledgerId] });
        void queryClient.invalidateQueries({ queryKey: ['similar-payees', ledgerId] });
        void queryClient.invalidateQueries({ queryKey: ['investment-merge-candidates', ledgerId] });
        void queryClient.invalidateQueries({ queryKey: ['payees', ledgerId] });
        onClose();
    }

    const saveBank = useMutation({
        mutationFn: (body: PatchTransactionRequest) =>
            saveBankEdit(ledgerId, headerId, body, item.accountId),
        onSuccess: afterSave,
    });

    // The editors' seeds, built once per read: both editors seed their drafts
    // once, and the investment one reads its seed's identity afterwards.
    const bankSeed = useMemo(
        () => (fresh === null || isInvestment
            ? null
            : bankEditSeed(fresh.kind === 'txn'
                ? { kind: 'txn', txn: fresh.txn as BankRow }
                : { kind: 'group', legs: fresh.legs as BankRow[] })),
        [fresh, isInvestment],
    );
    const legs = legsSettled ? legsQuery.data : undefined;
    const investmentContext = useMemo(
        () => (isInvestment && legs !== undefined && legs.length > 0
            ? investmentEditContext(legs[0]!.headerId, item.accountId, legs)
            : null),
        [isInvestment, item.accountId, legs],
    );

    const loading = accountsQuery.isPending || payeesQuery.isPending || !freshSettled
        || (isInvestment && editable && !legsSettled);
    // A read failed: another try can still open the editor.
    const loadFailed = accountsQuery.isError || payeesQuery.isError || freshQuery.isError
        || (isInvestment && editable && legsQuery.isError);
    // Every read answered and the row is not in them: deleted, or merged away,
    // since the list was drawn. Not an error, and no retry brings it back.
    const gone = (freshQuery.isSuccess && fresh === null)
        || (isInvestment && editable && legsSettled && legsQuery.isSuccess && investmentContext === null);
    const retrying = accountsQuery.isFetching || payeesQuery.isFetching || freshQuery.isFetching
        || legsQuery.isFetching;
    function retry() {
        if (accountsQuery.isError) void accountsQuery.refetch();
        if (payeesQuery.isError) void payeesQuery.refetch();
        if (freshQuery.isError) void freshQuery.refetch();
        if (legsQuery.isError) void legsQuery.refetch();
    }

    const owner = freshRow !== null && freshRow.isReadOnly ? owningAccount(freshRow, accounts) : null;
    const needsReview = freshRow?.needsReview ?? false;
    // Why the bank's figure put the row in review, while it awaits review: what the bank
    // cleared, or what it sent when its import rounded it. Amount's description in either
    // editor (ADR-0114 D15, board RV-2). From the row as read now, so a row accepted
    // meanwhile has none.
    const amountNote = fresh === null || freshRow === null ? null : reviewNote(freshRow, shownAmount(fresh));
    const acceptedNote = (
        <p className="text-xs text-text-muted">Already accepted. Changes save as a normal edit.</p>
    );
    // Every state without an editor ends in a footer Close, beside the
    // header's ✕, as the editors end in their Cancel.
    const closeFooter = (
        <ActionFooter cancel={{ label: 'Close', onClick: onClose }} dialog className={NO_EDITOR_FOOTER} />
    );
    const loadErrorState = (
        <>
            <p role="alert" className="py-6 text-center text-sm text-state-danger">
                Could not load this transaction.
            </p>
            <ActionFooter
                cancel={{ label: 'Close', onClick: onClose }}
                confirm={{ label: 'Retry', pendingLabel: 'Retrying…', onClick: retry, pending: retrying }}
                dialog
                className={NO_EDITOR_FOOTER}
            />
        </>
    );

    return (
        <EditorDialog
            ariaLabel={`Review: ${payee}`}
            title={`Review: ${payee}`}
            subtitle={subtitle}
            onClose={onClose}
            bodyClassName="space-y-3"
        >
            {loading ? (
                <>
                    <p className="py-6 text-center text-sm text-text-subtle">Loading…</p>
                    {closeFooter}
                </>
            ) : loadFailed ? (
                loadErrorState
            ) : gone || freshRow === null ? (
                <>
                    <p className="py-6 text-center text-sm text-text-muted">
                        This row is no longer in its register.
                    </p>
                    {closeFooter}
                </>
            ) : freshRow.isReadOnly ? (
                <>
                    <div className="flex flex-col items-center gap-2 py-4 text-center">
                        <p className="text-sm text-text-muted">
                            {owner !== null
                                ? `This row is read-only here: it belongs to a transaction entered from ${owner.name}.`
                                : 'This row is read-only here.'}
                        </p>
                        {owner !== null ? (
                            <Link
                                to="/ledgers/$ledgerId/accounts/$accountId"
                                params={{ ledgerId, accountId: owner.id }}
                                search={{ focus: headerId }}
                                onClick={onClose}
                                className="text-sm font-medium text-accent hover:underline"
                            >
                                Open in {owner.name} →
                            </Link>
                        ) : null}
                    </div>
                    {closeFooter}
                </>
            ) : isInvestment && investmentContext !== null && account !== null ? (
                <>
                    {needsReview ? null : acceptedNote}
                    <InvestmentTxnRowEdit
                        ledgerId={ledgerId}
                        brokerageAccountId={item.accountId}
                        accounts={accounts}
                        currency={item.currencyCode}
                        isTradeCommission={account.isTradeCommission}
                        layout="dialog"
                        onCancel={onClose}
                        hints={needsReview}
                        amountNote={amountNote}
                        mode={{
                            kind: 'edit',
                            headerId: investmentContext.headerId,
                            initialDraft: investmentContext.initialDraft,
                            providerSecurityHint: investmentContext.providerSecurityHint,
                            needsReview,
                            inherited: investmentContext.inherited,
                            onSaved: afterSave,
                        }}
                    />
                </>
            ) : bankSeed !== null ? (
                <>
                    {needsReview ? null : acceptedNote}
                    <TxnRowEdit
                        ledgerId={ledgerId}
                        mode={bankSeed}
                        payees={payeesQuery.data ?? []}
                        accounts={accounts}
                        accountPaths={accountPaths}
                        currency={item.currencyCode}
                        layout="dialog"
                        onSavePatch={(body) => saveBank.mutate(body)}
                        onCancel={onClose}
                        isSaving={saveBank.isPending}
                        saveError={saveBank.error === null ? null : saveRefusal(saveBank.error)}
                        cancelOnOutsideClick={false}
                        hints={needsReview}
                        amountNote={amountNote}
                    />
                </>
            ) : (
                loadErrorState
            )}
        </EditorDialog>
    );
}
