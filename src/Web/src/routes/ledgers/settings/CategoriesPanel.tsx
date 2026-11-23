import { useMemo, useRef, useState } from 'react';
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { Link, useNavigate } from '@tanstack/react-router';
import { Scale, TrendingDown, TrendingUp } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';

import { deleteCategory, fetchAccounts, fetchCategories } from '@/lib/api';
import type { CategoryNode } from '@/lib/types';
import { formatCurrency } from '@/lib/money';
import { errorMessage } from '@/lib/errorMessage';
import { cn } from '@/lib/cn';
import { usePersistedDisclosure } from '@/lib/usePersistedDisclosure';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { buildAccountPathMap } from '@/lib/accountPath';
import { NO_RATE_LABEL, rateUnavailableTitle } from '@/lib/conversion';
import type { RateUnavailable } from '@/lib/types';
import { invalidateHomeReads } from '@/lib/registerInvalidation';
import { Button } from '@/components/ui/Button';
import { Panel } from '@/components/ui/Panel';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ContextMenu, type ContextMenuItem } from '@/components/ui/ContextMenu';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';

import { LedgerConversionNote } from '@/components/LedgerConversionNote';

import { NoRate } from '../home/Money';
import { DeactivateConfirm } from '../structure/DeactivateConfirm';
import { normalizeQuery, rowLinkClass, transactionCountLabel } from '../structure/rowStyles';
import { StructureGroupHeading } from '../structure/StructureGroupHeading';
import { StructureBadge, StructureRow } from '../structure/StructureRow';
import { ActionError, ListEmpty, ListError, ListLoading, ListNoMatches } from '../structure/StructureStates';
import { StructureToolbar } from '../structure/StructureToolbar';
import { useRowMenu } from '../structure/useRowMenu';
import { useSetActive } from '../structure/useSetActive';
import {
    buildForest,
    canMergeAway,
    categoryIdsMatching,
    flattenForest,
    type CategoryTreeNode,
} from './categoryTree';
import {
    CategoryCreateDialog,
    CategoryMergeDialog,
    CategoryMoveDialog,
    CategoryRenameDialog,
} from './CategoryDialogs';

/**
 * Normalize a category's raw signed total to a positive-natural display
 * magnitude: expense nets positive (stays as-is), income nets negative so
 * earnings read positive under the Income header.
 *
 * `adjustment` (mig 224) is deliberately NOT normalized, and the arm is written
 * out rather than left to fall through a default. The
 * other two kinds have a natural direction — money earned, money spent — and
 * the negation exists to make each read positive under its own header. An
 * adjustment has no such direction: marking a house up and reconciling a
 * retirement balance down are the same kind of entry with opposite signs.
 * Negating it would assert the income reading; leaving it signed states what
 * happened.
 */
/** The kinds the panel draws, each as its own section, in this order. */
const SECTION_KINDS = ['income', 'expense', 'adjustment'] as const;

function displayTotal(total: number, kind: string): number {
    switch (kind) {
        case 'income': return -total;
        case 'adjustment': return total;
        default: return total;
    }
}

/**
 * The Categories tab of Structure (ADR-0017 / ADR-0068 Slice A, ADR-0111). Manage the budget
 * categories that are otherwise only reachable inline in the transaction
 * editor: see the full income/expense hierarchy, create / rename / move /
 * merge / delete / deactivate, with per-category usage counts. Categories ARE accounts
 * (`account_type='category'`), so this reads the dedicated /categories
 * endpoint (hierarchy + usage) and writes through the accounts endpoints
 * (create/rename/active) plus the /categories hierarchy ops (move/merge/delete).
 *
 * The toolbar, the row, the menu and the states are the ones the other two tabs use. What is
 * this list's own is the kind sections, which collapse and carry a total, and the tree.
 */
export function CategoriesPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const navigate = useNavigate();
    const { canEdit } = useLedgerRole(ledgerId);
    const [showInactive, setShowInactive] = useState(false);
    const [query, setQuery] = useState('');

    const categoriesQuery = useQuery({
        queryKey: ['categories', ledgerId, showInactive],
        queryFn: () => fetchCategories(ledgerId, { includeInactive: showInactive }),
        // Ticking "Show inactive" changes the key. Keeping the rows already drawn until the new
        // ones land means the list does not blink to a skeleton and the toolbar, with the box the
        // reader is standing on, is not disabled under them.
        placeholderData: keepPreviousData,
    });
    // The dialogs' parent / merge-target pickers reuse AccountCategoryPicker,
    // which wants the full ledger account list (it builds parent-name
    // qualifiers + filters via isEligible).
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId],
        queryFn: () => fetchAccounts(ledgerId),
    });
    const categories = useMemo(() => categoriesQuery.data?.categories ?? [], [categoriesQuery.data]);
    const accounts = useMemo(() => accountsQuery.data ?? [], [accountsQuery.data]);
    // A category has no currency of its own: its lines carry the paying account's
    // (ADR-0109 D9). Every figure here is converted into the ledger's reporting currency
    // (ADR-0114 D7), which the categories read carries beside the rows, as the budget
    // month does, so a figure never waits on another read and never wears a code stored
    // on a category.
    const ledgerCurrency = useMemo(
        () => (categoriesQuery.data === undefined ? null : { code: categoriesQuery.data.currencyCode }),
        [categoriesQuery.data],
    );

    // The TREE below keeps its bare leaf names — indentation is the answer there.
    // These paths are for everything that leaves the tree: the confirm dialogs,
    // and the tooltip and screen-reader label on each row. That label matters
    // more than it looks: this panel is not an ARIA tree (no role="tree", no
    // aria-level), so the indentation is purely visual and a screen reader gets
    // no ancestry at all without it.
    const categoryPaths = useMemo(() => buildAccountPathMap(accounts), [accounts]);
    const pathOf = (node: CategoryNode) => categoryPaths.get(node.id) ?? node.name;

    // What a search keeps: the categories that match and every ancestor of one, so a match is
    // never shown without the path to it. Null when nothing is being searched for.
    const q = normalizeQuery(query);
    const visible = useMemo(
        () => (q === '' ? null : categoryIdsMatching(categories, q)),
        [categories, q],
    );
    const shown = useMemo(
        () => (visible === null ? categories : categories.filter((c) => visible.has(c.id))),
        [categories, visible],
    );

    // A category mutation ripples beyond the tree: names/active state feed
    // the register chips + pickers (accounts), the register itself
    // (['register', …] is the ADR-0079 canonical key that reloads it) and the
    // home screen and the budget, whose spending and review rows name categories.
    const onSaved = () => {
        void queryClient.invalidateQueries({ queryKey: ['categories', ledgerId] });
        void queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
        invalidateHomeReads(queryClient, ledgerId);
        void queryClient.invalidateQueries({ queryKey: ['register', ledgerId] });
    };

    type DialogState =
        | { kind: 'create'; presetKind?: string; presetParentId?: string | null }
        | { kind: 'rename'; node: CategoryNode }
        | { kind: 'move'; node: CategoryNode }
        | { kind: 'merge'; node: CategoryNode };
    const [dialog, setDialog] = useState<DialogState | null>(null);
    const [deleteTarget, setDeleteTarget] = useState<CategoryNode | null>(null);
    const [deactivating, setDeactivating] = useState<CategoryNode | null>(null);
    const rowMenu = useRowMenu<CategoryNode>();
    const setActive = useSetActive(ledgerId);
    // A category deleted or merged away takes its row's kebab with it: focus goes
    // to the next row's in the order drawn (each kind's tree, kind by kind), the
    // previous row's, or New category (ADR-0023 §L). A row in a folded group has
    // no kebab on the page and is passed over.
    const newCategoryRef = useRef<HTMLButtonElement | null>(null);
    const drawnOrder = useMemo(
        () => SECTION_KINDS.flatMap((kind) =>
            flattenForest(buildForest(shown.filter((c) => c.categoryKind === kind))).map((r) => r.node.id)),
        [shown],
    );
    const rowControl = useRowRemovalFocus(drawnOrder, newCategoryRef);

    const deleteMut = useMutation({
        mutationFn: (id: string) => deleteCategory(ledgerId, id),
        onSuccess: () => { onSaved(); setDeleteTarget(null); },
    });

    const menuItems = (node: CategoryNode): ContextMenuItem[] => [
        { id: 'open', label: 'Open register',
            onSelect: () => void navigate({
                to: '/ledgers/$ledgerId/accounts/$accountId',
                params: { ledgerId, accountId: node.id },
            }) },
        // A sub-category of a category that cannot be chosen is not worth making.
        ...(node.isActive
            ? [{ id: 'add', label: 'Add sub-category',
                onSelect: () => setDialog({ kind: 'create', presetParentId: node.id }) }]
            : []),
        { id: 'rename', label: 'Rename…',
            onSelect: () => setDialog({ kind: 'rename', node }) },
        { id: 'move', label: 'Move…',
            onSelect: () => setDialog({ kind: 'move', node }) },
        // Enabled by the rule the dialog's targets follow, so the menu never offers a merge
        // that the dialog then says it has nothing to merge into.
        { id: 'merge', label: 'Merge…', disabled: !canMergeAway(node.id, accounts),
            onSelect: () => setDialog({ kind: 'merge', node }) },
        node.isActive
            ? { id: 'deactivate', label: 'Deactivate',
                onSelect: () => { setActive.reset(); setDeactivating(node); } }
            : { id: 'reactivate', label: 'Reactivate',
                onSelect: () => setActive.mutate({ accountId: node.id, active: true }) },
        { id: 'delete', label: 'Delete', danger: true,
            onSelect: () => { deleteMut.reset(); setDeleteTarget(node); } },
    ];

    const renderRow = (row: CategoryTreeNode) => {
        const { node, depth } = row;
        return (
            <StructureRow
                key={node.id}
                depth={depth}
                name={(
                    <Link
                        to="/ledgers/$ledgerId/accounts/$accountId"
                        params={{ ledgerId, accountId: node.id }}
                        title={`Open the ${pathOf(node)} register`}
                        className={rowLinkClass(!node.isActive)}
                    >
                        {node.name}
                    </Link>
                )}
                badges={(
                    <>
                        {!node.isActive ? <StructureBadge>Inactive</StructureBadge> : null}
                        {node.isSystem ? <StructureBadge>System</StructureBadge> : null}
                    </>
                )}
                count={transactionCountLabel(node.transactionCount)}
                // One category's own sum, converted; "no rate" when a leg needs a missing rate.
                amount={ledgerCurrency === null
                    ? null
                    : node.total === null
                        ? <NoRate missing={node.rateUnavailable ?? []} />
                        : formatCurrency(displayTotal(node.total, node.categoryKind), ledgerCurrency.code)}
                // System categories (e.g. Uncategorized) have no user actions — the server rejects
                // mutating them — and nor does any row for a reader who cannot edit.
                menu={!canEdit
                    ? undefined
                    : node.isSystem
                        ? 'none'
                        : { label: `Actions for ${pathOf(node)}`, onOpen: (anchor) => rowMenu.open(node, anchor) }}
                menuControl={rowControl(node.id)}
            />
        );
    };

    const failed = categoriesQuery.isError;
    const loading = categoriesQuery.isPending;
    const sections = [
        { kind: SECTION_KINDS[0], label: 'Income', icon: TrendingUp, iconClass: 'text-state-success' },
        { kind: SECTION_KINDS[1], label: 'Expense', icon: TrendingDown, iconClass: 'text-state-danger' },
        { kind: SECTION_KINDS[2], label: 'Adjustment', icon: Scale, iconClass: 'text-text-muted' },
    ] as const;

    return (
        <div className="space-y-4">
            {/* The one ledger-level note on converted totals (ADR-0114 D7): each
                category's total combines the accounts its lines are paid from. */}
            <LedgerConversionNote ledgerId={ledgerId} />
            <StructureToolbar
                noun="categories"
                query={query}
                onQuery={setQuery}
                showInactive={showInactive}
                onShowInactive={setShowInactive}
                createLabel={canEdit ? 'New category' : undefined}
                onCreate={canEdit ? () => setDialog({ kind: 'create' }) : undefined}
                createRef={newCategoryRef}
                disabled={failed}
                pending={loading}
            />

            {/* Reactivating asks nothing, so it has no dialog to say it failed in. */}
            {setActive.isError && setActive.variables?.active === true ? (
                <ActionError message={errorMessage(setActive.error, 'Could not reactivate the category.')} />
            ) : null}

            {failed ? (
                <ListError
                    message={errorMessage(categoriesQuery.error, 'Could not load categories.')}
                    onRetry={() => void categoriesQuery.refetch()}
                />
            ) : loading ? (
                <ListLoading noun="categories" />
            ) : categories.length === 0 ? (
                <ListEmpty
                    message="No categories yet"
                    hint="Categories say what each transaction was for. Start with a few and add more as you go."
                    action={canEdit ? (
                        <Button type="button" variant="primary" size="sm" onClick={() => setDialog({ kind: 'create' })}>
                            New category
                        </Button>
                    ) : undefined}
                />
            ) : shown.length === 0 ? (
                <ListNoMatches query={query} noun="category" onClear={() => setQuery('')} />
            ) : (
                sections.map((s) => (
                    <KindSection
                        key={s.kind}
                        kind={s.kind}
                        label={s.label}
                        icon={s.icon}
                        iconClass={s.iconClass}
                        categories={shown}
                        // The total is the kind's, over every category of it. A search shows part of
                        // the tree, and a total of part of it would read as the whole.
                        totalOf={visible === null ? categories : null}
                        ledgerCurrency={ledgerCurrency}
                        searching={visible !== null}
                        renderRow={renderRow}
                        ledgerId={ledgerId}
                    />
                ))
            )}

            {rowMenu.menu ? (
                <ContextMenu
                    anchor={rowMenu.menu.anchor}
                    label={`Actions for ${pathOf(rowMenu.menu.row)}`}
                    items={menuItems(rowMenu.menu.row)}
                    onClose={rowMenu.close}
                />
            ) : null}

            {dialog?.kind === 'create' ? (
                <CategoryCreateDialog
                    ledgerId={ledgerId}
                    accounts={accounts}
                    presetKind={dialog.presetKind}
                    presetParentId={dialog.presetParentId}
                    onClose={() => setDialog(null)}
                    onSaved={onSaved}
                />
            ) : null}
            {dialog?.kind === 'rename' ? (
                <CategoryRenameDialog
                    ledgerId={ledgerId}
                    node={dialog.node}
                    onClose={() => setDialog(null)}
                    onSaved={onSaved}
                />
            ) : null}
            {dialog?.kind === 'move' ? (
                <CategoryMoveDialog
                    ledgerId={ledgerId}
                    node={dialog.node}
                    accounts={accounts}
                    onClose={() => setDialog(null)}
                    onSaved={onSaved}
                />
            ) : null}
            {dialog?.kind === 'merge' ? (
                <CategoryMergeDialog
                    ledgerId={ledgerId}
                    node={dialog.node}
                    accounts={accounts}
                    onClose={() => setDialog(null)}
                    onSaved={onSaved}
                />
            ) : null}

            {deactivating !== null ? (
                <DeactivateConfirm
                    kind="category"
                    name={pathOf(deactivating)}
                    isConfirming={setActive.isPending}
                    error={setActive.isError ? errorMessage(setActive.error, 'Could not deactivate the category.') : null}
                    onConfirm={() => setActive.mutate(
                        { accountId: deactivating.id, active: false },
                        { onSuccess: () => setDeactivating(null) },
                    )}
                    onCancel={() => { setActive.reset(); setDeactivating(null); }}
                />
            ) : null}

            {deleteTarget ? (
                <DeleteCategoryConfirm
                    path={pathOf(deleteTarget)}
                    node={deleteTarget}
                    isDeleting={deleteMut.isPending}
                    error={deleteMut.isError
                        ? errorMessage(deleteMut.error, 'Could not delete the category.')
                        : null}
                    onConfirmDelete={() => deleteMut.mutate(deleteTarget.id)}
                    onMergeInstead={() => {
                        const node = deleteTarget;
                        setDeleteTarget(null);
                        setDialog({ kind: 'merge', node });
                    }}
                    onCancel={() => setDeleteTarget(null)}
                />
            ) : null}
        </div>
    );
}

/**
 * One kind's section: a header (disclosure + icon + label + count, then the kind's total) over
 * the tree, which is a panel of rows.
 *
 * COLLAPSIBLE because a real taxonomy is long. The Demo's expense categories alone run to
 * about eighty, so a third section below them sits about 2500px down and
 * reaching it means scrolling past everything — which is what made adding the
 * Adjustment section a nuisance rather than a fix. Collapsing is the general
 * answer: the same scroll is in the way of the Expense total and of anything else below the
 * fold.
 *
 * The toggle is the heading only. The total sits OUTSIDE it, because a button that both
 * reads a figure and collapses the list is a trap.
 *
 * State persists per ledger via {@link usePersistedDisclosure}, the same hook
 * (and the same reasoning) as the hub sections. A kind with nothing to show is left out
 * (there is no create button here to reach an empty one with: New category is the toolbar's,
 * and its dialog asks for the kind), and a search opens every section it found something in.
 */
function KindSection({
    kind, label, icon: Icon, iconClass, categories, totalOf, ledgerCurrency, searching, renderRow, ledgerId,
}: {
    kind: string;
    label: string;
    icon: LucideIcon;
    iconClass: string;
    /** The categories to draw: all of them, or what a search kept. */
    categories: readonly CategoryNode[];
    /** What the section's total is summed over, or null to leave the total off. */
    totalOf: readonly CategoryNode[] | null;
    /** The ledger's reporting currency, every total converted into it; null until the
     *  categories read has answered. */
    ledgerCurrency: { code: string } | null;
    searching: boolean;
    renderRow: (row: CategoryTreeNode) => React.ReactNode;
    ledgerId: string;
}) {
    // COLLAPSED until the user says otherwise. The screen then opens as header rows carrying
    // the totals, and expanding one is a click — rather than opening on a taxonomy long enough
    // that the section you came for is off-screen.
    //
    // Uniform across the three kinds rather than "collapse the big one":
    // whatever row count would justify that rule is a number read off this
    // ledger, and a taxonomy that makes Income the long section is somebody's
    // ordinary ledger.
    const [stored, toggle] = usePersistedDisclosure(
        `coffer.categories.${ledgerId}.${kind}.expanded`, false);
    const expanded = searching || stored;
    const bodyId = `categories-${kind}-body`;
    const rows = useMemo(
        () => flattenForest(buildForest(categories.filter((c) => c.categoryKind === kind))),
        [categories, kind],
    );
    const sectionTotal = useMemo((): { amount: number; missing: RateUnavailable[] } | null => {
        if (totalOf === null || ledgerCurrency === null) return null;
        const ofKind = totalOf.filter((c) => c.categoryKind === kind);
        // In the ledger's reporting currency, every total converted; a category whose
        // total needs a missing rate makes the sum "no rate", never a partial sum
        // (ADR-0114 D9).
        const missing = ofKind.flatMap((c) => (c.total === null ? (c.rateUnavailable ?? []) : []));
        if (ofKind.some((c) => c.total === null)) return { amount: 0, missing };
        const rawSum = ofKind.reduce((sum, c) => sum + c.total!, 0);
        return { amount: displayTotal(rawSum, kind), missing };
    }, [totalOf, ledgerCurrency, kind]);

    if (rows.length === 0) return null;

    return (
        <section className="space-y-1">
            <StructureGroupHeading
                label={label}
                count={rows.length}
                icon={<Icon className={cn('size-icon-md shrink-0', iconClass)} aria-hidden />}
                total={sectionTotal !== null && ledgerCurrency !== null
                    ? sectionTotal.missing.length > 0
                        ? {
                            // The marker's subtle colour, with the missing rates on hover.
                            text: NO_RATE_LABEL,
                            mixed: true,
                            title: rateUnavailableTitle(sectionTotal.missing),
                        }
                        : {
                            text: formatCurrency(sectionTotal.amount, ledgerCurrency.code),
                            title: `Total ${label.toLowerCase()} across all ${label.toLowerCase()} categories`,
                        }
                    : undefined}
                disclosure={{
                    expanded,
                    onToggle: toggle,
                    controls: bodyId,
                    // A search opens the section on its own, so there is nothing to toggle then.
                    disabled: searching,
                }}
            />
            <div id={bodyId} hidden={!expanded}>
                <Panel>
                    <ul className="divide-y divide-border">{rows.map(renderRow)}</ul>
                </Panel>
            </div>
        </section>
    );
}

/** Adaptive delete dialog: a normal danger-confirm when the category is
 *  empty, or an explain-and-redirect-to-merge when it's still in use
 *  (the server blocks the delete; the UI offers the path forward). */
function DeleteCategoryConfirm({
    node, path, isDeleting, error, onConfirmDelete, onMergeInstead, onCancel,
}: {
    node: CategoryNode;
    /** Full Parent/Child path. A modal has no tree around it, and two
     *  same-named leaves under different parents are indistinguishable by
     *  leaf alone — on a DESTRUCTIVE confirm that is the worst place for it. */
    path: string;
    isDeleting: boolean;
    error: string | null;
    onConfirmDelete: () => void;
    onMergeInstead: () => void;
    onCancel: () => void;
}) {
    const inUse = node.transactionCount > 0 || node.childCount > 0;
    const txns = node.transactionCount;
    const kids = node.childCount;
    return (
        <ConfirmDialog
            open
            variant={inUse ? 'neutral' : 'danger'}
            title={inUse ? `Can’t delete “${path}”` : `Delete “${path}”?`}
            confirmLabel={inUse ? 'Merge instead…' : 'Delete'}
            isConfirming={isDeleting}
            body={
                <>
                    {inUse ? (
                        <>
                            It still has{' '}
                            <span className="font-medium text-text">{txns} transaction{txns === 1 ? '' : 's'}</span>
                            {kids > 0 ? (
                                <> and <span className="font-medium text-text">{kids} sub-categor{kids === 1 ? 'y' : 'ies'}</span></>
                            ) : null}
                            , so deleting it would orphan them. Merge it into another category to fold those in.
                        </>
                    ) : (
                        // The server counts transactions and sub-categories before it deletes and not
                        // budget targets, which go with the category, so the line says so.
                        <>
                            This permanently deletes the category. It has no transactions or
                            sub-categories, and any budget targets set on it go with it.
                        </>
                    )}
                    {error !== null ? (
                        <p role="alert" className="mt-2 text-state-danger">{error}</p>
                    ) : null}
                </>
            }
            onConfirm={inUse ? onMergeInstead : onConfirmDelete}
            onCancel={onCancel}
        />
    );
}
