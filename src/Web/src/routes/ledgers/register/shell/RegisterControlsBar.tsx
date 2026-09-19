import { Checkbox } from '@/components/ui/Checkbox';

import { RegisterFilterChips } from './RegisterFilterChips';
import { RegisterFilterControls } from './RegisterFilterControls';
import { RegisterSortMenu } from './RegisterSortMenu';
import { RegisterStatusMenu } from './RegisterStatusMenu';
import { RegisterToolbarContent } from './RegisterToolbar';
import type { RegisterSortState } from './registerSort';
import type { StatusFilter } from './registerStatus';
import type { RegisterFilterArgs, RegisterStatusCounts } from '@/lib/api/register';
import type { AccountSummary, SecuritySummary, TagDto } from '@/lib/types';

// Combined register controls (ADR-0030 reuse; redesign Option A). One dense
// control row + a chips row that appears only when a filter is active:
//
//   Row 1: [ Show: All ▾ ]  [ Search… ] [ Filter ▾ ] ……… [ + New transaction ]
//   Row 2 (only when filtering): 56 matches  "food" ✕  Category: Groceries ✕  Clear all
//
// The status views collapse into the compact "Show ▾" dropdown (a scope
// selector — one active at a time) instead of a sprawling pill strip; search is
// a compact box, not full-width; the New-transaction action sits on the right.
// Both registers (bank + investment) pass this as RegisterShell's `toolbar`
// prop, so the controls are identical and can't drift.

export interface RegisterControlsBarProps {
    /** Active status-filter view. */
    statusFilter: StatusFilter;
    /** Status-view change handler. */
    onStatusFilterChange: (next: StatusFilter) => void;
    /** Column sort (mig 166). */
    sort: RegisterSortState;
    /** Sort change handler. */
    onSortChange: (next: RegisterSortState) => void;
    /** Investment register → offer the investment-only sort columns. */
    isInvestment: boolean;
    /** The structured/search filter (mig 164). */
    filter: RegisterFilterArgs;
    /** Filter change handler. */
    onFilterChange: (next: RegisterFilterArgs) => void;
    /** EVERY account in the ledger — the counterparty picker and the chips both
     *  apply their own eligibility and need the rest for path building. */
    accounts: readonly AccountSummary[];
    /** What the other side of a posting is on this register; see
     *  {@link RegisterFilterControls}. A CATEGORY register faces money
     *  accounts, everything else faces categories. */
    counterpartyKind?: 'category' | 'account';
    /** Ledger tags for the Tag filter's autocomplete. */
    tags: readonly TagDto[];
    /** Securities for the Security picker — omit on bank registers. */
    securities?: readonly SecuritySummary[];
    /** Total matches when a filter is active; null hides the count. */
    resultCount: number | null;
    /** Per-status counts for the Show dropdown's badges (mig 165). */
    statusCounts: RegisterStatusCounts | null;
    /** Open the new-transaction editor. */
    onNew: () => void;
    /**
     * Hide the New button outright, rather than disabling it.
     *
     * A CATEGORY register has one: a transaction is created against a money
     * account and categorised, never authored "inside" a category, so the
     * button offers something the editor cannot do. Disabled would imply a
     * state in which it becomes available; there is none.
     */
    hideNew?: boolean;
    /**
     * Offer the sub-category scope toggle — a parent CATEGORY register only.
     *
     * It is a persistent CHECKBOX rather than a one-shot button, because the
     * state it carries has to be visible and reversible from anywhere in the
     * register. The first version was a button in the empty state alone: it
     * vanished the moment it worked, leaving rows from several sub-categories
     * and nothing on screen saying why — and no way back except the browser's
     * Back button.
     */
    showSubcategoryScope?: boolean;
    /** Disable the New button (e.g. while an editor is already open). */
    newDisabled: boolean;
    /** Title/tooltip for the New button. Defaults to the bank copy. */
    newButtonTitle?: string;
}

export function RegisterControlsBar({
    statusFilter,
    onStatusFilterChange,
    sort,
    onSortChange,
    isInvestment,
    filter,
    onFilterChange,
    accounts,
    counterpartyKind,
    tags,
    securities,
    resultCount,
    statusCounts,
    onNew,
    newDisabled,
    newButtonTitle,
    hideNew = false,
    showSubcategoryScope = false,
}: RegisterControlsBarProps) {
    return (
        <div className="flex flex-col gap-1.5 border-b border-border bg-surface px-3 py-1.5">
            {/* Row 1: status view + search/filter (left), New action (right). */}
            <div className="flex items-center gap-2">
                <RegisterStatusMenu
                    statusFilter={statusFilter}
                    onChange={onStatusFilterChange}
                    counts={statusCounts}
                />
                <RegisterSortMenu
                    sort={sort}
                    onChange={onSortChange}
                    investment={isInvestment}
                />
                <RegisterFilterControls
                    filter={filter}
                    onChange={onFilterChange}
                    accounts={accounts}
                    counterpartyKind={counterpartyKind}
                    tags={tags}
                    securities={securities}
                />
                {/* Grouped with the other scope controls on the left, not
                    stranded in the gap the hidden New button leaves on the
                    right — it answers "which rows", like Show and Filter. */}
                {showSubcategoryScope ? (
                    <Checkbox
                        checked={filter.includeSubcategories === true}
                        onChange={(e) =>
                            onFilterChange({
                                ...filter,
                                includeSubcategories: e.target.checked,
                            })}
                        label="Include sub-categories"
                        title="Show entries filed under this category's descendants as well as its own"
                        className="size-icon-xs cursor-pointer"
                        wrapperClassName="shrink-0 cursor-pointer gap-1.5 text-[0.6875rem] text-text-subtle hover:text-text"
                    />
                ) : null}
                <div className="ml-auto shrink-0">
                    {hideNew ? null : (
                    <RegisterToolbarContent
                        onNew={onNew}
                        disabled={newDisabled}
                        newButtonTitle={newButtonTitle}
                        showHint={false}
                    />
                    )}
                </div>
            </div>
            {/* Row 2: active-filter chips (renders null when nothing is active). */}
            <RegisterFilterChips
                filter={filter}
                onChange={onFilterChange}
                accounts={accounts}
                counterpartyKind={counterpartyKind}
                securities={securities}
                resultCount={resultCount}
            />
        </div>
    );
}
