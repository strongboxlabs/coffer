/**
 * The splits region of the bank editor: every leg of a multi-posting
 * transaction, plus the affordances for adding, reordering and fixing them.
 *
 * WHAT THIS REPLACED, AND WHY. Leg rows used to be appended to the editor on
 * the register's own eight-column template. That had three consequences which
 * only show up past about six splits, and the editor's real ceiling is
 * twenty-five:
 *
 *   * THE EDITOR GREW WITHOUT BOUND. Twenty-five ~62px leg rows is ~1,550px of
 *     form. The Save button sat below all of it, so the further down the split
 *     you worked the further away the way out became, and on a laptop the
 *     running total scrolled off the top of the screen — the one number you
 *     are watching while you type amounts.
 *
 *   * NOTHING NAMED A ROW. Validation was one wrapping sentence with up to
 *     forty clauses ("Posting 4: amount is required. · Posting 11: pick a
 *     counterparty. · …") over rows that carried no visible number, so
 *     matching a clause to a row meant counting from the top.
 *
 *   * REORDER WAS DRAG-ONLY, and a drag highlighted the target ROW, which
 *     cannot express "between 2 and 3". Where the row landed depended on which
 *     direction you had dragged from — true, defensible, and impossible to
 *     predict from the highlight.
 *
 * The fix is one move: give the legs their OWN grid. They have no register
 * counterpart to align with — columns 1-3 were empty on every leg row — so
 * stepping out of the register template buys back three tracks of width, which
 * is enough to put category, memo and amount side by side instead of stacked.
 * A leg row is then 30px instead of ~62px, which makes a fixed-height scroll
 * viewport practical: the list scrolls, the editor does not grow, the total
 * and the footer stay where they are at any split count. The root row still
 * rides the register template, so the transaction's own Date / Payee / Amount
 * stay aligned with the rows above.
 *
 * Everything else follows from having room: a visible row number per leg, a
 * marker on the offending row, a summary strip whose numbers focus the exact
 * field, an insertion line that shows where a drag will actually land, and
 * Move up / Move down in a row menu with Alt+Arrow shortcuts so reorder works
 * from a keyboard and on touch.
 *
 * NO BALANCE RULE, HERE OR ANYWHERE. ADR-0025 rejected auto-balance and sum
 * warnings; the running total in the footer is informational. An "unbalanced"
 * split is not an invalid one and nothing in this file may imply otherwise.
 */

import { useCallback, useId, useRef, useState } from 'react';

import { formatCurrency } from '@/lib/money';
import { cn } from '@/lib/cn';
import type { AccountSummary, FrequentCounterpartiesResponse } from '@/lib/types';

import type { PostingDraft } from '../postingDraft';
import type { PostingIssue, PostingIssueKind } from '../validation';
import { PostingRowEditor } from './PostingRowEditor';

/**
 * The splits grid's own columns: `# · Category+Tags · Memo · Amount · actions`.
 *
 * BOTH middle tracks flex, 2:1 in the category's favour. The first cut gave
 * category a fixed 17rem and made memo the only flexible track, which on a wide
 * monitor meant a 272px category against a 1,230px memo — a five-to-one split in
 * favour of the field that matters less. The category carries the account path
 * ("Alder St · Rent collected") and is what a reader scans down the list; it is
 * also where per-leg tag chips will land when ADR-0009's header-level
 * restriction is lifted, so it is the track that needs room to grow. The memo
 * keeps a 12rem floor because "Social Security (FICA)" has to fit without
 * scrolling inside its own box.
 */
const LEG_COLS = '2rem minmax(16rem, 2fr) minmax(12rem, 1fr) 6.5rem 3.5rem';

/**
 * The whole region: sticky header, ~8.8 leg rows, sticky footer.
 *
 * Sized so the viewport is unambiguously a viewport — a PARTIAL row at the
 * bottom edge is what tells a reader there is more below, and a cap that
 * happened to land on a whole row would read as the end of the list. Below
 * this many splits nothing scrolls and the region is simply as tall as its
 * contents.
 *
 * The header and footer live INSIDE this box, stuck to its top and bottom
 * edges, rather than outside it. That is not a style choice: a scrollbar is
 * ~12-15px of the scrollport's width on Windows, so a header outside the
 * scroller is that much wider than the rows it labels. Both middle tracks flex,
 * so the difference lands entirely on them and everything downstream — the
 * AMOUNT header, the running total, the actions column — drifts right of the
 * column it belongs to. Inside the scroller the header, the rows and the footer
 * share one content box and line up by construction, at any scrollbar width,
 * on any platform, with no measuring.
 */
const VIEWPORT = 'max-h-fixed-344px';

/**
 * The inset a column LABEL needs to sit over its column's text rather than
 * over the box around it.
 *
 * Every control in a leg row is `border` + `px-2`, so its text starts 9px
 * inside the grid track. A header label with no inset therefore hangs 9px
 * outside the figures it labels — small, but on a right-aligned money column
 * it is exactly the "not quite aligned" that makes a grid look untidy.
 *
 * Expressed as a TRANSPARENT BORDER plus the same padding utility rather than
 * a measured offset, so the two stay locked together by construction: `px-2`
 * compiles to `calc(var(--spacing) * 2)` on the label and on the control
 * alike, so the density axis moves both or neither. A right padding written as
 * a literal pixel count would be correct at 1x and wrong at the other two
 * settings — and spacingScale.test.ts bans that spelling outright, comments
 * included, which is how this very note first failed the suite.
 */
const LABEL_INSET = 'border border-transparent px-2';

interface SplitsGridProps {
    postings: readonly PostingDraft[];
    /** Every defect in the whole editor, from `postingIssues`. */
    issues: readonly PostingIssue[];
    accounts: readonly AccountSummary[];
    isEligibleCounterparty: (a: AccountSummary) => boolean;
    frequent: FrequentCounterpartiesResponse | null;
    currency: string;
    /** Sum of the posting amounts — informational (see the header note). */
    total: number;
    disabled: boolean;
    focusKey: string | null;
    onAutoFocused: () => void;
    onPatch: (key: string, fields: Partial<PostingDraft>) => void;
    onAdd: () => void;
    onRemove: (key: string) => void;
    onReorder: (fromKey: string, toKey: string) => void;
    onMove: (key: string, delta: -1 | 1) => void;
}

export function SplitsGrid({
    postings,
    issues,
    accounts,
    isEligibleCounterparty,
    frequent,
    currency,
    total,
    disabled,
    focusKey,
    onAutoFocused,
    onPatch,
    onAdd,
    onRemove,
    onReorder,
    onMove,
}: SplitsGridProps) {
    const uid = useId();
    const viewportRef = useRef<HTMLDivElement | null>(null);
    const headerRef = useRef<HTMLDivElement | null>(null);
    const rowRefs = useRef(new Map<string, HTMLDivElement>());
    // Which row is being dragged, and which row the pointer is over. Both are
    // needed to place the insertion line, because where a drop lands depends
    // on the DIRECTION of the drag — see `dropsAfter` below.
    const [drag, setDrag] = useState<{ from: string; over: string | null } | null>(null);

    // Addressable by draft key, not by row index: a reorder renumbers rows,
    // and an index-derived id would then send focus to the wrong field.
    const fieldId = useCallback(
        (key: string, kind: PostingIssueKind) => `${uid}-${key}-${kind}`,
        [uid],
    );

    const fromIdx = drag === null ? -1 : postings.findIndex((p) => p.key === drag.from);
    const overIdx = drag?.over == null ? -1 : postings.findIndex((p) => p.key === drag.over);
    /**
     * `reorderPostings` splices the row out and re-inserts it at the target's
     * index, so a drop expresses "after you" when dragging down and "before
     * you" when dragging up. The insertion line has to say the same thing or
     * it is lying about where the row will land — which is the whole defect it
     * was added to fix.
     */
    const dropsAfter = fromIdx >= 0 && overIdx > fromIdx;
    const showLineBefore = (i: number) => overIdx === i && !dropsAfter && fromIdx !== overIdx;
    const showLineAfter = (i: number) => overIdx === i && dropsAfter;

    const endDrag = useCallback(() => setDrag(null), []);

    /**
     * Bring a leg row to the top of the viewport, just under the sticky header.
     *
     * Called when that row's category panel opens. The panel is clipped by this
     * viewport by design — see the picker's `compact` prop — so it needs room
     * BELOW the input, and a row sitting at the bottom of the scrollport has
     * none. Scrolling the row up is what turns "the dropdown is cut off" into
     * "the dropdown fits", without the panel having to escape its container and
     * become an opaque menu floating over unrelated chrome.
     *
     * Written against the viewport's own scrollTop rather than
     * `Element.scrollIntoView`, which walks EVERY scrollable ancestor: the
     * register's scroll surface would scroll too, moving the whole editor under
     * the user in the middle of a click.
     */
    const revealRow = useCallback((key: string) => {
        const vp = viewportRef.current;
        const row = rowRefs.current.get(key);
        if (vp === null || row === undefined) return;
        const delta = row.getBoundingClientRect().top - vp.getBoundingClientRect().top;
        vp.scrollTop += delta - (headerRef.current?.offsetHeight ?? 0);
    }, []);

    return (
        <>
            {/* The rail is the same device the sidebar uses to mark a grouped
                block: it says "these belong to the row above" without a box,
                a heading or an indent that would break column alignment. */}
            <div className="flex">
                <div
                    aria-hidden
                    className="w-fixed-12px shrink-0 border-r border-border bg-surface-muted"
                />
                <div
                    ref={viewportRef}
                    className={cn(VIEWPORT, 'min-w-0 flex-1 overflow-y-auto')}
                    // Ends a drag in flight; the insertion line it draws is
                    // meaningless once the rows have moved under the pointer.
                    // An open row MENU is not this handler's to close — its
                    // state lives in PostingRowEditor — and does not need to
                    // be: ContextMenu closes itself on any capture-phase
                    // scroll, which is where that belongs, since every fixed
                    // menu in the app is stranded by a scroll, not just this
                    // one. An earlier version of this comment claimed the
                    // handler covered the menu. It never did.
                    onScroll={endDrag}
                    onDragLeave={(e) => {
                        // Only when the pointer leaves the viewport itself
                        // — the event bubbles from every row it crosses.
                        if (e.currentTarget === e.target) {
                            setDrag((d) => (d === null ? null : { ...d, over: null }));
                        }
                    }}
                >
                    {/* Column header, stuck to the top of the SCROLLPORT —
                        see the VIEWPORT note: being inside the scroller is
                        what makes it exactly as wide as the rows it labels.
                        Its separator is an inset shadow rather than a
                        border-bottom, because a sticky element's border is
                        dropped at some scroll offsets in WebKit; an inset
                        shadow paints identically and is not. */}
                    <div
                        ref={headerRef}
                        aria-hidden
                        className="sticky top-0 z-20 grid items-center gap-2 bg-surface-header px-3 py-1 text-[0.625rem] uppercase tracking-wider text-text-muted shadow-[inset_0_-1px_0_var(--color-border)]"
                        style={{ gridTemplateColumns: LEG_COLS }}
                    >
                        {/* Each label carries the same box model as the
                            control below it — see LABEL_INSET. The row number
                            is the exception: it is bare text with `pr-1`, so
                            its label matches that instead. */}
                        <span className="pr-1 text-right">#</span>
                        <span className={LABEL_INSET}>Category · Tags</span>
                        <span className={LABEL_INSET}>Memo</span>
                        <span className={cn(LABEL_INSET, 'text-right')}>Amount</span>
                        <span />
                    </div>

                    <div>
                        {postings.map((p, idx) => (
                            <div
                                key={p.key}
                                ref={(el) => {
                                    if (el === null) rowRefs.current.delete(p.key);
                                    else rowRefs.current.set(p.key, el);
                                }}
                            >
                                {showLineBefore(idx) ? <InsertionLine /> : null}
                                <PostingRowEditor
                                    posting={p}
                                    index={idx}
                                    count={postings.length}
                                    issues={issues.filter((i) => i.key === p.key)}
                                    accounts={accounts}
                                    isEligibleCounterparty={isEligibleCounterparty}
                                    frequent={frequent}
                                    cols={LEG_COLS}
                                    disabled={disabled}
                                    autoFocusAmount={p.key === focusKey}
                                    onAutoFocused={onAutoFocused}
                                    fieldId={(kind) => fieldId(p.key, kind)}
                                    dragging={drag?.from === p.key}
                                    onPickerOpen={() => revealRow(p.key)}
                                    onChange={(fields) => onPatch(p.key, fields)}
                                    onRemove={() => onRemove(p.key)}
                                    onMove={(delta) => onMove(p.key, delta)}
                                    onDragStart={() => setDrag({ from: p.key, over: null })}
                                    onDragEnd={endDrag}
                                    onDragOver={() =>
                                        setDrag((d) =>
                                            d === null || d.over === p.key
                                                ? d
                                                : { ...d, over: p.key })}
                                    onDrop={() => {
                                        if (drag !== null) onReorder(drag.from, p.key);
                                        endDrag();
                                    }}
                                />
                                {showLineAfter(idx) ? <InsertionLine /> : null}
                            </div>
                        ))}
                    </div>

                    {/* Footer strip, stuck to the bottom of the scrollport.
                        Add on the left where the eye ends up after the last
                        row; the running total on the right, in the amount
                        column, directly under the figures it sums. Sticky
                        rather than below the scroller so it stays put while
                        the list scrolls AND stays exactly as wide as the
                        amounts it totals. Opaque background is load-bearing:
                        rows scroll UNDER it. */}
                    <div
                        className="sticky bottom-0 z-20 grid items-center gap-2 bg-surface-muted px-3 py-1 shadow-[inset_0_1px_0_var(--color-border)]"
                        style={{ gridTemplateColumns: LEG_COLS }}
                    >
                        <span />
                        <button
                            type="button"
                            disabled={disabled}
                            onClick={onAdd}
                            aria-label="Add another posting"
                            title="Add another posting to this split"
                            className="flex h-control-24px items-center justify-center gap-1 justify-self-start rounded border border-dashed border-border px-2 text-[0.6875rem] font-medium text-accent hover:border-accent hover:bg-accent-soft/30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50"
                        >
                            <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
                                <path d="M8 3.5v9M3.5 8h9" />
                            </svg>
                            Add split
                        </button>
                        <span className={cn(LABEL_INSET, 'text-[0.6875rem] text-text-subtle')}>
                            {postings.length} splits
                        </span>
                        <span
                            title="Sum of postings (saved as the transaction total)"
                            className={cn(
                                LABEL_INSET,
                                'text-right font-mono text-xs font-semibold tabular-nums text-text',
                            )}
                        >
                            {formatCurrency(total, currency)}
                        </span>
                        <span />
                    </div>
                </div>
            </div>

            <SplitsIssueSummary issues={issues} fieldId={fieldId} />
        </>
    );
}

/** Where the dragged row will land. */
function InsertionLine() {
    return (
        <div aria-hidden className="relative h-fixed-2px bg-accent">
            <span className="absolute -left-0.5 -top-0.5 size-fixed-8px rounded-full bg-accent" />
        </div>
    );
}

/**
 * The defects, grouped by what is wrong rather than listed per posting, with
 * each row number a link that focuses the field.
 *
 * Grouping is the difference between a sentence a reader can act on and one
 * they have to parse: eight blank amounts used to render as eight clauses
 * naming eight postings, which is the same information spread over a paragraph
 * that wraps to three lines. "Enter an amount: 4, 7, 11" is one clause and the
 * numbers are the work queue.
 */
function SplitsIssueSummary({
    issues,
    fieldId,
}: {
    issues: readonly PostingIssue[];
    fieldId: (key: string, kind: PostingIssueKind) => string;
}) {
    if (issues.length === 0) return null;

    // Rule order, not encounter order: the two groups keep the same left-to-
    // right position whichever defect happens to appear first, so the strip
    // does not reshuffle itself while the user fixes rows.
    const groups: readonly { kind: PostingIssueKind; label: string }[] = [
        { kind: 'amount', label: 'Enter an amount' },
        { kind: 'counterparty', label: 'Pick a category' },
    ];

    return (
        <div
            role="alert"
            className="flex flex-wrap items-center gap-x-2 gap-y-1 border-t border-state-danger/30 bg-state-danger-soft/60 px-3 py-1.5 text-[0.6875rem] text-state-danger"
        >
            <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true" className="shrink-0">
                <path d="M8 4v5" />
                <circle cx="8" cy="12" r="0.6" fill="currentColor" />
            </svg>
            {groups.map(({ kind, label }) => {
                const hits = issues.filter((i) => i.kind === kind);
                if (hits.length === 0) return null;
                return (
                    <span key={kind} className="flex items-center gap-1">
                        <strong className="font-semibold">{label}:</strong>
                        {hits.map((issue) => (
                            <button
                                key={issue.key}
                                type="button"
                                // Names the posting, not just the digit — a
                                // button announced as "7" tells a screen-reader
                                // user nothing about what it does.
                                aria-label={`${issue.message} Go to posting ${issue.n}.`}
                                onClick={() => {
                                    const el = document.getElementById(fieldId(issue.key, kind));
                                    el?.scrollIntoView({ block: 'nearest' });
                                    (el as HTMLElement | null)?.focus();
                                }}
                                className="rounded px-0.5 font-semibold underline underline-offset-2 hover:bg-state-danger/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-state-danger"
                            >
                                {issue.n}
                            </button>
                        ))}
                    </span>
                );
            })}
        </div>
    );
}
