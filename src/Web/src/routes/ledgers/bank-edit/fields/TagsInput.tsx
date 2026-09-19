/**
 * Extracted from TxnRowEdit.tsx during the Slice 3 decomposition. Presentational
 * only: it holds no draft state and fetches nothing — the shell owns the query
 * and hands results down as readonly props, per the boundary rule in
 * ../README.md.
 */

import { TagCombobox } from '@/components/tags/TagCombobox';
import type { TagDto } from '@/lib/types';

// --------------------------------------------------------------------
// TagsInput (slice 2c.6b; Tags v1 autocomplete)
// --------------------------------------------------------------------
// Chip-style header-level tag editor. Applied tags render as inline
// chips with an × to remove; the trailing field is a shared
// {@link TagCombobox} that autocompletes against the ledger's tag
// dictionary (colour swatch + usage) and offers "Create '<new>'" for a
// fresh name. Tag matching is case-insensitive within the ledger (server
// enforces) so the SPA dedupes against applied chips with a lower-case
// key. Constraints mirror the server validation (BusinessError codes
// `transaction-tag-{empty,too-long}` and `transaction-tags-too-many`):
// names are trimmed, whitespace-only is rejected, and the field is
// disabled once the cap is reached.

const TAG_MAX_LENGTH = 64;
const TAG_MAX_COUNT = 20;

export function TagsInput({
    label,
    tags,
    allTags,
    onChange,
    disabled,
    'aria-label': ariaLabel,
}: {
    label?: string;
    /** Tag names currently applied to this transaction. */
    tags: readonly string[];
    /** The ledger's tag dictionary — powers the autocomplete. */
    allTags: readonly TagDto[];
    onChange: (next: readonly string[]) => void;
    disabled: boolean;
    'aria-label'?: string;
}) {
    // Add a name chosen from the combobox (an existing tag or a freshly
    // typed one): dedupe case-insensitively + honour the same length /
    // count caps the server enforces.
    const addName = (name: string) => {
        const trimmed = name.trim();
        if (trimmed.length === 0 || trimmed.length > TAG_MAX_LENGTH) return;
        const lower = trimmed.toLowerCase();
        if (tags.some((t) => t.toLowerCase() === lower)) return;
        if (tags.length >= TAG_MAX_COUNT) return;
        onChange([...tags, trimmed]);
    };

    const removeAt = (index: number) => onChange(tags.filter((_, i) => i !== index));

    const atCap = tags.length >= TAG_MAX_COUNT;

    return (
        <label
            className="flex min-w-0 flex-col gap-1 text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted"
            aria-label={ariaLabel}
        >
            {label !== undefined ? <span>{label}</span> : null}
            <div
                className="flex min-h-control-28px min-w-0 flex-wrap items-center gap-1 rounded border border-border bg-surface px-1 py-0.5 text-xs focus-within:outline-none focus-within:ring-2 focus-within:ring-accent"
            >
                {tags.map((tag, i) => (
                    <span
                        key={`${tag.toLowerCase()}_${i}`}
                        className="inline-flex items-center gap-1 rounded bg-surface-muted px-1.5 py-0.5 text-[0.6875rem] text-text"
                    >
                        {tag}
                        <button
                            type="button"
                            onClick={() => removeAt(i)}
                            disabled={disabled}
                            aria-label={`Remove tag ${tag}`}
                            className="text-text-subtle hover:text-state-danger focus-visible:text-state-danger focus-visible:outline-none"
                        >
                            ×
                        </button>
                    </span>
                ))}
                <TagCombobox
                    tags={allTags}
                    excludeNames={tags}
                    onCommit={addName}
                    onBackspaceEmpty={() => { if (tags.length > 0) onChange(tags.slice(0, -1)); }}
                    disabled={disabled || atCap}
                    placeholder={tags.length === 0 ? 'Add tag…' : atCap ? '' : '+ tag'}
                    aria-label="Add tag"
                    maxLength={TAG_MAX_LENGTH}
                    inputClassName="min-w-[3rem] flex-1 border-none bg-transparent px-1 text-xs focus:outline-none disabled:cursor-not-allowed"
                />
            </div>
        </label>
    );
}
