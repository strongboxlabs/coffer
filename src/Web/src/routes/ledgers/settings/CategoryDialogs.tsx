import { useMemo, useState } from 'react';
import { useMutation } from '@tanstack/react-query';

import {
    createAccount,
    mergeCategory,
    reparentCategory,
    updateAccount,
} from '@/lib/api';
import type { AccountSummary, CategoryNode } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Modal } from '@/components/ui/Modal';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { errorMessage } from '@/lib/errorMessage';

import { collectDescendantIds } from './categoryTree';

// Manage-categories action dialogs (Slice A). Categories ARE accounts,
// so create/rename go through the accounts endpoints (createAccount /
// updateAccount) while the hierarchy ops (move = reparent, merge) use
// the dedicated /categories endpoints. Parent / target selection reuses
// the SAME AccountCategoryPicker the transaction + loan editors use
// (ADR-0043) so the type-ahead behaves identically everywhere — no
// parallel picker. Each dialog owns its mutation and calls `onSaved` on
// success; the host panel owns cache invalidation. Form idiom mirrors
// AccountEditorDialog (ADR-0023): labels above inputs, inline error,
// [Cancel] [confirm] footer.

const inputClass =
    'mt-1 w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text ' +
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

const checkboxRowClass = 'mt-1 flex items-center gap-2 text-sm text-text';

/** Eligibility for a parent / merge target: an active category of the
 *  given kind, excluding the supplied ids (the node + its own subtree, so
 *  a move / merge can't form a cycle — the server enforces this too). */
function eligibleCategory(
    kind: string,
    excludeIds: ReadonlySet<string>,
): (a: AccountSummary) => boolean {
    return (a) =>
        a.accountType === 'category'
        && a.categoryKind === kind
        && a.isActive
        && !excludeIds.has(a.id);
}

// ---------------------------------------------------------------------------
// Create
// ---------------------------------------------------------------------------

export interface CategoryCreateDialogProps {
    ledgerId: string;
    /** Full ledger account list — feeds the parent picker (ADR-0043). */
    accounts: readonly AccountSummary[];
    /** Seed the kind (e.g. the section the user clicked "New" in). */
    presetKind?: string;
    /** Seed + lock the parent (e.g. "Add sub-category" on a row). */
    presetParentId?: string | null;
    onClose: () => void;
    onSaved: () => void;
}

export function CategoryCreateDialog({
    ledgerId, accounts, presetKind, presetParentId, onClose, onSaved,
}: CategoryCreateDialogProps) {
    const parentLocked = presetParentId != null && presetParentId !== '';
    const lockedParent = parentLocked
        ? accounts.find((a) => a.id === presetParentId) ?? null
        : null;

    const [name, setName] = useState('');
    const [kind, setKind] = useState(lockedParent?.categoryKind ?? presetKind ?? 'expense');
    const [topLevel, setTopLevel] = useState(!parentLocked);
    const [parentId, setParentId] = useState<string | null>(presetParentId ?? null);
    const [error, setError] = useState<string | null>(null);

    const isEligible = eligibleCategory(kind, new Set());

    const createMut = useMutation({
        mutationFn: () =>
            createAccount(ledgerId, {
                name: name.trim(),
                accountType: 'category',
                categoryKind: kind,
                parentId: parentLocked ? presetParentId : (topLevel ? null : parentId),
                openingBalance: 0,
            }),
        onSuccess: () => { onSaved(); onClose(); },
        onError: (e) => setError(errorMessage(e, 'Could not create the category.')),
    });

    function handleSave() {
        setError(null);
        if (name.trim() === '') { setError('Name is required.'); return; }
        if (!parentLocked && !topLevel && parentId === null) {
            setError('Pick a parent category, or choose Top level.'); return;
        }
        createMut.mutate();
    }

    return (
        <Modal open onClose={onClose} titleId="cat-create-title" className="max-w-sm">
            <div className="flex flex-col gap-3 p-5">
                <h2 id="cat-create-title" className="text-base font-semibold text-text">
                    New category
                </h2>

                <div>
                    <FieldLabel htmlFor="cat-create-name">Name</FieldLabel>
                    <input id="cat-create-name" className={inputClass} value={name} autoFocus
                        onChange={(e) => setName(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') handleSave(); }} />
                </div>

                <div>
                    <FieldLabel htmlFor="cat-create-kind">Kind</FieldLabel>
                    <select id="cat-create-kind" className={inputClass} value={kind}
                        disabled={parentLocked}
                        title={parentLocked ? 'A sub-category inherits its parent’s kind' : undefined}
                        onChange={(e) => { setKind(e.target.value); setParentId(null); }}>
                        <option value="expense">Expense</option>
                        <option value="income">Income</option>
                    </select>
                </div>

                <div>
                    <FieldLabel>Parent</FieldLabel>
                    {parentLocked ? (
                        <p className="mt-1 rounded-md border border-border bg-surface-muted/40 px-2 py-1.5 text-sm text-text-muted">
                            {lockedParent?.name ?? 'Selected category'}
                        </p>
                    ) : (
                        <>
                            <label className={checkboxRowClass}>
                                <input type="checkbox" checked={topLevel}
                                    onChange={(e) => setTopLevel(e.target.checked)} />
                                Top level (no parent)
                            </label>
                            <div className="mt-1">
                                <AccountCategoryPicker
                                    accounts={accounts}
                                    isEligible={isEligible}
                                    valueId={topLevel ? null : parentId}
                                    onChangeId={setParentId}
                                    placeholder="Pick a parent category…"
                                    disabled={topLevel}
                                />
                            </div>
                        </>
                    )}
                </div>

                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}

                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        disabled={createMut.isPending}>
                        Cancel
                    </Button>
                    <Button type="button" variant="primary" size="sm" onClick={handleSave}
                        disabled={createMut.isPending}>
                        {createMut.isPending ? 'Creating…' : 'Create'}
                    </Button>
                </div>
            </div>
        </Modal>
    );
}

// ---------------------------------------------------------------------------
// Rename
// ---------------------------------------------------------------------------

export interface CategoryRenameDialogProps {
    ledgerId: string;
    node: CategoryNode;
    onClose: () => void;
    onSaved: () => void;
}

export function CategoryRenameDialog({ ledgerId, node, onClose, onSaved }: CategoryRenameDialogProps) {
    const [name, setName] = useState(node.name);
    const [error, setError] = useState<string | null>(null);

    const renameMut = useMutation({
        mutationFn: () => updateAccount(ledgerId, node.id, { name: name.trim() }),
        onSuccess: () => { onSaved(); onClose(); },
        onError: (e) => setError(errorMessage(e, 'Could not rename the category.')),
    });

    const trimmed = name.trim();
    function handleSave() {
        setError(null);
        if (trimmed === '') { setError('Name is required.'); return; }
        if (trimmed === node.name) { onClose(); return; }
        renameMut.mutate();
    }

    return (
        <Modal open onClose={onClose} titleId="cat-rename-title" className="max-w-sm">
            <div className="flex flex-col gap-3 p-5">
                <h2 id="cat-rename-title" className="text-base font-semibold text-text">
                    Rename category
                </h2>
                <div>
                    <FieldLabel htmlFor="cat-rename-name">Name</FieldLabel>
                    <input id="cat-rename-name" className={inputClass} value={name} autoFocus
                        onChange={(e) => setName(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') handleSave(); }} />
                </div>
                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}
                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        disabled={renameMut.isPending}>
                        Cancel
                    </Button>
                    <Button type="button" variant="primary" size="sm" onClick={handleSave}
                        disabled={renameMut.isPending}>
                        {renameMut.isPending ? 'Saving…' : 'Save'}
                    </Button>
                </div>
            </div>
        </Modal>
    );
}

// ---------------------------------------------------------------------------
// Move (reparent)
// ---------------------------------------------------------------------------

export interface CategoryMoveDialogProps {
    ledgerId: string;
    node: CategoryNode;
    accounts: readonly AccountSummary[];
    onClose: () => void;
    onSaved: () => void;
}

export function CategoryMoveDialog({ ledgerId, node, accounts, onClose, onSaved }: CategoryMoveDialogProps) {
    // Default to picking a parent (picker active) — the dialog's whole job is
    // to choose a destination, so the target picker is always visible; "Top
    // level" is the explicit opt-out.
    const [topLevel, setTopLevel] = useState(false);
    const [parentId, setParentId] = useState<string | null>(node.parentId);
    const [error, setError] = useState<string | null>(null);

    // Exclude self + subtree (a move under a descendant would form a cycle).
    const excluded = useMemo(() => {
        const set = collectDescendantIds(node.id, accounts);
        set.add(node.id);
        return set;
    }, [node.id, accounts]);
    const isEligible = useMemo(
        () => eligibleCategory(node.categoryKind, excluded),
        [node.categoryKind, excluded],
    );

    const moveMut = useMutation({
        mutationFn: () =>
            reparentCategory(ledgerId, node.id, { parentId: topLevel ? null : parentId }),
        onSuccess: () => { onSaved(); onClose(); },
        onError: (e) => setError(errorMessage(e, 'Could not move the category.')),
    });

    const needsPick = !topLevel && parentId === null;
    const unchanged = (topLevel ? null : parentId) === node.parentId;

    function handleSave() {
        setError(null);
        if (needsPick) { setError('Pick a parent category, or choose Top level.'); return; }
        moveMut.mutate();
    }

    return (
        <Modal open onClose={onClose} titleId="cat-move-title" className="max-w-sm">
            <div className="flex flex-col gap-3 p-5">
                <h2 id="cat-move-title" className="text-base font-semibold text-text">
                    Move “{node.name}”
                </h2>
                <p className="text-xs text-text-muted">
                    Choose a new parent, or move it to the top level.
                </p>
                <div>
                    <FieldLabel>New parent</FieldLabel>
                    <label className={checkboxRowClass}>
                        <input type="checkbox" checked={topLevel}
                            onChange={(e) => setTopLevel(e.target.checked)} />
                        Top level (no parent)
                    </label>
                    <div className="mt-1">
                        <AccountCategoryPicker
                            accounts={accounts}
                            isEligible={isEligible}
                            valueId={topLevel ? null : parentId}
                            onChangeId={setParentId}
                            placeholder="Pick a parent category…"
                            disabled={topLevel}
                        />
                    </div>
                </div>
                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}
                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        disabled={moveMut.isPending}>
                        Cancel
                    </Button>
                    <Button type="button" variant="primary" size="sm" onClick={handleSave}
                        disabled={moveMut.isPending || unchanged}>
                        {moveMut.isPending ? 'Moving…' : 'Move'}
                    </Button>
                </div>
            </div>
        </Modal>
    );
}

// ---------------------------------------------------------------------------
// Merge
// ---------------------------------------------------------------------------

export interface CategoryMergeDialogProps {
    ledgerId: string;
    node: CategoryNode;
    accounts: readonly AccountSummary[];
    onClose: () => void;
    onSaved: () => void;
}

export function CategoryMergeDialog({ ledgerId, node, accounts, onClose, onSaved }: CategoryMergeDialogProps) {
    const [targetId, setTargetId] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);

    const excluded = useMemo(() => {
        const set = collectDescendantIds(node.id, accounts);
        set.add(node.id);
        return set;
    }, [node.id, accounts]);
    const isEligible = useMemo(
        () => eligibleCategory(node.categoryKind, excluded),
        [node.categoryKind, excluded],
    );
    const hasTarget = useMemo(() => accounts.some(isEligible), [accounts, isEligible]);

    const mergeMut = useMutation({
        mutationFn: () => mergeCategory(ledgerId, node.id, { targetId: targetId ?? '' }),
        onSuccess: () => { onSaved(); onClose(); },
        onError: (e) => setError(errorMessage(e, 'Could not merge the category.')),
    });

    const targetName = targetId !== null
        ? accounts.find((a) => a.id === targetId)?.name ?? null
        : null;
    const txns = node.transactionCount;
    const kids = node.childCount;

    return (
        <Modal open onClose={onClose} titleId="cat-merge-title" className="max-w-sm">
            <div className="flex flex-col gap-3 p-5">
                <h2 id="cat-merge-title" className="text-base font-semibold text-text">
                    Merge “{node.name}”
                </h2>

                {!hasTarget ? (
                    <p className="text-sm text-text-muted">
                        There’s no other {node.categoryKind} category to merge into. Create one
                        first, or move this category instead.
                    </p>
                ) : (
                    <>
                        <div>
                            <FieldLabel>Merge into</FieldLabel>
                            <div className="mt-1">
                                <AccountCategoryPicker
                                    accounts={accounts}
                                    isEligible={isEligible}
                                    valueId={targetId}
                                    onChangeId={setTargetId}
                                    placeholder="Pick a category…"
                                />
                            </div>
                        </div>
                        {targetName !== null ? (
                            <p className="rounded border border-border bg-surface-muted/40 p-2 text-xs leading-relaxed text-text-muted">
                                Moves{' '}
                                <span className="font-medium text-text">{txns} transaction{txns === 1 ? '' : 's'}</span>
                                {kids > 0 ? (
                                    <> and <span className="font-medium text-text">{kids} sub-categor{kids === 1 ? 'y' : 'ies'}</span></>
                                ) : null}{' '}
                                into <span className="font-medium text-text">{targetName}</span>, then
                                deactivates “{node.name}”. This can be undone by reactivating it.
                            </p>
                        ) : null}
                    </>
                )}

                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}

                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        disabled={mergeMut.isPending}>
                        Cancel
                    </Button>
                    <Button type="button" variant="primary" size="sm" onClick={() => mergeMut.mutate()}
                        disabled={mergeMut.isPending || targetId === null}>
                        {mergeMut.isPending ? 'Merging…' : 'Merge'}
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
