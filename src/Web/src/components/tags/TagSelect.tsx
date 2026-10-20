import { useEffect, useId, useMemo, useRef, useState } from 'react';

import { cn } from '@/lib/cn';
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
 * scroll instead of the editor growing. Moneydance users tag heavily and names
 * reach 50 characters, so "a few short tags" is not the case to design for.
 *
 * TWO JOBS, TWO TARGETS. The strip is for reading and the button is for
 * editing, so neither compromises for the other: the strip can be a plain
 * truncating line, and the panel can be wider than the control it hangs off,
 * which is what a 50-character name needs.
 *
 * The panel is filter-first because with a few hundred tags scrolling a list is
 * not a search.
 */

const TAG_MAX_LENGTH = 64;
const TAG_MAX_COUNT = 20;

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

export function TagSelect({
    id,
    tags,
    allTags,
    onChange,
    disabled,
    compact,
    'aria-label': ariaLabel,
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
    disabled: boolean;
    /** The splits-grid form: a narrower strip, sized to the leg row's budget. */
    compact?: boolean;
    'aria-label'?: string;
}) {
    const [open, setOpen] = useState(false);
    const [place, setPlace] = useState<{ top: number; left: number; list: number } | null>(null);
    const [filter, setFilter] = useState('');
    const rootRef = useRef<HTMLSpanElement>(null);
    const filterRef = useRef<HTMLInputElement>(null);
    const listId = useId();

    const applied = useMemo(
        () => new Set(tags.map((t) => t.toLowerCase())),
        [tags],
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
        && tags.length < TAG_MAX_COUNT;

    useEffect(() => {
        if (!open) return;
        const onDown = (e: MouseEvent) => {
            if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
        };
        document.addEventListener('mousedown', onDown);
        return () => document.removeEventListener('mousedown', onDown);
    }, [open]);

    useEffect(() => {
        if (open) filterRef.current?.focus();
        else setFilter('');
    }, [open]);

    // The panel is FIXED to the viewport, placed once when it opens, and closed
    // the moment anything scrolls.
    //
    // It used to be absolute inside whatever scrolled, following
    // AccountCategoryPicker, whose note argues for staying inside: a dropdown
    // that can be visible while its own input is not ends up floating over
    // unrelated chrome, still taking clicks into a field nobody can see. That
    // reasoning is sound and this answers it differently — by closing on scroll,
    // so the panel can never outlive the view it was opened from.
    //
    // Staying inside cost three rounds of the wrong fix. Absolutely positioned
    // children count toward a scroller's scrollable content, so opening from the
    // last leg of a long split transaction grew the grid's scroll range and left a
    // dead band under "Add split". Before that the panel lost a stacking fight
    // with the grid's sticky header and footer (both z-20) and rendered sliced in
    // half. Neither was the fit problem I kept solving.
    useEffect(() => {
        if (!open) { setPlace(null); return; }
        const el = rootRef.current;
        if (el === null) return;

        const rect = el.getBoundingClientRect();
        // ALWAYS DOWN. No flipping, by request and on the evidence: choosing a
        // side by available room meant two adjacent legs in one grid opened
        // opposite ways, and opening upward needed the panel's height BEFORE it
        // existed — an estimate that assumed a full list and a Create row, so a
        // short list floated two rows above the control, pointing at nothing.
        // Both bugs came from the flip; deleting it deletes them.
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
                const target = e.target;
                if (target instanceof Node && rootRef.current?.contains(target) === true) return;
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
            onChange(tags.filter((t) => t.toLowerCase() !== lower));
        } else if (tags.length < TAG_MAX_COUNT) {
            onChange([...tags, name]);
        }
    };

    const create = () => {
        if (!canCreate) return;
        onChange([...tags, trimmed]);
        setFilter('');
        filterRef.current?.focus();
    };

    return (
        <span ref={rootRef} className={cn('flex items-stretch', compact ? 'shrink-0' : 'min-w-0 flex-1')}>
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
                    compact ? 'h-control-24px w-fixed-128px' : 'h-control-28px min-w-0 flex-1',
                    disabled && 'bg-surface-header',
                )}
            >
                {tags.length === 0 ? (
                    <span className="truncate text-[0.6875rem] text-text-subtle">tags</span>
                ) : (
                    <TagLine tags={tags} />
                )}
            </span>

            <button
                id={id}
                type="button"
                disabled={disabled}
                aria-label={`Edit ${ariaLabel ?? 'tags'}`}
                aria-expanded={open}
                aria-controls={open ? listId : undefined}
                onClick={() => setOpen((o) => !o)}
                className={cn(
                    'flex w-control-20px shrink-0 items-center justify-center rounded-r border border-border bg-surface-header text-text-subtle hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-60',
                    compact ? 'h-control-24px' : 'h-control-28px',
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
                    style={{ top: place.top, left: place.left }}
                    className="fixed z-50 w-fixed-320px overflow-hidden rounded border border-border-strong bg-surface shadow-lg"
                    onKeyDown={(e) => {
                        if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); }
                    }}
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
                                            disabled={!on && tags.length >= TAG_MAX_COUNT}
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
