import { useMemo, useRef, useState, type ReactNode } from 'react';
import { useMutation } from '@/lib/mutation';

import {
    ApiError,
    createAccount,
    mergeCategory,
    reparentCategory,
    updateAccount,
} from '@/lib/api';
import type { AccountSummary, CategoryNode, RateUnavailable } from '@/lib/types';
import { ExchangeRatesLink } from '@/components/ConversionNote';
import { monthLabel } from '@/lib/calendar';
import { formatLedgerDateLong } from '@/lib/dates';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Modal } from '@/components/ui/Modal';
import { pendingChoiceProps, pendingTextProps } from '@/components/ui/pending';
import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { errorMessage } from '@/lib/errorMessage';
import { buildAccountPathMap } from '@/lib/accountPath';

import { canMergeAway, collectDescendantIds, eligibleMergeTarget } from './categoryTree';

/**
 * Full `Parent/Child` path for a category, for use in flat prose.
 *
 * A modal has no tree around it. The user may have opened this from a row deep
 * in the hierarchy, and a ledger can have same-named leaves under
 * different parents — so a bare name in a confirmation sentence can name the
 * wrong category convincingly. The manage-categories TREE behind the dialog is
 * the exception and keeps its leaf names: indentation answers it there.
 */
function useCategoryPath(accounts: readonly AccountSummary[]) {
    const paths = useMemo(() => buildAccountPathMap(accounts), [accounts]);
    return (id: string, fallback: string) => paths.get(id) ?? fallback;
}

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

/** Kind values as a person reads them. Falls through to the raw string so a
 *  value from an older or newer build never renders blank. */
const KIND_LABEL: Record<string, string> = {
    income: 'Income',
    expense: 'Expense',
    adjustment: 'Adjustment',
};

// A field whose save is in flight is pending (ADR-0023 §L): the save's success
// closes the dialog, so a change made meanwhile would be dropped unseen, and it
// looks as a disabled field does.
const inputClass =
    'mt-1 w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text ' +
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent ' +
    'disabled:cursor-not-allowed disabled:opacity-50 aria-disabled:cursor-not-allowed aria-disabled:opacity-50';

const checkboxRowClass = 'mt-1 flex items-center gap-2 text-sm text-text';

const checkboxClass = 'disabled:cursor-not-allowed disabled:opacity-50 aria-disabled:cursor-not-allowed aria-disabled:opacity-50';

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
    const categoryPath = useCategoryPath(accounts);
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

    // Enter in the name field presses Create, so the press holds it as a click
    // does; an error stays until its retry's result replaces it (ADR-0023 §L).
    const createRef = useRef<HTMLButtonElement | null>(null);
    function handleSave() {
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
                    {/* Modal focuses the first field, this one. */}
                    <input id="cat-create-name" className={inputClass} value={name}
                        onChange={(e) => setName(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') createRef.current?.click(); }}
                        {...pendingTextProps<HTMLInputElement>(createMut.isPending)} />
                </div>

                <div>
                    <FieldLabel htmlFor="cat-create-kind">Kind</FieldLabel>
                    <select id="cat-create-kind" className={inputClass} value={kind}
                        disabled={parentLocked}
                        title={parentLocked ? 'A sub-category inherits its parent’s kind' : undefined}
                        {...pendingChoiceProps<HTMLSelectElement>(createMut.isPending, {
                            onChange: (e) => { setKind(e.target.value); setParentId(null); },
                        })}>
                        <option value="expense">Expense</option>
                        <option value="income">Income</option>
                        {/* mig 224 / ADR-0017. A valuation adjustment — marking a
                            house up, reconciling a retirement balance — is neither
                            income nor expense, and booking it as an expense made it
                            read as NEGATIVE SPENDING. It falls out of all three
                            reporting measures by construction. */}
                        <option value="adjustment">Adjustment</option>
                    </select>
                </div>

                <div>
                    <FieldLabel>Parent</FieldLabel>
                    {parentLocked ? (
                        <p className="mt-1 rounded-md border border-border bg-surface-muted/40 px-2 py-1.5 text-sm text-text-muted">
                            {lockedParent !== null
                                ? categoryPath(lockedParent.id, lockedParent.name)
                                : 'Selected category'}
                        </p>
                    ) : (
                        <>
                            <label className={checkboxRowClass}>
                                <input type="checkbox" checked={topLevel} className={checkboxClass}
                                    {...pendingChoiceProps<HTMLInputElement>(createMut.isPending, {
                                        onChange: (e) => setTopLevel(e.target.checked),
                                    })} />
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
                                    pending={createMut.isPending}
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
                        pending={createMut.isPending}>
                        Cancel
                    </Button>
                    <Button ref={createRef} type="button" variant="primary" size="sm" onClick={handleSave}
                        pending={createMut.isPending} pendingLabel="Creating…">
                        Create
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
    // Enter in the name field presses Save, so the press holds it as a click
    // does; an error stays until its retry's result replaces it (ADR-0023 §L).
    const saveRef = useRef<HTMLButtonElement | null>(null);
    function handleSave() {
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
                    {/* Modal focuses the first field, this one. */}
                    <input id="cat-rename-name" className={inputClass} value={name}
                        onChange={(e) => setName(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') saveRef.current?.click(); }}
                        {...pendingTextProps<HTMLInputElement>(renameMut.isPending)} />
                </div>
                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}
                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        pending={renameMut.isPending}>
                        Cancel
                    </Button>
                    <Button ref={saveRef} type="button" variant="primary" size="sm" onClick={handleSave}
                        pending={renameMut.isPending} pendingLabel="Saving…">
                        Save
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
    const categoryPath = useCategoryPath(accounts);
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

    // An error stays until its retry's result replaces it (ADR-0023 §L).
    function handleSave() {
        if (needsPick) { setError('Pick a parent category, or choose Top level.'); return; }
        moveMut.mutate();
    }

    return (
        <Modal open onClose={onClose} titleId="cat-move-title" className="max-w-sm">
            <div className="flex flex-col gap-3 p-5">
                <h2 id="cat-move-title" className="text-base font-semibold text-text">
                    Move “{categoryPath(node.id, node.name)}”
                </h2>
                <p className="text-xs text-text-muted">
                    Choose a new parent, or move it to the top level.
                </p>
                <div>
                    <FieldLabel>New parent</FieldLabel>
                    <label className={checkboxRowClass}>
                        <input type="checkbox" checked={topLevel} className={checkboxClass}
                            {...pendingChoiceProps<HTMLInputElement>(moveMut.isPending, {
                                onChange: (e) => setTopLevel(e.target.checked),
                            })} />
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
                            pending={moveMut.isPending}
                        />
                    </div>
                </div>
                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}
                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        pending={moveMut.isPending}>
                        Cancel
                    </Button>
                    <Button type="button" variant="primary" size="sm" onClick={handleSave}
                        disabled={unchanged} pending={moveMut.isPending} pendingLabel="Moving…">
                        Move
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

/**
 * A merge refused for a missing rate (ADR-0114 D12): both categories have a target for
 * the month in different currencies, and the rate that would add them is missing. In
 * words, naming the month and the pair, and where to add the rate.
 */
function rateRefusal(missing: RateUnavailable, ledgerId: string): ReactNode {
    const [y, m] = missing.date.split('-').map(Number);
    return (
        <>
            Nothing was merged. Both categories have a budget target for {monthLabel(y!, m!)}, one
            in {missing.fromCurrency} and one in {missing.toCurrency}, and there is
            no {missing.fromCurrency} to {missing.toCurrency} rate for {formatLedgerDateLong(missing.date)} to
            add them with. Add that rate, then merge again.{' '}
            <ExchangeRatesLink ledgerId={ledgerId} />
        </>
    );
}

export function CategoryMergeDialog({ ledgerId, node, accounts, onClose, onSaved }: CategoryMergeDialogProps) {
    const categoryPath = useCategoryPath(accounts);
    const [targetId, setTargetId] = useState<string | null>(null);
    const [error, setError] = useState<ReactNode>(null);

    const excluded = useMemo(() => {
        const set = collectDescendantIds(node.id, accounts);
        set.add(node.id);
        return set;
    }, [node.id, accounts]);
    const isEligible = useMemo(() => eligibleMergeTarget(excluded), [excluded]);
    // Asked of the same function the row menu asks, so the two cannot disagree about whether
    // there is anywhere to merge to.
    const hasTarget = useMemo(() => canMergeAway(node.id, accounts), [node.id, accounts]);
    const target = targetId !== null ? accounts.find((a) => a.id === targetId) ?? null : null;
    const kindChanges = target !== null && target.categoryKind !== node.categoryKind;

    const mergeMut = useMutation({
        mutationFn: () => mergeCategory(ledgerId, node.id, { targetId: targetId ?? '' }),
        onSuccess: () => { onSaved(); onClose(); },
        onError: (e) => setError(e instanceof ApiError && e.code === 'rate-unavailable' && e.rateUnavailable !== undefined
            ? rateRefusal(e.rateUnavailable, ledgerId)
            : errorMessage(e, 'Could not merge the category.')),
    });

    const targetName = targetId !== null
        ? (() => {
            const a = accounts.find((x) => x.id === targetId);
            return a !== undefined ? categoryPath(a.id, a.name) : null;
        })()
        : null;
    const txns = node.transactionCount;
    const kids = node.childCount;

    return (
        <Modal open onClose={onClose} titleId="cat-merge-title" className="max-w-sm">
            <div className="flex flex-col gap-3 p-5">
                <h2 id="cat-merge-title" className="text-base font-semibold text-text">
                    Merge “{categoryPath(node.id, node.name)}”
                </h2>

                {!hasTarget ? (
                    <p className="text-sm text-text-muted">
                        There’s no other category to merge into. Create one first, or
                        move this category instead.
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
                                    pending={mergeMut.isPending}
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
                                deactivates “{categoryPath(node.id, node.name)}”. Reactivating it later
                                makes it selectable again, but it comes back empty: nothing moves back.
                            </p>
                        ) : null}
                        {targetName !== null ? (
                            // The merge moves a number somebody typed (ADR-0099), so it says so before it
                            // does: summed where both have a target for a month, and a target dropped
                            // where it would sit under another for that month, because a category and
                            // what is beneath it cannot both carry one.
                            <p className="rounded border border-border bg-surface-muted/40 p-2 text-xs leading-relaxed text-text-muted">
                                Budget targets move too. Where both categories have one for the same
                                month they are added together. A category and what is beneath it cannot
                                both have a target for a month, so where the merge would cause that, the
                                target on the lower category is dropped.
                            </p>
                        ) : null}
                        {kindChanges && target !== null ? (
                            <p className="rounded border border-state-warning/40 bg-state-warning-soft p-2 text-xs leading-relaxed text-text">
                                These are different kinds. Those{' '}
                                <span className="font-medium">{txns} transaction{txns === 1 ? '' : 's'}</span>{' '}
                                move from{' '}
                                <span className="font-medium">{KIND_LABEL[node.categoryKind] ?? node.categoryKind}</span>{' '}
                                to{' '}
                                <span className="font-medium">{KIND_LABEL[target.categoryKind ?? ''] ?? target.categoryKind}</span>,
                                which changes the totals they count towards — for their whole history,
                                not just this month. This is the intended way to reclassify a category;
                                it is only worth being sure it is what you meant.
                            </p>
                        ) : null}
                    </>
                )}

                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}

                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        pending={mergeMut.isPending}>
                        Cancel
                    </Button>
                    <Button type="button" variant="primary" size="sm" onClick={() => mergeMut.mutate()}
                        disabled={targetId === null} pending={mergeMut.isPending} pendingLabel="Merging…">
                        Merge
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
