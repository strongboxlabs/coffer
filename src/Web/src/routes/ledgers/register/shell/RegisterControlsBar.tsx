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
    /** Categories for the Category picker. */
    categories: readonly AccountSummary[];
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
    categories,
    tags,
    securities,
    resultCount,
    statusCounts,
    onNew,
    newDisabled,
    newButtonTitle,
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
                    categories={categories}
                    tags={tags}
                    securities={securities}
                />
                <div className="ml-auto shrink-0">
                    <RegisterToolbarContent
                        onNew={onNew}
                        disabled={newDisabled}
                        newButtonTitle={newButtonTitle}
                        showHint={false}
                    />
                </div>
            </div>
            {/* Row 2: active-filter chips (renders null when nothing is active). */}
            <RegisterFilterChips
                filter={filter}
                onChange={onFilterChange}
                categories={categories}
                securities={securities}
                resultCount={resultCount}
            />
        </div>
    );
}
