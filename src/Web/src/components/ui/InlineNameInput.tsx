import { useEffect, useRef, useState, type KeyboardEvent } from 'react';

import { cn } from '@/lib/cn';

// InlineNameInput — name a thing in place, where it sits in a list.
//
// Lifted out of the sidebar's tab strip, whose tabs were named this way, when
// the strip went (folders replaced tabs, FU-006) and the folder rows needed the
// same behaviour. One copy, so the two can never disagree about what Enter or a
// blank name does:
//
//   * focused, with its text selected, on mount;
//   * Enter commits and Escape cancels;
//   * blur commits, so clicking away keeps what was typed;
//   * a blank name (after trimming) cancels rather than committing.
//
// The key handler stops propagation: the register's document-level handler
// would otherwise take a typed `n` or an arrow key as its own.
//
// Each callback says how editing ended. The caller unmounts the input either
// way, and focus would drop to <body> with it, so after Enter or Escape the
// caller hands focus back to where the reader was. After a blur it must not:
// focus is already on its way to whatever the reader clicked or tabbed to.

/** How editing ended: a key (Enter or Escape), or focus leaving the input. */
export type InlineNameEnd = 'key' | 'blur';

export interface InlineNameInputProps {
    /** The starting text; empty for a new name. */
    initial: string;
    /** Announced to screen readers, since the input has no visible label. */
    label: string;
    placeholder?: string;
    maxLength?: number;
    /** Called with the trimmed, non-blank name. */
    onCommit: (name: string, how: InlineNameEnd) => void;
    onCancel: (how: InlineNameEnd) => void;
    /** Width and type size, which differ by where the input sits. */
    className?: string;
}

export function InlineNameInput({
    initial,
    label,
    placeholder,
    maxLength,
    onCommit,
    onCancel,
    className,
}: InlineNameInputProps) {
    const [value, setValue] = useState(initial);
    const inputRef = useRef<HTMLInputElement | null>(null);
    // Enter commits and the parent then unmounts the input; a blur arriving on
    // the way out must not commit a second time.
    const doneRef = useRef(false);
    useEffect(() => {
        inputRef.current?.focus();
        inputRef.current?.select();
    }, []);
    function finish(commit: boolean, how: InlineNameEnd) {
        if (doneRef.current) return;
        doneRef.current = true;
        const trimmed = value.trim();
        if (!commit || trimmed.length === 0) onCancel(how);
        else onCommit(trimmed, how);
    }
    function handleKeyDown(e: KeyboardEvent<HTMLInputElement>) {
        e.stopPropagation();
        if (e.key === 'Enter') {
            e.preventDefault();
            finish(true, 'key');
        } else if (e.key === 'Escape') {
            e.preventDefault();
            finish(false, 'key');
        }
    }
    return (
        <input
            ref={inputRef}
            type="text"
            aria-label={label}
            value={value}
            placeholder={placeholder}
            maxLength={maxLength}
            onChange={(e) => setValue(e.target.value)}
            onKeyDown={handleKeyDown}
            onBlur={() => finish(true, 'blur')}
            className={cn(
                'h-control-24px rounded border border-accent bg-surface px-1.5 text-[0.6875rem] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent',
                className,
            )}
        />
    );
}
