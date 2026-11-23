import { useMemo } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import type { AccountSummary, BankRow, InvestmentRow, MergeCandidateDto, SimilarPayeeDto } from '@/lib/types';
import { splitParentNotes } from '@/lib/annotations/splitHeader';
import { InheritedText } from '@/components/register/InheritedMarker';
import { TagLine } from '@/components/tags/TagLine';
import { TagColorsValueProvider } from '@/components/tags/TagColorsContext';
import { RegisterRow } from '@/routes/ledgers/register/shell/RegisterRow';
import { bankRowStrategy } from '@/routes/ledgers/register/strategies/bankRowStrategy';
import { investmentRowStrategy } from '@/routes/ledgers/register/strategies/investmentRowStrategy';
import { TxnRowEdit, type TxnRowMode } from '@/routes/ledgers/TxnRowEdit';
import { BANK_COLS } from '@/routes/ledgers/register/bank/columns';
import { systemCategoryIds } from '@/lib/categoryChip';

import { Board, WithoutMountFocus } from './SampleBoard';
import {
    SAMPLE_DINING, SAMPLE_LEDGER, sampleAccount as account, sampleBankRow as bank,
    sampleInvestmentRow as investment,
} from './sampleRows';

/**
 * Style-guide samples for memo and tags per side: every register shape and
 * editor state the design boards show, rendered through the real row strategy
 * and the real editor so a capture of this page is a capture of the product.
 *
 * Data is placeholder only. The editor samples run against a local query
 * client that never fetches; its tag dictionary is seeded below.
 */

const LEDGER = SAMPLE_LEDGER;
const CHECKING = 'sg-checking';
const DINING = SAMPLE_DINING;
const GROCERIES = 'sg-groceries';
const SUPPLIES = 'sg-supplies';
const BROKERAGE = 'sg-brokerage';
const COMMISSION = 'sg-commission';
const OTHER_BROKERAGE = 'sg-brokerage-2';
/** The one transaction every editor sample opens; its per-transaction queries are seeded under it. */
const EDIT_HEADER = 'sg-edit';
/** The editor samples that show the hints under Payee; their queries are seeded with placeholder hits. */
const HINTS_HEADER = 'sg-edit-hints';
const SPLIT_HINTS_HEADER = 'sg-edit-split-hints';

const SIMILAR_PAYEES: SimilarPayeeDto[] = [
    { payee: 'Sample Payee A', counterpartyAccountId: DINING, counterpartyAccountName: 'Food/Dining', useCount: 3, lastUsedAt: '2026-09-28T12:00:00Z' },
    { payee: 'Sample Payee B', counterpartyAccountId: GROCERIES, counterpartyAccountName: 'Food/Groceries', useCount: 1, lastUsedAt: '2026-09-14T12:00:00Z' },
];

const MERGE_CANDIDATES: MergeCandidateDto[] = [
    {
        headerId: 'sg-merge-1', payee: 'Sample Payee A', postedAt: '2026-10-01T12:00:00Z',
        postings: [{ counterpartyAccountId: DINING, counterpartyAccountName: 'Food/Dining' }],
        origin: 'manual',
    },
];

const TAGS = [
    { id: 'sg-t1', name: 'Work', color: '#14b8a6', usageCount: 4, transactionCount: 4 },
    { id: 'sg-t2', name: 'Reimbursable', color: '#f97316', usageCount: 2, transactionCount: 2 },
    { id: 'sg-t3', name: 'Household', color: '#3b82f6', usageCount: 3, transactionCount: 3 },
    { id: 'sg-t4', name: 'Organic', color: '#10b981', usageCount: 1, transactionCount: 1 },
    { id: 'sg-t5', name: 'Retirement', color: '#6366f1', usageCount: 2, transactionCount: 2 },
];

const ACCOUNTS: AccountSummary[] = [
    account(CHECKING, 'Checking', 'bank'),
    account(DINING, 'Food/Dining', 'category'),
    account(GROCERIES, 'Food/Groceries', 'category'),
    account(SUPPLIES, 'Home/Supplies', 'category'),
    account(BROKERAGE, 'Brokerage', 'investment'),
    account(COMMISSION, 'Investment/Commission', 'category'),
    account(OTHER_BROKERAGE, 'Other Brokerage', 'investment'),
];
const PATHS = new Map(ACCOUNTS.map((a) => [a.id, a.name]));
/** The samples' system categories, read from the accounts as a register reads them. */
const SYSTEM_CATEGORY_IDS = systemCategoryIds(ACCOUNTS);

const today = new Date('2026-10-23T12:00:00Z');
const noop = () => {};

function BankRows({ rows }: { rows: Array<{ row: BankRow; variant?: 'txn' | 'split-parent' | 'split-leg'; legs?: BankRow[]; scheduled?: boolean; selected?: boolean; focused?: boolean }> }) {
    return (
        <>
            {rows.map(({ row, variant = 'txn', legs, scheduled = false, selected = false, focused = false }) => (
                <RegisterRow
                    key={row.id + variant}
                    strategy={bankRowStrategy}
                    variant={variant}
                    row={row}
                    legs={legs}
                    currency="USD"
                    today={today}
                    focused={focused}
                    onFocus={noop}
                    needsReview={row.needsReview}
                    selected={selected}
                    onToggleSelected={noop}
                    status={scheduled ? 'scheduled' : row.status}
                    cycleStatus={row.status}
                    statusHeaderId={row.headerId}
                    statusStatic={scheduled}
                    onCycleReconStatus={noop}
                    readOnly={row.isReadOnly}
                    accountPaths={PATHS}
                    systemCategoryIds={SYSTEM_CATEGORY_IDS}
                    expand={variant === 'split-parent'
                        ? { expanded: true, onToggle: noop, count: legs?.length ?? 0, groupId: row.headerId }
                        : undefined}
                />
            ))}
        </>
    );
}

function InvestmentRows({ rows }: { rows: InvestmentRow[] }) {
    return (
        <>
            {rows.map((row) => (
                <RegisterRow
                    key={row.id}
                    strategy={investmentRowStrategy}
                    variant="txn"
                    row={row}
                    currency="USD"
                    today={today}
                    focused={false}
                    onFocus={noop}
                    selected={false}
                    onToggleSelected={noop}
                    status={row.status}
                    cycleStatus={row.status}
                    statusHeaderId={row.headerId}
                    onCycleReconStatus={noop}
                    readOnly={row.isReadOnly}
                    accountPaths={PATHS}
                    systemCategoryIds={SYSTEM_CATEGORY_IDS}
                />
            ))}
        </>
    );
}

function Editor({ mode }: { mode: TxnRowMode }) {
    return (
        <TxnRowEdit
            ledgerId={LEDGER}
            mode={mode}
            payees={[]}
            accounts={ACCOUNTS}
            accountPaths={PATHS}
            currency="USD"
            cols={BANK_COLS}
            onCancel={noop}
            onSavePatch={noop}
            isSaving={false}
            saveError={null}
            cancelOnOutsideClick={false}
        />
    );
}

const fromChecking = (memo: string, tags: string[]) => ({
    memo, tags, memoInherited: true, tagsInherited: true,
    inheritedMemo: memo, inheritedTags: tags, inheritFromAccountId: CHECKING,
});

function editMode(over: Partial<Extract<TxnRowMode, { kind: 'edit' }>>): TxnRowMode {
    return {
        kind: 'edit', headerId: EDIT_HEADER, sourceAccountId: CHECKING,
        postings: [{ legId: 'sg-edit-leg', counterpartyAccountId: DINING, counterpartyAccountName: 'Food/Dining', amount: -12, legMemo: null, legTags: [] }],
        payee: 'Corner Café', memo: 'Team coffee', checkNumber: null,
        postedAt: '2026-10-02T00:00:00.000Z', transactedAt: null, balanceAfter: 2000, balanceCurrencyCode: 'USD',
        tags: ['Work'], inherited: null, needsReview: false,
        ...over,
    };
}

export function MemoTagsSamples() {
    // Local and offline. `enabled: false` holds the editor queries that leave
    // `enabled` unset; the two that set their own (similar payees and merge
    // candidates, enabled for any edit-mode editor) override it, so they are
    // answered from the cache instead: seeded empty under the editor's keys and
    // never stale, a seeded query does not fetch. The tag dictionary the editors
    // autocomplete from is seeded the same way.
    const client = useMemo(() => {
        const c = new QueryClient({
            defaultOptions: { queries: { enabled: false, retry: false, staleTime: Infinity, gcTime: Infinity } },
        });
        c.setQueryData(['tags', LEDGER], TAGS);
        c.setQueryData<SimilarPayeeDto[]>(['similar-payees', LEDGER, EDIT_HEADER], []);
        c.setQueryData<MergeCandidateDto[]>(['merge-candidates', LEDGER, EDIT_HEADER], []);
        c.setQueryData<SimilarPayeeDto[]>(['similar-payees', LEDGER, HINTS_HEADER], SIMILAR_PAYEES);
        c.setQueryData<MergeCandidateDto[]>(['merge-candidates', LEDGER, HINTS_HEADER], MERGE_CANDIDATES);
        c.setQueryData<MergeCandidateDto[]>(['merge-candidates', LEDGER, SPLIT_HINTS_HEADER], MERGE_CANDIDATES);
        return c;
    }, []);

    const split = { accountId: CHECKING, payee: 'Grocery Mart', headerId: 'sg-split', txnGroupId: 'sg-split', accountPostingsOnHeader: 2, headerTotalPostings: 2, notesKind: 'splitSide' as const, headerMemo: 'Weekly shop', headerTags: ['Household'], postedAt: '2026-10-05T12:00:00Z', balanceAfter: 1858 };
    const splitLegs = [
        bank({ ...split, legIndex: 0, amount: -60, counterpartyAccountId: GROCERIES, counterpartyAccountName: 'Food/Groceries', memo: 'Produce', tags: ['Organic'] }),
        bank({ ...split, legIndex: 1, amount: -30, counterpartyAccountId: SUPPLIES, counterpartyAccountName: 'Home/Supplies', memo: null, tags: [] }),
    ];
    const splitParent = { ...splitParentNotes(splitLegs[0]!, splitLegs), amount: -90 };

    return (
        <TagColorsValueProvider colors={TAGS}>
            <QueryClientProvider client={client}>
                <WithoutMountFocus className="space-y-6">
                    <Board title="Inherited marker" note="Own values are plain; an inherited value leads with the corner arrow, in the subtle colour; the account it comes from is the arrow's tooltip and label.">
                        <div className="grid grid-cols-2 gap-4 p-3 text-xs">
                            <div className="space-y-1">
                                <span className="block text-[0.6875rem] text-text-muted">Team coffee</span>
                                <TagLine tags={['Work']} inset />
                            </div>
                            <div className="space-y-1">
                                <InheritedText inherited from="Checking" className="text-[0.6875rem]">Team coffee</InheritedText>
                                <TagLine tags={['Work']} inset inherited={{ from: 'Checking' }} />
                            </div>
                        </div>
                    </Board>

                    <Board title="One-split · Checking (entered it)">
                        <BankRows rows={[
                            { row: bank({ accountId: CHECKING, memo: 'Team coffee', tags: ['Work'], status: 'cleared' }) },
                            { row: bank({ accountId: CHECKING, payee: 'Bistro Nine', amount: -40, memo: 'Card purchase', tags: ['Work'], postedAt: '2026-10-03T12:00:00Z', balanceAfter: 1948 }) },
                        ]} />
                    </Board>

                    <Board title="One-split · Food/Dining (category register)" note="Row 1 inherits Checking's memo and tags. Row 2 has its own.">
                        <BankRows rows={[
                            { row: bank({ accountId: DINING, counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', counterpartyAccountType: 'bank', amount: 12, balanceAfter: 212, status: 'cleared', ...fromChecking('Team coffee', ['Work']) }) },
                            { row: bank({ accountId: DINING, payee: 'Bistro Nine', counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', counterpartyAccountType: 'bank', amount: 40, balanceAfter: 252, memo: 'Client lunch', tags: ['Reimbursable'], inheritedMemo: 'Card purchase', inheritedTags: ['Work'], inheritFromAccountId: CHECKING }) },
                        ]} />
                    </Board>

                    <Board title="Scheduled rows · Food/Dining" note="Future-dated rows: the inherited one beside an own one, so the marker still tells them apart when the whole row is in the scheduled style.">
                        <BankRows rows={[
                            { scheduled: true, row: bank({ accountId: DINING, payee: 'Corner Café', counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', counterpartyAccountType: 'bank', amount: 12, balanceAfter: 264, postedAt: '2026-11-02T12:00:00Z', ...fromChecking('Team coffee', ['Work']) }) },
                            { scheduled: true, row: bank({ accountId: DINING, payee: 'Bistro Nine', counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', counterpartyAccountType: 'bank', amount: 40, balanceAfter: 304, postedAt: '2026-11-03T12:00:00Z', memo: 'Client lunch', tags: ['Reimbursable'], inheritedMemo: 'Card purchase', inheritedTags: ['Work'], inheritFromAccountId: CHECKING }) },
                        ]} />
                    </Board>

                    <Board title="Row states · Food/Dining" note="Three rows inheriting Checking's memo and tags: one selected, one focused, one needing review.">
                        <BankRows rows={[
                            { selected: true, row: bank({ accountId: DINING, counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', counterpartyAccountType: 'bank', amount: 12, balanceAfter: 212, ...fromChecking('Team coffee', ['Work']) }) },
                            { focused: true, row: bank({ accountId: DINING, payee: 'Bistro Nine', counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', counterpartyAccountType: 'bank', amount: 40, balanceAfter: 252, ...fromChecking('Card purchase', ['Work']) }) },
                            { row: bank({ accountId: DINING, payee: 'Market Hall', counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', counterpartyAccountType: 'bank', amount: 9, balanceAfter: 261, needsReview: true, ...fromChecking('Snacks', ['Work']) }) },
                        ]} />
                    </Board>

                    <Board title="Multi-split from the split side · Checking, expanded" note="The transaction row shows the transaction's memo and tags; each line its own; a line with no memo shows —.">
                        <BankRows rows={[
                            { row: splitParent, variant: 'split-parent', legs: splitLegs },
                            { row: splitLegs[0]!, variant: 'split-leg' },
                            { row: splitLegs[1]!, variant: 'split-leg' },
                        ]} />
                    </Board>

                    <Board title="A split line from its opposite side · Food/Groceries" note="Read-only; inherits the line's memo and tags, never the transaction's.">
                        <BankRows rows={[
                            { row: bank({ accountId: GROCERIES, payee: 'Grocery Mart', headerId: 'sg-split', txnGroupId: 'sg-split', counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', counterpartyAccountType: 'bank', amount: 60, balanceAfter: 460, postedAt: '2026-10-05T12:00:00Z', accountPostingsOnHeader: 1, headerTotalPostings: 2, notesKind: 'splitOpposite', isReadOnly: true, ...fromChecking('Produce', ['Organic']) }) },
                        ]} />
                    </Board>

                    <Board title="Investment event · Brokerage register">
                        <InvestmentRows rows={[
                            investment({ accountId: BROKERAGE, payee: 'Rebalance', investmentAction: 'buy', derivedAction: 'buy', amount: -505, balanceAfter: 995, feeAmount: 5, feeCategoryId: COMMISSION, feeCategoryName: 'Investment/Commission', securityTicker: 'IDXF', securityName: 'Index Fund', quantity: 10, unitPrice: 50, memo: 'Quarterly rebalance', tags: ['Retirement'], headerMemo: 'Quarterly rebalance', headerTags: ['Retirement'], notesKind: 'eventSide', accountPostingsOnHeader: 2, headerTotalPostings: 2, postedAt: '2026-10-06T12:00:00Z' }),
                            investment({ accountId: BROKERAGE, payee: 'Contribution', investmentAction: 'transfer', derivedAction: 'transfer', postingRole: 'transfer', amount: 500, balanceAfter: 1500, transferAccountId: CHECKING, transferAccountName: 'Checking', transferAccountType: 'bank', memo: 'Monthly', tags: ['Retirement'], postedAt: '2026-10-01T12:00:00Z' }),
                        ]} />
                    </Board>

                    <Board title="Read-only rows · Brokerage register" note="A paycheck split's line landing here, and the funding side of a buy in another brokerage. Both read-only, each with its action chip.">
                        <InvestmentRows rows={[
                            investment({ accountId: BROKERAGE, payee: 'Paycheck', investmentAction: null, derivedAction: 'Xfr', postingRole: null, amount: 300, balanceAfter: 1800, counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', counterpartyAccountType: 'bank', transferAccountId: CHECKING, transferAccountName: 'Checking', transferAccountType: 'bank', accountPostingsOnHeader: 1, headerTotalPostings: 3, notesKind: 'splitOpposite', isReadOnly: true, postedAt: '2026-10-08T12:00:00Z', ...fromChecking('Deferral', ['Retirement']) }),
                            investment({ accountId: BROKERAGE, payee: 'Rollover', investmentAction: 'buyx', derivedAction: 'buyx', postingRole: 'transfer', amount: -250, balanceAfter: 1550, counterpartyAccountId: OTHER_BROKERAGE, counterpartyAccountName: 'Other Brokerage', counterpartyAccountType: 'investment', transferAccountId: OTHER_BROKERAGE, transferAccountName: 'Other Brokerage', transferAccountType: 'investment', accountPostingsOnHeader: 1, headerTotalPostings: 2, notesKind: 'eventOpposite', isReadOnly: true, postedAt: '2026-10-09T12:00:00Z', ...fromChecking('Rollover buy', ['Retirement']), inheritFromAccountId: OTHER_BROKERAGE }),
                        ]} />
                    </Board>

                    <Board title="Investment event · its opposite sides (Checking, Investment/Commission)" note="Both inherit the event's memo and tags, one posting or several; both read-only.">
                        <BankRows rows={[
                            { row: bank({ accountId: CHECKING, payee: 'Contribution', amount: -500, balanceAfter: 1448, counterpartyAccountId: BROKERAGE, counterpartyAccountName: 'Brokerage', counterpartyAccountType: 'investment', derivedAction: 'transfer', notesKind: 'eventCounterpartOne', isReadOnly: true, postedAt: '2026-10-01T12:00:00Z', ...fromChecking('Monthly', ['Retirement']), inheritFromAccountId: BROKERAGE }) },
                            { row: bank({ accountId: COMMISSION, payee: 'Rebalance', amount: 5, balanceAfter: 45, counterpartyAccountId: BROKERAGE, counterpartyAccountName: 'Brokerage', counterpartyAccountType: 'investment', derivedAction: 'buy', notesKind: 'eventOpposite', isReadOnly: true, postedAt: '2026-10-06T12:00:00Z', ...fromChecking('Quarterly rebalance', ['Retirement']), inheritFromAccountId: BROKERAGE }) },
                        ]} />
                    </Board>

                    <Board title="One-split editor · a · Checking, its own values">
                        <Editor mode={editMode({})} />
                    </Board>

                    <Board title="One-split editor · b · Food/Dining, inheriting Checking's">
                        <Editor mode={editMode({
                            sourceAccountId: DINING,
                            postings: [{ legId: 'sg-edit-leg', counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', amount: 12, legMemo: null, legTags: [] }],
                            memo: null, tags: [], balanceAfter: 200,
                            inherited: { memo: 'Team coffee', tags: ['Work'], fromAccountId: CHECKING },
                        })} />
                    </Board>

                    <Board title="One-split editor · c · Food/Dining with its own memo and tags">
                        <Editor mode={editMode({
                            sourceAccountId: DINING, payee: 'Bistro Nine',
                            postings: [{ legId: 'sg-edit-leg', counterpartyAccountId: CHECKING, counterpartyAccountName: 'Checking', amount: 40, legMemo: null, legTags: [] }],
                            memo: 'Client lunch', tags: ['Reimbursable'], balanceAfter: 212,
                            inherited: { memo: 'Card purchase', tags: ['Work'], fromAccountId: CHECKING },
                        })} />
                    </Board>

                    <Board title="One-split editor · d · Checking, with hints under Payee" note="Placeholder Similar and Merge candidates hits. The hints sit under Payee; Memo and Tags start below them, together.">
                        <Editor mode={editMode({ headerId: HINTS_HEADER })} />
                    </Board>

                    <Board title="Split editor · Checking">
                        <Editor mode={editMode({
                            payee: 'Grocery Mart', memo: 'Weekly shop', tags: ['Household'], balanceAfter: 1858,
                            postings: [
                                { legId: 'sg-l1', counterpartyAccountId: GROCERIES, counterpartyAccountName: 'Food/Groceries', amount: -60, legMemo: 'Produce', legTags: ['Organic'] },
                                { legId: 'sg-l2', counterpartyAccountId: SUPPLIES, counterpartyAccountName: 'Home/Supplies', amount: -30, legMemo: null, legTags: [] },
                            ],
                        })} />
                    </Board>

                    <Board title="Split editor · Checking, with a hint under Payee" note="A placeholder Merge candidates hit, the one hint a split shows.">
                        <Editor mode={editMode({
                            headerId: SPLIT_HINTS_HEADER,
                            payee: 'Grocery Mart', memo: 'Weekly shop', tags: ['Household'], balanceAfter: 1858,
                            postings: [
                                { legId: 'sg-l1', counterpartyAccountId: GROCERIES, counterpartyAccountName: 'Food/Groceries', amount: -60, legMemo: 'Produce', legTags: ['Organic'] },
                                { legId: 'sg-l2', counterpartyAccountId: SUPPLIES, counterpartyAccountName: 'Home/Supplies', amount: -30, legMemo: null, legTags: [] },
                            ],
                        })} />
                    </Board>
                </WithoutMountFocus>
            </QueryClientProvider>
        </TagColorsValueProvider>
    );
}
