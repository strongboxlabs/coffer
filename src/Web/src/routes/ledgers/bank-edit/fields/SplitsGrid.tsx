/**
 * The splits region of the bank editor: every leg of a multi-posting
 * transaction, plus the affordances for adding, reordering and fixing them.
 *
 * WHY ITS OWN GRID. Leg rows appended to the editor on the register's own
 * eight-column template have three problems which only show up past about six
 * splits, and the editor's real ceiling is twenty-five:
 *
 *   * THE EDITOR GROWS WITHOUT BOUND. Twenty-five ~62px leg rows is ~1550px of
 *     form. The Save button sits below all of it, so the further down the split
 *     you work the further away the way out becomes, and on a laptop the
 *     running total scrolls off the top of the screen — the one number you
 *     are watching while you type amounts.
 *
 *   * NOTHING NAMES A ROW. Validation as one wrapping sentence with up to
 *     forty clauses ("Posting 4: amount is required. · Posting 11: pick a
 *     counterparty. · …") over rows that carry no visible number means
 *     matching a clause to a row by counting from the top.
 *
 *   * DRAG-ONLY REORDER that highlights the target ROW cannot express
 *     "between 2 and 3". Where the row lands depends on which direction you
 *     dragged from — true, defensible, and impossible to predict from the
 *     highlight.
 *
 * The fix is one move: give the legs their OWN grid. They have no register
 * counterpart to align with — columns 1-3 are empty on every leg row — so
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
import { counterpartyWords, type CounterpartyKind, type CounterpartyWords } from '@/lib/counterparty';
import type { AccountSummary, FrequentCounterpartiesResponse } from '@/lib/types';
import { secondAmountCaption, type SecondAmountSide } from '@/lib/secondAmount';

import type { PostingDraft } from '../postingDraft';
import type { PostingIssue, PostingIssueKind } from '../validation';
import type { TagDto } from '@/lib/types';
import { Mixed } from '../../home/Money';
import { PostingRowEditor } from './PostingRowEditor';
import { InsertionLine } from '@/components/ui/InsertionLine';
import { usePendingButtonProps } from '@/components/ui/pending';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';

/**
 * The splits grid's own columns: `# · Category+Tags · Memo · Amount · actions`.
 *
 * BOTH middle tracks flex, 2:1 in the category's favour. A fixed 17rem
 * category with memo as the only flexible track would, on a wide monitor, give a
 * 272px category against a 1230px memo — a five-to-one split in favour of the
 * field that matters less. The category carries the account path (parent and
 * child, two names joined by a separator) and is what a reader scans down the
 * list; it also holds the posting's own tag chips (ADR-0103), so it is the
 * track that needs room to grow. The memo keeps a 12rem floor because a typical
 * payroll-tax line memo has to fit without scrolling inside its own box.
 */
const LEG_COLS = '2rem minmax(16rem, 2fr) minmax(12rem, 1fr) 6.5rem 3.5rem';

/**
 * The columns while any line goes to a real account in another currency (ADR-0114 D14,
 * the ADR-0025 2026-11-14 amendment): one 6.5rem track right of Amount for the second
 * amount, captioned in the header the way Amount is. Memo gives up the width. Lines with
 * no second amount leave the track empty.
 */
const LEG_COLS_SECOND = '2rem minmax(16rem, 2fr) minmax(12rem, 1fr) 6.5rem 6.5rem 3.5rem';

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
 * included.
 */
const LABEL_INSET = 'border border-transparent px-2';

interface SplitsGridProps {
    postings: readonly PostingDraft[];
    /** Every defect in the whole editor, from `postingIssues`. */
    issues: readonly PostingIssue[];
    accounts: readonly AccountSummary[];
    isEligibleCounterparty: (a: AccountSummary) => boolean;
    /** Which side the register is on. A kind rather than a label, because the
     *  header, the issue strip and each row's screen-reader name all need
     *  different words for it. */
    counterpartyKind: CounterpartyKind;
    frequent: FrequentCounterpartiesResponse | null;
    /** The ledger's tag dictionary, handed to each posting's tag field. */
    allTags: readonly TagDto[];
    /** The total's currency: the one every line's Amount is in. */
    currency: string;
    /** The currency each line's Amount is typed and rounded in (ADR-0114 D14, D15). */
    currencyOf: (posting: PostingDraft) => string;
    /** Each line's second amount, from the shell's one predicate: null for a line
     *  that has none (ADR-0114 D13, D14). */
    sideOf: (posting: PostingDraft) => SecondAmountSide | null;
    /** For a line's derived-rate prefill. */
    ledgerId: string;
    /** The entry's date, `yyyy-MM-dd`: the day a prefill's rate is for. */
    date: string;
    /** Sum of the posting amounts — informational (see the header note). The Amount
     *  column only, in `currency`, never a second amount. Null when the lines are in
     *  more than one currency, a category's split paid from accounts in different
     *  ones: no figure adds currencies, and the footer shows the mixed marker
     *  (ADR-0114 D7). */
    total: number | null;
    /** The editor's save is in flight: the legs' fields are disabled, and the
     *  grid's buttons keep focus and refuse to act (pending.ts). */
    pending: boolean;
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
    counterpartyKind,
    frequent,
    allTags,
    currency,
    currencyOf,
    sideOf,
    ledgerId,
    date,
    total,
    pending,
    focusKey,
    onAutoFocused,
    onPatch,
    onAdd,
    onRemove,
    onReorder,
    onMove,
}: SplitsGridProps) {
    const pendingProps = usePendingButtonProps();
    const counterparty = counterpartyWords(counterpartyKind);
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
     * it is lying about where the row will land.
     */
    const dropsAfter = fromIdx >= 0 && overIdx > fromIdx;
    const showLineBefore = (i: number) => overIdx === i && !dropsAfter && fromIdx !== overIdx;
    const showLineAfter = (i: number) => overIdx === i && dropsAfter;

    const endDrag = useCallback(() => setDrag(null), []);

    // Remove split, chosen from a leg's own menu, takes the leg and its kebab
    // away: focus goes to the next leg's kebab, the previous leg's, or Add
    // split, never to the page, where the next Escape would no longer reach
    // the editor (ADR-0023 §L).
    const addRef = useRef<HTMLButtonElement | null>(null);
    const actionsControl = useRowRemovalFocus(postings.map((p) => p.key), addRef);

    // The second amount's column, while any line has one (ADR-0114 D14). Its header
    // caption names a direction, Received or Sent, only when every such line shares it.
    // Each line's direction follows its own Amount's sign, so two lines can go opposite
    // ways, and either word would then be false for one of them: the caption then reads
    // Other amount, which names no direction. Each field's accessible name and title
    // still name its own direction.
    const sides = postings.map((p) => sideOf(p));
    const shown = sides.filter((side): side is SecondAmountSide => side !== null);
    const hasSecond = shown.length > 0;
    const sharedDirection = hasSecond && shown.every((s) => s.direction === shown[0]!.direction)
        ? shown[0]!.direction
        : null;
    const cols = hasSecond ? LEG_COLS_SECOND : LEG_COLS;

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
                    // one. This handler does not cover the menu.
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
                        style={{ gridTemplateColumns: cols }}
                    >
                        {/* Each label carries the same box model as the
                            control below it — see LABEL_INSET. The row number
                            is the exception: it is bare text with `pr-1`, so
                            its label matches that instead. */}
                        <span className="pr-1 text-right">#</span>
                        <span className={LABEL_INSET}>{counterparty.noun} · Tags</span>
                        <span className={LABEL_INSET}>Memo</span>
                        <span className={cn(LABEL_INSET, 'text-right')}>Amount</span>
                        {hasSecond ? (
                            <span className={cn(LABEL_INSET, 'text-right')}>
                                {sharedDirection !== null ? secondAmountCaption(sharedDirection) : 'Other amount'}
                            </span>
                        ) : null}
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
                                    counterpartyKind={counterpartyKind}
                                    frequent={frequent}
                                    allTags={allTags}
                                    currency={currencyOf(p)}
                                    side={sides[idx] ?? null}
                                    secondColumn={hasSecond}
                                    ledgerId={ledgerId}
                                    date={date}
                                    cols={cols}
                                    pending={pending}
                                    autoFocusAmount={p.key === focusKey}
                                    onAutoFocused={onAutoFocused}
                                    fieldId={(kind) => fieldId(p.key, kind)}
                                    dragging={drag?.from === p.key}
                                    onChange={(fields) => onPatch(p.key, fields)}
                                    onRemove={() => onRemove(p.key)}
                                    actionsRef={actionsControl(p.key)}
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
                        style={{ gridTemplateColumns: cols }}
                    >
                        <span />
                        <button
                            ref={addRef}
                            type="button"
                            {...pendingProps(pending, onAdd)}
                            aria-label="Add another posting"
                            title="Add another posting to this split"
                            className="flex h-control-24px items-center justify-center gap-1 justify-self-start rounded border border-dashed border-border px-2 text-[0.6875rem] font-medium text-accent hover:border-accent hover:bg-accent-soft/30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50 aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                        >
                            <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
                                <path d="M8 3.5v9M3.5 8h9" />
                            </svg>
                            Add split
                        </button>
                        <span className={cn(LABEL_INSET, 'text-[0.6875rem] text-text-subtle')}>
                            {postings.length} splits
                        </span>
                        {/* The sum's hover only over a sum: "mixed" says its own. */}
                        <span
                            title={total === null ? undefined : 'Sum of postings (saved as the transaction total)'}
                            className={cn(
                                LABEL_INSET,
                                'text-right font-mono text-xs font-semibold tabular-nums text-text',
                            )}
                        >
                            {total === null ? <Mixed /> : formatCurrency(total, currency)}
                        </span>
                        {hasSecond ? <span /> : null}
                        <span />
                    </div>
                </div>
            </div>

            <SplitsIssueSummary issues={issues} fieldId={fieldId} counterparty={counterparty} />
        </>
    );
}

/**
 * The defects, grouped by what is wrong rather than listed per posting, with
 * each row number a link that focuses the field.
 *
 * Grouping is the difference between a sentence a reader can act on and one
 * they have to parse: eight blank amounts listed per posting render as eight
 * clauses naming eight postings, which is the same information spread over a paragraph
 * that wraps to three lines. "Enter an amount: 4, 7, 11" is one clause and the
 * numbers are the work queue.
 */
function SplitsIssueSummary({
    issues,
    fieldId,
    counterparty,
}: {
    issues: readonly PostingIssue[];
    fieldId: (key: string, kind: PostingIssueKind) => string;
    counterparty: CounterpartyWords;
}) {
    // Rule order, not encounter order: the two fixed groups keep the same left-
    // to-right position whichever defect happens to appear first, so the strip
    // does not reshuffle itself while the user fixes rows.
    //
    // A missing second amount follows them, one group per sentence, headed by the
    // field's own sentence (its hint): the sentence names the currency, the account
    // and which way the money went, so lines share a group only when all three agree
    // (ADR-0114 D14). Those groups follow the current first line of each, the order
    // the issues arrive in, so two of them can swap places when one group's first
    // line is fixed and its later lines are not; the two fixed groups never move.
    const fixed: readonly { kind: PostingIssueKind; label: string }[] = [
        { kind: 'amount', label: 'Enter an amount' },
        { kind: 'counterparty', label: `Pick ${counterparty.withArticle}` },
    ];
    const secondLabels = [...new Set(issues.filter((i) => i.kind === 'second').map((i) => i.hint))];
    const groups = [
        ...fixed.map(({ kind, label }) => ({ kind, label, hits: issues.filter((i) => i.kind === kind) })),
        ...secondLabels.map((label) => ({
            kind: 'second' as const,
            label,
            hits: issues.filter((i) => i.kind === 'second' && i.hint === label),
        })),
    ].filter((g) => g.hits.length > 0);
    if (groups.length === 0) return null;

    return (
        <div
            role="alert"
            className="flex flex-wrap items-center gap-x-2 gap-y-1 border-t border-state-danger/30 bg-state-danger-soft/60 px-3 py-1.5 text-[0.6875rem] text-state-danger"
        >
            <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true" className="shrink-0">
                <path d="M8 4v5" />
                <circle cx="8" cy="12" r="0.6" fill="currentColor" />
            </svg>
            {groups.map(({ kind, label, hits }) => {
                return (
                    <span key={`${kind}:${label}`} className="flex items-center gap-1">
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
