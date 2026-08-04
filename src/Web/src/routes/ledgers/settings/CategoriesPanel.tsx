import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { Plus, TrendingDown, TrendingUp } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';

import { deleteCategory, fetchAccounts, fetchCategories } from '@/lib/api';
import type { CategoryNode } from '@/lib/types';
import { formatCurrency } from '@/lib/money';
import { errorMessage } from '@/lib/errorMessage';
import { cn } from '@/lib/cn';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ContextMenu, type ContextMenuItem } from '@/components/ui/ContextMenu';

import { buildForest, flattenForest, type CategoryTreeNode } from './categoryTree';
import {
    CategoryCreateDialog,
    CategoryMergeDialog,
    CategoryMoveDialog,
    CategoryRenameDialog,
} from './CategoryDialogs';

/** Normalize a category's raw signed total to a positive-natural display
 *  magnitude: expense nets positive (stays as-is), income nets negative so
 *  earnings read positive under the Income header. */
function displayTotal(total: number, kind: string): number {
    return kind === 'income' ? -total : total;
}

/**
 * Settings → Categories (ADR-0017 / ADR-0068 Slice A). Manage the budget
 * categories that are otherwise only reachable inline in the transaction
 * editor: see the full income/expense hierarchy, create / rename / move /
 * merge / delete, with per-category usage counts. Categories ARE accounts
 * (`account_type='category'`), so this reads the dedicated /categories
 * endpoint (hierarchy + usage) and writes through the accounts endpoints
 * (create/rename) plus the /categories hierarchy ops (move/merge/delete).
 */
export function CategoriesPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const [showInactive, setShowInactive] = useState(false);

    const categoriesQuery = useQuery({
        queryKey: ['categories', ledgerId, showInactive],
        queryFn: () => fetchCategories(ledgerId, { includeInactive: showInactive }),
    });
    // The dialogs' parent / merge-target pickers reuse AccountCategoryPicker,
    // which wants the full ledger account list (it builds parent-name
    // qualifiers + filters via isEligible).
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId],
        queryFn: () => fetchAccounts(ledgerId),
    });
    const categories = useMemo(() => categoriesQuery.data ?? [], [categoriesQuery.data]);
    const accounts = useMemo(() => accountsQuery.data ?? [], [accountsQuery.data]);

    // A category mutation ripples beyond the tree: names/active state feed
    // the register chips + pickers (accounts) and the register itself
    // (['register', …] is the ADR-0079 canonical key that reloads it).
    const onSaved = () => {
        void queryClient.invalidateQueries({ queryKey: ['categories', ledgerId] });
        void queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
        void queryClient.invalidateQueries({ queryKey: ['register', ledgerId] });
    };

    type DialogState =
        | { kind: 'create'; presetKind?: string; presetParentId?: string | null }
        | { kind: 'rename'; node: CategoryNode }
        | { kind: 'move'; node: CategoryNode }
        | { kind: 'merge'; node: CategoryNode };
    const [dialog, setDialog] = useState<DialogState | null>(null);
    const [deleteTarget, setDeleteTarget] = useState<CategoryNode | null>(null);
    const [menu, setMenu] = useState<{ node: CategoryNode; x: number; y: number } | null>(null);

    const deleteMut = useMutation({
        mutationFn: (id: string) => deleteCategory(ledgerId, id),
        onSuccess: () => { onSaved(); setDeleteTarget(null); },
    });

    const menuItems = (node: CategoryNode): ContextMenuItem[] => {
        const hasMergeTarget = categories.some(
            (c) => c.categoryKind === node.categoryKind && c.id !== node.id,
        );
        return [
            { id: 'add', label: 'Add sub-category',
                onSelect: () => setDialog({ kind: 'create', presetParentId: node.id }) },
            { id: 'rename', label: 'Rename',
                onSelect: () => setDialog({ kind: 'rename', node }) },
            { id: 'move', label: 'Move…',
                onSelect: () => setDialog({ kind: 'move', node }) },
            { id: 'merge', label: 'Merge…', disabled: !hasMergeTarget,
                onSelect: () => setDialog({ kind: 'merge', node }) },
            { id: 'delete', label: 'Delete', danger: true,
                onSelect: () => setDeleteTarget(node) },
        ];
    };

    const renderRow = (row: CategoryTreeNode) => {
        const { node, depth } = row;
        // System categories (e.g. Uncategorized) have no user actions — the
        // server rejects mutating them — so they aren't right-clickable.
        const actionable = !node.isSystem;
        return (
            <li
                key={node.id}
                style={{ paddingLeft: `${0.5 + depth * 1.25}rem` }}
                title={actionable ? 'Right-click for actions' : undefined}
                onContextMenu={actionable
                    ? (e) => { e.preventDefault(); setMenu({ node, x: e.clientX, y: e.clientY }); }
                    : undefined}
                className={cn(
                    'flex items-center justify-between gap-2 rounded py-1 pr-1.5 hover:bg-surface-hover',
                    actionable && 'cursor-context-menu',
                )}
            >
                <div className="flex min-w-0 items-center gap-2">
                    <Link
                        to="/ledgers/$ledgerId/accounts/$accountId"
                        params={{ ledgerId, accountId: node.id }}
                        title={`Open the ${node.name} register`}
                        className={cn(
                            'truncate text-sm hover:text-accent hover:underline',
                            !node.isActive && 'text-text-subtle line-through',
                        )}
                    >
                        {node.name}
                    </Link>
                    {!node.isActive ? <Tag>Inactive</Tag> : null}
                    {node.isSystem ? <Tag>System</Tag> : null}
                </div>
                <div className="flex shrink-0 items-center gap-3">
                    <span
                        className="text-[0.6875rem] tabular-nums text-text-subtle"
                        title={`${node.transactionCount} transaction${node.transactionCount === 1 ? '' : 's'}`}
                    >
                        {node.transactionCount} txns
                    </span>
                    <span className="w-24 text-right text-sm tabular-nums text-text">
                        {formatCurrency(displayTotal(node.total, node.categoryKind))}
                    </span>
                </div>
            </li>
        );
    };

    return (
        <Panel>
            <PanelHead>
                <div className="flex w-full items-center justify-between gap-3">
                    <span className="font-medium">Categories</span>
                    <label className="flex items-center gap-1.5 text-xs text-text-muted">
                        <input
                            type="checkbox"
                            checked={showInactive}
                            onChange={(e) => setShowInactive(e.target.checked)}
                        />
                        Show inactive
                    </label>
                </div>
            </PanelHead>
            <PanelBody className="space-y-5">
                {categoriesQuery.isPending ? (
                    <p className="text-sm text-text-muted">Loading categories…</p>
                ) : categoriesQuery.isError ? (
                    <p role="alert" className="text-sm text-state-danger">
                        {errorMessage(categoriesQuery.error, 'Could not load categories.')}
                    </p>
                ) : (
                    <>
                        <KindSection
                            kind="income"
                            label="Income"
                            icon={TrendingUp}
                            iconClass="text-state-success"
                            categories={categories}
                            renderRow={renderRow}
                            onNew={() => setDialog({ kind: 'create', presetKind: 'income' })}
                        />
                        <KindSection
                            kind="expense"
                            label="Expense"
                            icon={TrendingDown}
                            iconClass="text-state-danger"
                            categories={categories}
                            renderRow={renderRow}
                            onNew={() => setDialog({ kind: 'create', presetKind: 'expense' })}
                        />
                    </>
                )}
            </PanelBody>

            {menu ? (
                <ContextMenu
                    anchor={{ x: menu.x, y: menu.y }}
                    items={menuItems(menu.node)}
                    onClose={() => setMenu(null)}
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

            {deleteTarget ? (
                <DeleteCategoryConfirm
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
        </Panel>
    );
}

/** One kind's section: header (icon + label + count + New) over the tree. */
function KindSection({
    kind, label, icon: Icon, iconClass, categories, renderRow, onNew,
}: {
    kind: string;
    label: string;
    icon: LucideIcon;
    iconClass: string;
    categories: readonly CategoryNode[];
    renderRow: (row: CategoryTreeNode) => React.ReactNode;
    onNew: () => void;
}) {
    const { rows, sectionTotal } = useMemo(() => {
        const ofKind = categories.filter((c) => c.categoryKind === kind);
        const rawSum = ofKind.reduce((sum, c) => sum + c.total, 0);
        return {
            rows: flattenForest(buildForest(ofKind)),
            sectionTotal: displayTotal(rawSum, kind),
        };
    }, [categories, kind]);

    return (
        <section>
            <div className="mb-1 flex items-center justify-between gap-2 border-b border-border pb-1">
                <h3 className="flex items-center gap-1.5 text-sm font-semibold text-text">
                    <Icon className={cn('h-4 w-4', iconClass)} aria-hidden />
                    {label}
                    <span className="text-xs font-normal text-text-subtle">({rows.length})</span>
                </h3>
                <div className="flex items-center gap-3">
                    <span
                        className="text-sm font-semibold tabular-nums text-text"
                        title={`Total ${label.toLowerCase()} across all ${label.toLowerCase()} categories`}
                    >
                        {formatCurrency(sectionTotal)}
                    </span>
                    <button
                        type="button"
                        onClick={onNew}
                        className="flex items-center gap-1 rounded px-1.5 py-0.5 text-xs text-text-muted hover:bg-surface-muted hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                    >
                        <Plus className="h-3.5 w-3.5" aria-hidden />
                        New {label.toLowerCase()}
                    </button>
                </div>
            </div>
            {rows.length === 0 ? (
                <p className="px-2 py-1 text-xs text-text-subtle">
                    No {label.toLowerCase()} categories yet.
                </p>
            ) : (
                <ul>{rows.map(renderRow)}</ul>
            )}
        </section>
    );
}

/** Adaptive delete dialog: a normal danger-confirm when the category is
 *  empty, or an explain-and-redirect-to-merge when it's still in use
 *  (the server blocks the delete; the UI offers the path forward). */
function DeleteCategoryConfirm({
    node, isDeleting, error, onConfirmDelete, onMergeInstead, onCancel,
}: {
    node: CategoryNode;
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
            title={inUse ? `Can’t delete “${node.name}”` : `Delete “${node.name}”?`}
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
                        <>This permanently deletes the category. It has no transactions or sub-categories, so nothing else is affected.</>
                    )}
                    {error !== null ? (
                        <p className="mt-2 text-state-danger">{error}</p>
                    ) : null}
                </>
            }
            onConfirm={inUse ? onMergeInstead : onConfirmDelete}
            onCancel={onCancel}
        />
    );
}

/** Tiny inline badge for the Inactive / System row markers. */
function Tag({ children }: { children: React.ReactNode }) {
    return (
        <span className="shrink-0 rounded bg-surface-muted px-1.5 py-0.5 text-[0.625rem] font-medium uppercase tracking-wide text-text-subtle">
            {children}
        </span>
    );
}
