import type { ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';

import { cn } from '@/lib/cn';

// The heading over a group of rows on one of Structure's lists (ADR-0111 D4): the accounts of
// one type, the categories of one kind. One part, so the two lists set the same words in the
// same type, count the rows the same way, and put a figure on the right in the same place.
//
// What differs is the list's own, and is a prop rather than a second heading: a group that can be
// folded away takes a `disclosure` (the Categories kind sections, which are long), and a group that
// has a figure to show takes a `total`. Neither is required. A heading with neither is the plain
// one the Accounts tab uses, whose groups are short.

export interface StructureGroupHeadingProps {
    label: string;
    /** How many rows the group has. Said as `(3)` beside the label. */
    count: number;
    /** A leading mark, already drawn at its size and colour by the caller; decoration only. */
    icon?: ReactNode;
    /** A figure for the group, set outside the toggle: a button that both reads a number and folds
     *  the list is a trap. A `mixed` total is the rail's "mixed" marker, so it takes the rail's
     *  muted colour (ADR-0109 D9) at the heading's weight, where a figure is drawn in full. */
    total?: { text: string; title?: string; mixed?: boolean };
    /** Makes the heading the group's toggle. `controls` is the id of what it folds. */
    disclosure?: {
        expanded: boolean;
        onToggle: () => void;
        controls: string;
        /** The group cannot be folded right now (a search has opened it). */
        disabled?: boolean;
    };
}

export function StructureGroupHeading({ label, count, icon, total, disclosure }: StructureGroupHeadingProps) {
    const words = (
        <>
            {icon}
            {label}
            <span className="text-xs font-normal text-text-subtle">({count})</span>
        </>
    );
    return (
        <div className="flex items-center justify-between gap-2 px-1">
            <h3 className="min-w-0 flex-1 text-sm font-semibold text-text">
                {disclosure ? (
                    <button
                        type="button"
                        onClick={disclosure.onToggle}
                        disabled={disclosure.disabled}
                        aria-expanded={disclosure.expanded}
                        aria-controls={disclosure.controls}
                        className="flex w-full items-center gap-1.5 rounded text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1 disabled:cursor-default"
                    >
                        <ChevronDown
                            className={cn(
                                'size-icon-md shrink-0 text-text-muted transition-transform',
                                disclosure.expanded ? 'rotate-0' : '-rotate-90',
                            )}
                            aria-hidden
                        />
                        {words}
                    </button>
                ) : (
                    <span className="flex items-center gap-1.5">{words}</span>
                )}
            </h3>
            {total ? (
                <span
                    className={cn(
                        'text-sm font-semibold tabular-nums',
                        total.mixed ? 'text-text-subtle' : 'text-text',
                    )}
                    title={total.title}
                >
                    {total.text}
                </span>
            ) : null}
        </div>
    );
}
