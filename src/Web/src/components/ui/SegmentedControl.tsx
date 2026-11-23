import type { ReactNode } from 'react';

import { cn } from '@/lib/cn';

import { usePendingButtonProps } from './pending';

// SegmentedControl — one choice from a few, shown side by side (was three
// hand-rolled toggles: Reports' View and Bars, and the account picker's
// All / Accounts / Categories filter, none with a focus ring). A group of
// `aria-pressed` buttons, the pattern those toggles already used, so a screen
// reader hears "View, group; Table, toggle button, pressed".

export interface SegmentedOption<T extends string> {
    value: T;
    label: ReactNode;
    /** Trailing, quieter text: a keyboard hint such as "⌥1". */
    hint?: ReactNode;
}

export interface SegmentedControlProps<T extends string> {
    /** The group's accessible name ("View", "Columns"). */
    label: string;
    options: readonly SegmentedOption<T>[];
    value: T;
    onChange: (value: T) => void;
    /** `sm` sits in a toolbar or a popover; `md` is a 28px control in a form row. */
    size?: 'sm' | 'md';
    /**
     * Leave focus where it is. A popover over a text input (the account picker)
     * needs typing to keep working after a click, so a mouse press neither
     * focuses the button nor blurs the input; the keyboard path still works.
     */
    keepFocus?: boolean;
    /**
     * A request the choice belongs to is in flight: every option keeps focus
     * and refuses to be chosen, from a press, Enter or Space, until it is not
     * (pending.ts, ADR-0023 §L).
     */
    pending?: boolean;
    className?: string;
}

export function SegmentedControl<T extends string>({
    label,
    options,
    value,
    onChange,
    size = 'sm',
    keepFocus = false,
    pending,
    className,
}: SegmentedControlProps<T>) {
    const pendingProps = usePendingButtonProps();
    return (
        <div
            role="group"
            aria-label={label}
            className={cn('inline-flex items-center gap-0.5 rounded border border-border p-0.5', className)}
        >
            {options.map((o) => {
                const selected = o.value === value;
                return (
                    <button
                        key={o.value}
                        type="button"
                        aria-pressed={selected}
                        onMouseDown={keepFocus
                            ? (e) => {
                                e.preventDefault();
                                // Pending: the press chooses nothing, as its click does not.
                                if (pending !== true) onChange(o.value);
                            }
                            : undefined}
                        // With keepFocus the press already chose; a click with no
                        // pointer behind it (Enter or Space) still has to.
                        {...pendingProps(pending, (e) => { if (!keepFocus || e.detail === 0) onChange(o.value); })}
                        className={cn(
                            'whitespace-nowrap rounded font-medium',
                            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1',
                            // Pending looks as disabled does and keeps its pointer
                            // events, so it shows no hover (pending.ts).
                            'aria-disabled:cursor-default aria-disabled:opacity-50',
                            size === 'md' ? 'h-control-28px px-3 text-[0.8125rem]' : 'px-2 py-0.5 text-xs',
                            selected
                                ? 'bg-accent-soft text-accent-soft-text'
                                : 'text-text-muted hover:bg-surface-hover hover:text-text aria-disabled:hover:bg-transparent aria-disabled:hover:text-text-muted',
                        )}
                    >
                        {o.label}
                        {o.hint !== undefined ? <span className="ml-1 opacity-60">{o.hint}</span> : null}
                    </button>
                );
            })}
        </div>
    );
}
