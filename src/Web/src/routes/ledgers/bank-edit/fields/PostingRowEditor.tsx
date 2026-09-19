/**
 * One leg row of a split transaction, on the splits grid's own five columns:
 * `# · Category · Tags · Memo · Amount · actions`.
 *
 * WHY ITS OWN GRID AND NOT THE REGISTER'S EIGHT TRACKS. Leg rows used to ride
 * the register template so each field sat under its root counterpart. That
 * bought vertical alignment with the rows above and paid for it three times
 * over: columns 1-3 were always empty (three tracks of dead width on every leg
 * of every split), the category and memo had to stack inside ONE track to fit,
 * and a leg row was therefore ~62px tall. Twenty-five of those is 1,550px of
 * editor — a form whose Save button you cannot see from the row you are
 * editing. On its own grid a leg row is 30px and the fields sit side by side.
 * The root row still rides the register template, so the transaction's own
 * Date / Payee / Amount stay aligned with the register; it is only the legs,
 * which have no register counterpart, that step out of it.
 *
 * Holds NO draft state: the shell owns the postings array and passes one
 * posting down with an `onChange` that merges a partial back. The local state
 * is presentational only.
 *
 * THREE PROPS THAT LOOK INCIDENTAL AND ARE NOT:
 *
 *   `isEligibleCounterparty` must keep a STABLE IDENTITY across renders.
 *   AccountCategoryPicker holds it in a useMemo dep that chains into a
 *   scroll-highlight effect, so a new function per render re-runs that effect on
 *   every keystroke anywhere in the form and fights the user's own scrolling
 *   while the popover is open. That is behaviour, not performance.
 *
 *   `autoFocusAmount` is how a newly added row's focus lands. The draft hook
 *   mints the key and sets the focus marker in the SAME synchronous statement,
 *   so both setState calls land in one React batch; any deferral renders this
 *   row once with autoFocusAmount=false and the focus effect below never fires.
 *
 *   `fieldId` makes each input addressable by posting key and defect kind, so
 *   the summary strip below the grid can send focus to the exact field that is
 *   wrong. Keyed on the draft key rather than the row index, because a reorder
 *   renumbers rows and an index-derived id would then point at the wrong one.
 */

import { useEffect, useRef, useState } from 'react';

import { AccountCategoryPicker } from '@/components/register/AccountCategoryPicker';
import { ContextMenu, type ContextMenuItem } from '@/components/ui/ContextMenu';
import { RowActionsButton } from '@/components/ui/RowActionsButton';
import { cn } from '@/lib/cn';
import type { AccountSummary, FrequentCounterpartiesResponse } from '@/lib/types';

import type { PostingDraft } from '../postingDraft';
import type { PostingIssue, PostingIssueKind } from '../validation';
import { TagsPlaceholder } from './TagsPlaceholder';

interface PostingRowEditorProps {
    posting: PostingDraft;
    /** 0-based position; the row prints `index + 1`. */
    index: number;
    /** How many postings there are — bounds the move actions. */
    count: number;
    /** This row's defects, from `postingIssues`. Empty on a valid row. */
    issues: readonly PostingIssue[];
    /** Full ledger accounts (for the picker's path/display) + the
     *  eligibility predicate + the source account's frequents —
     *  ADR-0043, same shared picker as the investment editor. */
    accounts: readonly AccountSummary[];
    isEligibleCounterparty: (a: AccountSummary) => boolean;
    frequent: FrequentCounterpartiesResponse | null;
    /** The splits grid's column template — shared with the header and
     *  footer strips so all three line up. */
    cols: string;
    disabled: boolean;
    /** True for the most-recently-added posting; the amount input
     *  focuses itself on mount so the user can start typing
     *  immediately after clicking Add split. */
    autoFocusAmount: boolean;
    /** Called once after autoFocusAmount-driven focus lands, so
     *  the parent can clear its focusKey marker. */
    onAutoFocused: () => void;
    /** DOM id for one of this row's fields — see the header note. */
    fieldId: (kind: PostingIssueKind) => string;
    /** This row is the one currently being dragged. */
    dragging: boolean;
    /** Fired when this row's category panel opens, so the grid can bring the
     *  row to the top of its scrollport and give the panel room. */
    onPickerOpen: () => void;
    onChange: (fields: Partial<PostingDraft>) => void;
    onRemove: () => void;
    /** Nudge this row one slot. The parent bounds-checks too. */
    onMove: (delta: -1 | 1) => void;
    onDragStart: () => void;
    onDragEnd: () => void;
    /** Fired continuously while a drag hovers this row. */
    onDragOver: () => void;
    onDrop: () => void;
}

export function PostingRowEditor({
    posting,
    index,
    count,
    issues,
    accounts,
    isEligibleCounterparty,
    frequent,
    cols,
    disabled,
    autoFocusAmount,
    onAutoFocused,
    fieldId,
    dragging,
    onPickerOpen,
    onChange,
    onRemove,
    onMove,
    onDragStart,
    onDragEnd,
    onDragOver,
    onDrop,
}: PostingRowEditorProps) {
    const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);
    const amountRef = useRef<HTMLInputElement | null>(null);
    useEffect(() => {
        if (autoFocusAmount && amountRef.current) {
            amountRef.current.focus();
            onAutoFocused();
        }
    }, [autoFocusAmount, onAutoFocused]);

    const n = index + 1;
    const canRemove = count > 1;
    const badAmount = issues.some((i) => i.kind === 'amount');
    const badCounterparty = issues.some((i) => i.kind === 'counterparty');
    const bad = issues.length > 0;

    const amount = Number(posting.amount);
    /**
     * The BANK register's convention: a debit is red, a credit is plain text
     * (bankRowStrategy.tsx — the main row, the split parent and the leg row all
     * agree). The investment register uses the opposite pairing, plain for
     * debits and green for credits, and each register is internally consistent.
     *
     * This editor first followed the INVESTMENT convention, on the reasoning
     * that red is also the error colour in this grid so a split of ordinary
     * expenses would read as twenty-five errors. That was wrong twice over: it
     * is the BANK editor, so it sits inside and visually replaces bank register
     * rows — every leg of a paycheck was red in the register and plain the
     * instant you opened it, which is the same number contradicting itself. And
     * the collision it was avoiding does not really exist: a defect here is
     * signalled by a tinted row, an inset bar, a red field BORDER, a red row
     * number and a marker icon, none of which is a figure's text colour.
     */
    const amountTone =
        posting.amount.trim().length === 0 || Number.isNaN(amount) || amount >= 0
            ? 'text-text'
            : 'text-state-danger';

    const menuItems: readonly ContextMenuItem[] = [
        {
            id: 'up',
            label: 'Move up',
            shortcutHint: 'Alt+↑',
            disabled: index === 0,
            onSelect: () => onMove(-1),
        },
        {
            id: 'down',
            label: 'Move down',
            shortcutHint: 'Alt+↓',
            disabled: index === count - 1,
            onSelect: () => onMove(1),
        },
        {
            id: 'remove',
            label: 'Remove split',
            danger: true,
            disabled: !canRemove,
            onSelect: onRemove,
        },
    ];

    return (
        <div
            // `group/leg` drives the quiet-until-active row actions below.
            className={cn(
                'group/leg grid items-center gap-2 border-b border-border/20 px-3 py-0.5',
                bad && 'bg-state-danger-soft/40 shadow-[inset_2px_0_0_var(--color-state-danger)]',
                !bad && 'focus-within:bg-accent-soft/20',
                dragging && 'opacity-40',
            )}
            style={{ gridTemplateColumns: cols }}
            onKeyDown={(e) => {
                // Alt+Arrow reorders. The whole point of the keyboard path is
                // that reorder must not require a drag — no drag on touch, and
                // none from a keyboard at all. stopPropagation keeps the
                // editor's own key handling out of it.
                if (!e.altKey || (e.key !== 'ArrowUp' && e.key !== 'ArrowDown')) return;
                e.preventDefault();
                e.stopPropagation();
                onMove(e.key === 'ArrowUp' ? -1 : 1);
            }}
            onContextMenu={(e) => {
                e.preventDefault();
                setMenu({ x: e.clientX, y: e.clientY });
            }}
            onDragOver={(e) => {
                e.preventDefault();
                onDragOver();
            }}
            onDrop={(e) => {
                e.preventDefault();
                onDrop();
            }}
        >
            {/* col1 — row number. Visible because a validation message that
                says "Posting 4" is useless against rows that are not
                numbered, and because a reorder needs a before/after the user
                can actually read. */}
            <span
                aria-hidden
                className={cn(
                    'pr-1 text-right font-mono text-[0.6875rem] tabular-nums',
                    bad ? 'font-semibold text-state-danger' : 'text-text-subtle',
                )}
            >
                {n}
            </span>

            {/* col2 — category picker + the reserved per-posting tags slot,
                side by side. ADR-0009 keeps tags at header level; the slot is
                reserved so the tags PR does not reshuffle this form. */}
            <div className="flex min-w-0 items-center gap-1">
                <div className="min-w-0 flex-1">
                    <AccountCategoryPicker
                        id={fieldId('counterparty')}
                        accounts={accounts}
                        isEligible={isEligibleCounterparty}
                        frequent={frequent}
                        valueId={posting.counterpartyId}
                        onChangeId={(id) => onChange({ counterpartyId: id })}
                        placeholder="Category or account…"
                        ariaLabel={`Posting ${n} category`}
                        disabled={disabled}
                        invalid={badCounterparty}
                        // The panel is clipped by the leg viewport, on purpose
                        // — see the `compact` prop's note. `compact` makes it
                        // short enough to fit; the open callback makes sure
                        // there is room below the input for it to fit INTO.
                        compact
                        onOpenChange={(isOpen) => { if (isOpen) onPickerOpen(); }}
                    />
                </div>
                <TagsPlaceholder
                    compact
                    hint="Per-posting tags coming soon"
                    aria-label={`Posting ${n} tags`}
                />
            </div>

            {/* col3 — per-leg memo. A textarea rather than an input even
                though the row is one line tall: an <input> runs the HTML
                value-sanitisation algorithm, which STRIPS newlines, so
                rendering an existing multi-line leg memo in one would mangle
                it and the next save would persist the mangling. Fixed height,
                no auto-grow — the grid's height budget is what makes the
                scroll viewport predictable. */}
            <textarea
                rows={1}
                value={posting.legMemo}
                disabled={disabled}
                placeholder="Memo (optional)"
                aria-label={`Posting ${n} memo`}
                onChange={(e) => onChange({ legMemo: e.target.value })}
                onKeyDown={(e) => {
                    if (e.key === 'Enter' && e.shiftKey) e.stopPropagation();
                }}
                className="h-control-28px w-full resize-none overflow-y-auto rounded border border-border bg-surface px-2 py-1 text-xs leading-tight focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50"
            />

            {/* col4 — leg amount. */}
            <input
                id={fieldId('amount')}
                ref={amountRef}
                type="number"
                step="0.01"
                inputMode="decimal"
                value={posting.amount}
                placeholder="0.00"
                disabled={disabled}
                aria-label={`Posting ${n} amount`}
                aria-invalid={badAmount || undefined}
                onChange={(e) => onChange({ amount: e.target.value })}
                onBlur={(e) => {
                    const text = e.target.value.trim();
                    if (text.length === 0) return;
                    const v = Number(text);
                    if (!Number.isNaN(v)) onChange({ amount: v.toFixed(2) });
                }}
                className={cn(
                    'h-control-28px w-full rounded border bg-surface px-2 text-right font-mono text-xs tabular-nums focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                    badAmount ? 'border-state-danger' : 'border-border',
                    amountTone,
                )}
            />

            {/* col5 — drag handle, defect marker, row menu. Quiet at rest and
                emphasised on the row you are working in: twenty-five loud
                kebabs is noise, but hiding them entirely is the affordance
                failure ADR-0021 Rule 10 exists to prevent, so they stay
                visible rather than appearing on hover. */}
            <div className="flex items-center justify-end gap-0.5">
                {bad ? (
                    <span
                        title={issues.map((i) => i.hint).join(' · ')}
                        aria-hidden
                        className="flex size-control-20px items-center justify-center text-state-danger"
                    >
                        <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
                            <path d="M8 4v5" />
                            <circle cx="8" cy="12" r="0.6" fill="currentColor" />
                        </svg>
                    </span>
                ) : null}
                <span
                    role="button"
                    aria-label={`Reorder posting ${n}`}
                    title="Drag to reorder, or Alt+↑ / Alt+↓"
                    draggable={!disabled && canRemove}
                    onDragStart={(e) => {
                        e.dataTransfer.setData('text/posting-key', posting.key);
                        e.dataTransfer.effectAllowed = 'move';
                        onDragStart();
                    }}
                    onDragEnd={onDragEnd}
                    className="flex size-control-20px cursor-grab select-none items-center justify-center text-text-subtle opacity-50 transition-opacity group-hover/leg:opacity-100 group-focus-within/leg:opacity-100"
                >
                    <svg width="10" height="10" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true">
                        <circle cx="6" cy="3" r="1.3" /><circle cx="10" cy="3" r="1.3" />
                        <circle cx="6" cy="8" r="1.3" /><circle cx="10" cy="8" r="1.3" />
                        <circle cx="6" cy="13" r="1.3" /><circle cx="10" cy="13" r="1.3" />
                    </svg>
                </span>
                <RowActionsButton
                    size="sm"
                    label={`Actions for posting ${n}`}
                    disabled={disabled}
                    className="opacity-50 transition-opacity group-hover/leg:opacity-100 group-focus-within/leg:opacity-100"
                    onOpen={({ x, y }) => setMenu({ x, y })}
                />
                {menu !== null ? (
                    <ContextMenu anchor={menu} items={menuItems} onClose={() => setMenu(null)} />
                ) : null}
            </div>
        </div>
    );
}
