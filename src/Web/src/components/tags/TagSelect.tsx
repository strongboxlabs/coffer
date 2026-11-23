import { useEffect, useId, useMemo, useRef, useState } from 'react';

import { useFieldSizeClass } from '@/components/ui/fieldSize';
import { usePendingButtonProps } from '@/components/ui/pending';
import { happenedInside } from '@/components/ui/popoverDismissal';
import { cn } from '@/lib/cn';
import { TAG_LIMIT } from '@/lib/annotations/notes';
import type { TagDto } from '@/lib/types';

import { TagLine } from './TagLine';

/**
 * The tag editor: a readable strip, and a button that opens a checkbox list.
 *
 * ONE control for both levels — a transaction's tags and a posting's. They used
 * to differ (chips on the header, nothing usable on a posting), which made the
 * same concept look like two.
 *
 * WHY NOT CHIPS. A chip strip wraps, so its height is a function of how many
 * tags there are and how long their names run. The header could absorb that; a
 * 30px leg row cannot, and that row's fixed height is what lets the split list
 * scroll instead of the editor growing. A transaction can carry up to
 * TAG_LIMIT tags of up to TAG_MAX_LENGTH characters each, so "a few short
 * tags" is not the case to design for.
 *
 * TWO JOBS, TWO TARGETS. The strip is for reading and the button is for
 * editing, so neither compromises for the other: the strip can be a plain
 * truncating line, and the panel can be wider than the control it hangs off,
 * which is what a long name needs.
 *
 * The panel is filter-first because a ledger's tag list has no upper bound, and
 * once it outgrows the panel, scrolling a list is not a search.
 */

const TAG_MAX_LENGTH = 64;

/** The list's cap when there is room for it. */
const LIST_MAX_HEIGHT = 160;

/** Filter bar, create row and borders — everything in the panel that is not the list. */
const PANEL_CHROME = 70;

/**
 * The floor. Below this a list is not worth rendering, so the panel is allowed
 * to overhang the window rather than shrink further — which only happens when
 * the control is within ~150px of the bottom of the screen.
 */
const LIST_MIN_HEIGHT = 84;

/** Matches w-fixed-320px. Needed as a number to place the panel before it exists. */
const PANEL_WIDTH = 320;

/** Keep the panel off the window's edges. */
const EDGE = 8;

/** The strip's text while the set is empty and nothing is inherited. */
const EMPTY_PLACEHOLDER = 'Tags (optional)';

export function TagSelect({
    id,
    tags,
    allTags,
    onChange,
    disabled = false,
    pending = false,
    compact,
    placeholderTags,
    placeholderFrom,
    maxCount = TAG_LIMIT,
    'aria-label': ariaLabel,
    'aria-describedby': ariaDescribedBy,
}: {
    /**
     * Lands on the EDIT BUTTON, not the root, so a sibling `FieldLabel
     * htmlFor` names the thing that actually opens — and so clicking the
     * caption opens the panel, the way clicking a field's label focuses its
     * input. Deliberately not a wrapping `<label>`: a label forwards a click
     * from anywhere inside it to its control, which would make the strip open
     * the panel, and the strip exists to be read and scrolled without that.
     */
    id?: string;
    /** Names currently applied, in the order the server returned them. */
    tags: readonly string[];
    /** The ledger's tag dictionary. */
    allTags: readonly TagDto[];
    onChange: (next: readonly string[]) => void;
    /** The tags cannot be changed here at all. */
    disabled?: boolean;
    /** The editor's save is in flight: the edit button keeps focus and refuses
     *  to open the list (pending.ts). */
    pending?: boolean;
    /** The splits-grid form: a narrower strip, sized to the leg row's budget. */
    compact?: boolean;
    /**
     * The other side's tags, shown with the inherited marker while `tags` is
     * empty. Picking or unpicking a tag then starts from these: the own set
     * becomes the inherited set with that one change, never the pick alone.
     */
    placeholderTags?: readonly string[];
    /** The account `placeholderTags` come from, for the marker's label. */
    placeholderFrom?: string | null;
    /**
     * The most tags the set may hold. The server refuses only a set that grows
     * past the limit, so a set already over it (a merge made it) may be kept or
     * trimmed: the caller passes the larger of the limit and the stored size.
     */
    maxCount?: number;
    'aria-label'?: string;
    /** Describes the edit button, e.g. the "from X" caption while inheriting. */
    'aria-describedby'?: string;
}) {
    const pendingProps = usePendingButtonProps();
    // The splits grid's compact select keeps its 24px: its leg rows have no
    // room for more, in any layout.
    const sizeClass = useFieldSizeClass();
    const [open, setOpen] = useState(false);
    const [place, setPlace] = useState<{ top: number; left: number; list: number } | null>(null);
    const [filter, setFilter] = useState('');
    const rootRef = useRef<HTMLSpanElement>(null);
    const buttonRef = useRef<HTMLButtonElement>(null);
    const filterRef = useRef<HTMLInputElement>(null);
    const listId = useId();

    // While this side inherits, the set the user is editing is the inherited
    // one: unpicking an inherited tag or adding to it starts from what the row
    // shows, so the first change keeps the rest of what was there.
    const inheriting = tags.length === 0 && (placeholderTags?.length ?? 0) > 0;
    const effective = inheriting ? placeholderTags! : tags;

    const applied = useMemo(
        () => new Set(effective.map((t) => t.toLowerCase())),
        [effective],
    );

    // Just the dictionary. An applied tag is ALWAYS in it — txn_header_tags and
    // txn_leg_tags both carry a composite FK into tags(id, ledger_id) — so a
    // missing one means the fetch has not finished or something upstream is
    // broken, neither of which this control should paper over. Synthesising a
    // row for it was the first thing tried here and it lied: the stand-in
    // carried no colour, so a tag that has one would have rendered without.
    const pool = useMemo(
        () => [...allTags].sort((a, b) =>
            a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })),
        [allTags],
    );

    const matches = useMemo(() => {
        const q = filter.trim().toLowerCase();
        return q.length === 0 ? pool : pool.filter((t) => t.name.toLowerCase().includes(q));
    }, [pool, filter]);

    // Offer creation only for a name that is not already in the dictionary —
    // case-insensitively, matching how the server resolves it.
    const trimmed = filter.trim();
    const canCreate =
        trimmed.length > 0
        && trimmed.length <= TAG_MAX_LENGTH
        && !pool.some((t) => t.name.toLowerCase() === trimmed.toLowerCase())
        && effective.length < maxCount;

    useEffect(() => {
        if (!open) return;
        const onDown = (e: MouseEvent) => {
            if (rootRef.current && !happenedInside(e, rootRef.current)) setOpen(false);
        };
        document.addEventListener('mousedown', onDown);
        return () => document.removeEventListener('mousedown', onDown);
    }, [open]);

    // The filter takes focus once the panel is on screen. The panel renders
    // only after it is placed (below), one render after `open` turns true, so
    // an effect on `open` alone ran while the filter did not exist yet.
    const placed = place !== null;
    useEffect(() => {
        if (open && placed) filterRef.current?.focus();
        else if (!open) setFilter('');
    }, [open, placed]);

    // The panel is FIXED to the viewport, placed once when it opens, and closed
    // the moment anything scrolls.
    //
    // The case for keeping it absolute inside whatever scrolls: a dropdown that
    // can be visible while its own input is not ends up floating over unrelated
    // chrome, still taking clicks into a field nobody can see. Closing on scroll
    // answers that — the panel can never outlive the view it was opened from.
    //
    // Staying inside has two costs of its own. Absolutely positioned children
    // count toward a scroller's scrollable content, so opening from the last leg
    // of a long split transaction grows the grid's scroll range and leaves a dead
    // band under "Add split". And the panel loses a stacking fight with the
    // grid's sticky header and footer (both z-20) and renders sliced in half.
    useEffect(() => {
        if (!open) { setPlace(null); return; }
        const el = rootRef.current;
        if (el === null) return;

        const rect = el.getBoundingClientRect();
        // ALWAYS DOWN. No flipping: choosing a side by available room means two
        // adjacent legs in one grid open opposite ways, and opening upward needs
        // the panel's height BEFORE it exists — an estimate that assumes a full
        // list and a Create row, so a short list floats two rows above the
        // control, pointing at nothing. Both come from the flip.
        //
        // The cost, stated plainly: the panel is fixed, so the browser will not
        // scroll to reveal anything hanging below the window. The list is
        // therefore sized to the room that is actually there — from the lowest
        // rows you get a SHORTER list, never a hidden one.
        const room = window.innerHeight - rect.bottom - 3 - EDGE;
        const list = Math.floor(Math.max(
            LIST_MIN_HEIGHT,
            Math.min(LIST_MAX_HEIGHT, room - PANEL_CHROME),
        ));

        setPlace({
            top: rect.bottom + 3,
            // Right-aligned to the control, then kept on screen.
            left: Math.max(
                EDGE,
                Math.min(rect.right - PANEL_WIDTH, window.innerWidth - PANEL_WIDTH - EDGE),
            ),
            list,
        });

        // Attached a frame late: focusing the filter can itself scroll an
        // ancestor, and listening immediately would close the panel on the very
        // scroll that opening it caused.
        let dispose = () => {};
        const frame = requestAnimationFrame(() => {
            const close = () => setOpen(false);
            // A scroll INSIDE the panel is the user reading the list, not the
            // view moving out from under it. Dragging the list's own scrollbar
            // fires a scroll event that reaches this capture listener, and
            // closing on it made the panel vanish mid-drag. Only a scroll that
            // did NOT come from this control counts as the view moving.
            const closeOnScroll = (e: Event) => {
                if (happenedInside(e, rootRef.current)) return;
                setOpen(false);
            };
            // Capture, because the scroll that matters happens on an ancestor
            // and scroll events do not bubble.
            window.addEventListener('scroll', closeOnScroll, true);
            window.addEventListener('resize', close);
            dispose = () => {
                window.removeEventListener('scroll', closeOnScroll, true);
                window.removeEventListener('resize', close);
            };
        });
        return () => { cancelAnimationFrame(frame); dispose(); };
    }, [open]);

    const toggle = (name: string) => {
        const lower = name.toLowerCase();
        if (applied.has(lower)) {
            onChange(effective.filter((t) => t.toLowerCase() !== lower));
        } else if (effective.length < maxCount) {
            onChange([...effective, name]);
        }
    };

    const create = () => {
        if (!canCreate) return;
        onChange([...effective, trimmed]);
        setFilter('');
        filterRef.current?.focus();
    };

    // Escape with the list open closes the list and nothing else, the rule
    // every list in the editors follows: marked handled with preventDefault
    // (the editors and Modal read it) and stopped. Focus goes back to the button,
    // or it would fall to the page with the list's filter, and the next Escape
    // would land outside the editor instead of cancelling it. On the root, so
    // it holds wherever in the control focus is, the button included; focus
    // does not leave the control while the list is open (onFocusLeave,
    // keepFocus).
    const onEscape = (e: React.KeyboardEvent) => {
        if (e.key !== 'Escape' || !open) return;
        e.preventDefault();
        e.stopPropagation();
        setOpen(false);
        buttonRef.current?.focus();
    };

    // Focus moving to another field closes the list. Left open, it would
    // still be on screen with focus elsewhere, and the Escape meant to close
    // it would never reach onEscape: it would reach the editor and cancel the
    // edit, or close the dialog. Only a move to a NAMED element counts. A
    // press on the list never moves focus out of the control (keepFocus), and
    // focus going nowhere, such as the window losing it, is not focus leaving
    // for another field. A click anywhere outside is the mousedown listener's.
    const onFocusLeave = (e: React.FocusEvent) => {
        const next = e.relatedTarget;
        if (!open || next === null) return;
        if (rootRef.current?.contains(next) === true) return;
        setOpen(false);
    };

    // A press on the list keeps focus where it is, unless it lands on one of
    // the list's own fields (the filter, a checkbox). A press on a part that
    // cannot take focus (a tag's name, the padding, "No match.") would
    // otherwise send focus to the nearest ancestor that can. In a dialog that
    // is the dialog's panel, outside the control: the list would close before
    // the click toggled the tag, and even where it stayed open, focus would sit
    // where the next Escape could not reach onEscape. The click still lands,
    // and a tag's name still toggles its checkbox.
    const keepFocus = (e: React.MouseEvent) => {
        if (!(e.target instanceof HTMLInputElement)) e.preventDefault();
    };

    return (
        <span
            ref={rootRef}
            onKeyDown={onEscape}
            onBlur={onFocusLeave}
            className={cn('flex items-stretch', compact ? 'shrink-0' : 'min-w-0 flex-1')}
        >
            {/* The strip. Not a button: clicking it must not open the panel, or
                there is no way to scroll a long value without the panel jumping
                in front of it. */}
            <span
                aria-label={ariaLabel}
                className={cn(
                    // px-2, matching the inputs this sits beside and under. At
                    // px-1.5 the tag text began 2px left of the category above it.
                    'flex items-center overflow-hidden rounded-l border border-r-0 border-border bg-surface px-2',
                    // 24px only in the splits grid, whose leg rows are 30px and
                    // have no room for more. Everywhere else this sits among the
                    // editor's other fields, which are 28px — at 24 it read as a
                    // control that had been dropped in rather than laid out.
                    compact ? 'h-control-24px w-fixed-128px' : cn('h-control-28px min-w-0 flex-1', sizeClass),
                    (disabled || pending) && 'bg-surface-header',
                )}
            >
                {/* Own names in the full text colour, like the memo typed
                    beside this control: here they are a field's value, not the
                    register's secondary line. The placeholder says what the
                    field is, in the words the memo's empty state uses. This
                    control is the editors' only; the register's filter bar
                    has its own (TagCombobox). */}
                {inheriting ? (
                    <TagLine tags={effective} inherited={{ from: placeholderFrom }} />
                ) : tags.length === 0 ? (
                    <span className="truncate text-[0.6875rem] text-text-subtle">{EMPTY_PLACEHOLDER}</span>
                ) : (
                    <TagLine tags={tags} ownTone="text" />
                )}
            </span>

            <button
                ref={buttonRef}
                id={id}
                type="button"
                disabled={disabled}
                aria-label={`Edit ${ariaLabel ?? 'tags'}`}
                aria-describedby={ariaDescribedBy}
                aria-expanded={open}
                aria-controls={open ? listId : undefined}
                {...pendingProps(pending, () => setOpen((o) => !o))}
                className={cn(
                    'flex w-control-20px shrink-0 items-center justify-center rounded-r border border-border bg-surface-header text-text-subtle hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-60 aria-disabled:cursor-not-allowed aria-disabled:opacity-60',
                    compact ? 'h-control-24px' : cn('h-control-28px', sizeClass),
                )}
            >
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3"
                     className="size-icon-xs" aria-hidden>
                    <path d="m6 9 6 6 6-6" />
                </svg>
            </button>

            {open && place !== null ? (
                <div
                    id={listId}
                    onMouseDown={keepFocus}
                    style={{ top: place.top, left: place.left }}
                    className="fixed z-50 w-fixed-320px overflow-hidden rounded border border-border-strong bg-surface shadow-lg"
                >
                    <div className="border-b border-border bg-surface-header p-1">
                        <input
                            ref={filterRef}
                            value={filter}
                            onChange={(e) => setFilter(e.target.value)}
                            onKeyDown={(e) => { if (e.key === 'Enter' && canCreate) { e.preventDefault(); create(); } }}
                            placeholder="Type to filter…"
                            maxLength={TAG_MAX_LENGTH}
                            aria-label="Filter tags"
                            className="w-full rounded border border-border bg-surface px-1.5 py-0.5 text-xs focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                    </div>

                    <div
                        role="group"
                        aria-label="Tags"
                        // Inline because the value is measured, not a token: the
                        // available space depends on where this row sits in its
                        // scroller, which no class can know.
                        style={{ maxHeight: place.list }}
                        className="overflow-y-auto p-0.5"
                    >
                        {matches.length === 0 ? (
                            <p className="px-1.5 py-1 text-xs text-text-subtle">
                                {pool.length === 0 ? 'No tags in this ledger yet.' : 'No match.'}
                            </p>
                        ) : (
                            matches.map((t) => {
                                const on = applied.has(t.name.toLowerCase());
                                return (
                                    <label
                                        key={t.id}
                                        className="flex cursor-pointer items-center gap-2 rounded px-1.5 py-0.5 text-xs hover:bg-surface-hover"
                                    >
                                        <input
                                            type="checkbox"
                                            checked={on}
                                            // Square, not round. A checkbox is the
                                            // one shape that says "several".
                                            onChange={() => toggle(t.name)}
                                            disabled={!on && effective.length >= maxCount}
                                            className="size-icon-xs shrink-0 cursor-pointer"
                                        />
                                        {t.color === null ? null : (
                                            <span
                                                aria-hidden
                                                className="h-[0.75rem] w-[2px] shrink-0 rounded-[1px]"
                                                style={{ backgroundColor: t.color }}
                                            />
                                        )}
                                        <span className="min-w-0 truncate" title={t.name}>{t.name}</span>
                                    </label>
                                );
                            })
                        )}
                    </div>

                    {canCreate ? (
                        <button
                            type="button"
                            onClick={create}
                            className="flex w-full items-center gap-1.5 border-t border-border px-1.5 py-1 text-left text-xs text-accent hover:bg-surface-hover"
                        >
                            <span className="font-bold">+</span>
                            Create “{trimmed}”
                        </button>
                    ) : null}
                </div>
            ) : null}
        </span>
    );
}
