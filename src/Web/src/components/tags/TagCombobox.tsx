import { useId, useMemo, useRef, useState } from 'react';

import { cn } from '@/lib/cn';
import type { TagDto } from '@/lib/types';

// Shared single-tag autocomplete (Tags v1). One text input plus a
// suggestion dropdown listing matching ledger tags (colour swatch + name
// + usage count) and — when `allowCreate` and the draft doesn't match an
// existing tag — a "Create '<draft>'" row. Emits ONE chosen name via
// `onCommit`; the caller decides what to do with it (append to a chip
// list in the transaction editor, or set the single register filter).
// It owns no chips and no border, so it embeds either as the trailing
// field inside the editor's chip box or as a standalone filter field —
// styling comes in via `inputClassName`.

const MAX_SUGGESTIONS = 8;

export interface TagComboboxProps {
    /** The ledger's tag dictionary (caller fetches — usually the shared
     *  React Query `['tags', ledgerId]`). */
    tags: readonly TagDto[];
    /** Fired when the user picks a suggestion or the Create row. The name
     *  is trimmed; for an existing tag it's that tag's stored casing. */
    onCommit: (name: string) => void;
    /** Fired on Backspace when the draft is empty — the editor's chip list
     *  uses it to remove the last applied tag (standard chip-input idiom). */
    onBackspaceEmpty?: () => void;
    /** Offer a "Create '<draft>'" row for a non-matching draft. Editor:
     *  true (create-on-first-use). Filter: false (filter existing only). */
    allowCreate?: boolean;
    /** Names to hide from suggestions (case-insensitive) — e.g. tags
     *  already applied to the transaction being edited. */
    excludeNames?: readonly string[];
    placeholder?: string;
    disabled?: boolean;
    autoFocus?: boolean;
    'aria-label'?: string;
    /** Classes for the <input> so it matches its host (borderless inside
     *  the editor chip box; a bordered field in the filter popover). */
    inputClassName?: string;
    /** Max chars accepted before commit (mirrors the server cap). */
    maxLength?: number;
}

export function TagCombobox({
    tags,
    onCommit,
    onBackspaceEmpty,
    allowCreate = true,
    excludeNames,
    placeholder,
    disabled,
    autoFocus,
    'aria-label': ariaLabel,
    inputClassName,
    maxLength = 64,
}: TagComboboxProps) {
    const listId = useId();
    const [draft, setDraft] = useState('');
    const [open, setOpen] = useState(false);
    const [highlight, setHighlight] = useState(0);
    const inputRef = useRef<HTMLInputElement>(null);

    const excludeLower = useMemo(
        () => new Set((excludeNames ?? []).map((n) => n.toLowerCase())),
        [excludeNames],
    );

    const trimmed = draft.trim();
    const draftLower = trimmed.toLowerCase();

    const matches = useMemo(() => {
        const filtered = tags.filter((t) => {
            const lower = t.name.toLowerCase();
            if (excludeLower.has(lower)) return false;
            return draftLower === '' || lower.includes(draftLower);
        });
        return filtered.slice(0, MAX_SUGGESTIONS);
    }, [tags, excludeLower, draftLower]);

    // A Create row only when the draft is non-empty, creation is allowed,
    // and no existing tag matches it exactly (case-insensitive).
    const exactExists = useMemo(
        () => tags.some((t) => t.name.toLowerCase() === draftLower),
        [tags, draftLower],
    );
    const showCreate = allowCreate && trimmed.length > 0 && !exactExists;

    // Flat option list the keyboard + click share: existing matches then
    // the optional create row (represented as `null`).
    const options: Array<TagDto | null> = useMemo(
        () => (showCreate ? [...matches, null] : matches),
        [matches, showCreate],
    );

    const commit = (name: string) => {
        const value = name.trim();
        if (value.length === 0 || value.length > maxLength) return;
        onCommit(value);
        setDraft('');
        setOpen(false);
        setHighlight(0);
    };

    const commitOption = (index: number) => {
        const opt = options[index];
        if (opt === undefined) return;
        commit(opt === null ? trimmed : opt.name);
    };

    const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
        if (e.key === 'ArrowDown') {
            e.preventDefault();
            if (!open) setOpen(true);
            setHighlight((h) => Math.min(h + 1, Math.max(options.length - 1, 0)));
            return;
        }
        if (e.key === 'ArrowUp') {
            e.preventDefault();
            setHighlight((h) => Math.max(h - 1, 0));
            return;
        }
        if (e.key === 'Enter' || e.key === ',') {
            e.preventDefault();
            if (open && options.length > 0) {
                commitOption(Math.min(highlight, options.length - 1));
            } else if (allowCreate) {
                commit(trimmed);
            }
            return;
        }
        if (e.key === 'Escape') {
            setOpen(false);
            return;
        }
        if (e.key === 'Backspace' && draft.length === 0) {
            onBackspaceEmpty?.();
        }
    };

    return (
        <div className="relative min-w-0 flex-1">
            <input
                ref={inputRef}
                type="text"
                role="combobox"
                aria-expanded={open}
                aria-controls={listId}
                aria-autocomplete="list"
                aria-label={ariaLabel}
                value={draft}
                disabled={disabled}
                autoFocus={autoFocus}
                maxLength={maxLength}
                placeholder={placeholder}
                onChange={(e) => {
                    setDraft(e.target.value);
                    setOpen(true);
                    setHighlight(0);
                }}
                onFocus={() => setOpen(true)}
                onBlur={() => setOpen(false)}
                onKeyDown={handleKeyDown}
                className={inputClassName}
            />
            {open && options.length > 0 ? (
                <ul
                    id={listId}
                    role="listbox"
                    className="absolute left-0 top-full z-40 mt-1 max-h-56 w-56 max-w-[16rem] overflow-auto rounded border border-border bg-surface py-1 text-xs shadow-lg"
                >
                    {options.map((opt, i) => {
                        const isCreate = opt === null;
                        const key = isCreate ? '__create__' : opt.id;
                        return (
                            <li key={key} role="option" aria-selected={i === highlight}>
                                <button
                                    type="button"
                                    // Keep focus on the input so the blur-close
                                    // doesn't fire before the click commits.
                                    onMouseDown={(e) => e.preventDefault()}
                                    onMouseEnter={() => setHighlight(i)}
                                    onClick={() => commitOption(i)}
                                    className={cn(
                                        'flex w-full items-center gap-2 px-2 py-1 text-left',
                                        i === highlight ? 'bg-surface-hover' : 'hover:bg-surface-hover',
                                    )}
                                >
                                    {isCreate ? (
                                        <span className="truncate text-accent">
                                            Create “{trimmed}”
                                        </span>
                                    ) : (
                                        <>
                                            <span
                                                aria-hidden
                                                className={cn(
                                                    'h-2.5 w-2.5 shrink-0 rounded-full border border-border/50',
                                                    opt.color ? '' : 'bg-surface-hover',
                                                )}
                                                style={opt.color ? { backgroundColor: opt.color } : undefined}
                                            />
                                            <span className="min-w-0 flex-1 truncate text-text">
                                                {opt.name}
                                            </span>
                                            <span className="shrink-0 tabular-nums text-text-subtle">
                                                {opt.usageCount}
                                            </span>
                                        </>
                                    )}
                                </button>
                            </li>
                        );
                    })}
                </ul>
            ) : null}
        </div>
    );
}
