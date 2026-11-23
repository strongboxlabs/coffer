import { useMemo, useRef, useState } from 'react';
import { useMutation } from '@/lib/mutation';

import { ApiError, createTag, mergeTag, patchTag } from '@/lib/api';
import type { MergeSummary } from './mergeSummary';
import type { CreateTagRequest, PatchTagRequest, TagDto } from '@/lib/types';
import { ActionFooter } from '@/components/ui/ActionFooter';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Modal } from '@/components/ui/Modal';
import { cn } from '@/lib/cn';
import { errorMessage } from '@/lib/errorMessage';
import { TAG_PALETTE } from '@/lib/tagPalette';

// Tag-management action dialogs (Tags v1). Each dialog owns its mutation
// and calls `onSaved` on success; the host TagsPanel owns cache
// invalidation. Form idiom mirrors CategoryDialogs / AccountEditorDialog
// (ADR-0023): labels above inputs, inline error, [Cancel] [confirm]
// footer.

const inputClass =
    'mt-1 w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text ' +
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent';

/** Palette picker — the fixed swatch set (a tag with no colour reads gray;
 *  picking a swatch sets a hex). Editing has no "clear to gray" on purpose:
 *  the PATCH colour is set-only in v1, so once coloured a tag changes
 *  among swatches. A NEW tag can start with none, so `allowNone` adds that choice, drawn
 *  as the gray a tag with no colour is drawn in. `value` is the current lower-cased hex or null. */
function ColorSwatchPicker({
    value,
    onChange,
    allowNone = false,
}: {
    value: string | null;
    onChange: (color: string | null) => void;
    allowNone?: boolean;
}) {
    return (
        <div className="mt-1 flex flex-wrap items-center gap-1.5">
            {allowNone ? (
                <button
                    type="button"
                    onClick={() => onChange(null)}
                    aria-label="No colour"
                    aria-pressed={value === null}
                    className={cn(
                        'size-swatch-sm rounded-full border border-border bg-surface-hover',
                        value === null
                            ? 'ring-2 ring-accent ring-offset-1 ring-offset-surface'
                            : '',
                    )}
                />
            ) : null}
            {TAG_PALETTE.map((hex) => (
                <button
                    key={hex}
                    type="button"
                    onClick={() => onChange(hex)}
                    aria-label={`Colour ${hex}`}
                    aria-pressed={value === hex}
                    style={{ backgroundColor: hex }}
                    className={cn(
                        'size-swatch-sm rounded-full border border-black/10',
                        value === hex
                            ? 'ring-2 ring-accent ring-offset-1 ring-offset-surface'
                            : '',
                    )}
                />
            ))}
        </div>
    );
}

// ---------------------------------------------------------------------------
// Create
// ---------------------------------------------------------------------------

export interface TagCreateDialogProps {
    ledgerId: string;
    onClose: () => void;
    /** Called with the tag that was made, before the dialog closes. */
    onSaved: (tag: TagDto) => void;
}

/** Make a tag: a name, and a colour if one is wanted. The same rules as a rename. */
export function TagCreateDialog({ ledgerId, onClose, onSaved }: TagCreateDialogProps) {
    const [name, setName] = useState('');
    const [color, setColor] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);
    const trimmed = name.trim();

    const createMut = useMutation({
        mutationFn: () => {
            const body: CreateTagRequest = { name: trimmed };
            if (color !== null) body.color = color;
            return createTag(ledgerId, body);
        },
        onSuccess: (tag) => { onSaved(tag); onClose(); },
        onError: (e) => {
            if (e instanceof ApiError && e.code === 'tag-name-exists') {
                setError(`A tag named “${trimmed}” already exists.`);
                return;
            }
            setError(errorMessage(e, 'Could not create the tag.'));
        },
    });

    // Enter in the name field presses Create, so the press holds it as a click
    // does (ADR-0023 §L). An error stays until a new one replaces it, or the
    // dialog closes on success.
    const createRef = useRef<HTMLButtonElement | null>(null);
    function handleSave() {
        if (trimmed.length === 0) { setError('Name is required.'); return; }
        createMut.mutate();
    }

    return (
        <Modal open onClose={onClose} titleId="tag-create-title" className="max-w-sm">
            <div className="flex flex-col gap-3 p-5">
                <h2 id="tag-create-title" className="text-base font-semibold text-text">
                    New tag
                </h2>
                <div>
                    <FieldLabel htmlFor="tag-create-name">Name</FieldLabel>
                    <input
                        id="tag-create-name"
                        className={inputClass}
                        value={name}
                        maxLength={64}
                        onChange={(e) => setName(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') createRef.current?.click(); }}
                    />
                </div>
                <div>
                    <FieldLabel>Colour</FieldLabel>
                    <ColorSwatchPicker value={color} onChange={setColor} allowNone />
                </div>

                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}

                <ActionFooter
                    className="mt-1"
                    confirmRef={createRef}
                    cancel={{ label: 'Cancel', onClick: onClose, pending: createMut.isPending }}
                    confirm={{
                        label: 'Create',
                        pendingLabel: 'Creating…',
                        onClick: handleSave,
                        pending: createMut.isPending,
                    }}
                />
            </div>
        </Modal>
    );
}

// ---------------------------------------------------------------------------
// Edit (rename + recolor)
// ---------------------------------------------------------------------------

export interface TagEditDialogProps {
    ledgerId: string;
    tag: TagDto;
    /** Full ledger tag list — used to resolve a rename collision to the
     *  existing tag so the dialog can offer a merge. */
    allTags: readonly TagDto[];
    onClose: () => void;
    onSaved: () => void;
    /** The user hit a name collision and chose to merge into the existing
     *  tag instead — the panel opens the merge dialog targeting it. */
    onRequestMerge: (target: TagDto) => void;
}

export function TagEditDialog({
    ledgerId, tag, allTags, onClose, onSaved, onRequestMerge,
}: TagEditDialogProps) {
    const [name, setName] = useState(tag.name);
    const [color, setColor] = useState<string | null>(tag.color);
    // A refusal, with the tag it collided with on a name collision, surfaced
    // so the user can merge into it (ADR-0077 D3). One state, so a new refusal
    // replaces both at once.
    const [refusal, setRefusal] = useState<{ message: string; collision: TagDto | null } | null>(null);

    const trimmed = name.trim();
    const nameChanged = trimmed.length > 0 && trimmed !== tag.name;
    const colorChanged = color !== null && color !== tag.color;

    const saveMut = useMutation({
        mutationFn: () => {
            const body: PatchTagRequest = {};
            if (nameChanged) body.name = trimmed;
            if (colorChanged) body.color = color!;
            return patchTag(ledgerId, tag.id, body);
        },
        onSuccess: () => { onSaved(); onClose(); },
        onError: (e) => {
            if (e instanceof ApiError && e.code === 'tag-name-exists') {
                const existing = allTags.find(
                    (t) => t.id !== tag.id && t.name.toLowerCase() === trimmed.toLowerCase(),
                );
                setRefusal({ message: `A tag named “${trimmed}” already exists.`, collision: existing ?? null });
                return;
            }
            setRefusal({ message: errorMessage(e, 'Could not save the tag.'), collision: null });
        },
    });

    // Enter in the name field presses Save, so the press holds it as a click
    // does (ADR-0023 §L). An error stays shown while a retry is in flight, until
    // its result replaces it or the dialog closes: cleared at the press, the
    // alert above Save would go and move Save from under the pointer.
    const saveRef = useRef<HTMLButtonElement | null>(null);
    function handleSave() {
        if (trimmed.length === 0) { setRefusal({ message: 'Name is required.', collision: null }); return; }
        if (!nameChanged && !colorChanged) { onClose(); return; }
        saveMut.mutate();
    }

    return (
        <Modal open onClose={onClose} titleId="tag-edit-title" className="max-w-sm">
            <div className="flex flex-col gap-3 p-5">
                <h2 id="tag-edit-title" className="text-base font-semibold text-text">
                    Edit tag
                </h2>
                <div>
                    <FieldLabel htmlFor="tag-edit-name">Name</FieldLabel>
                    <input
                        id="tag-edit-name"
                        className={inputClass}
                        value={name}
                        maxLength={64}
                        onChange={(e) => setName(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') saveRef.current?.click(); }}
                    />
                </div>
                <div>
                    <FieldLabel>Colour</FieldLabel>
                    <ColorSwatchPicker value={color} onChange={setColor} />
                </div>

                {refusal !== null ? (
                    <div role="alert" className="text-xs text-state-danger">
                        {refusal.message}
                        {refusal.collision !== null ? (
                            <div className="mt-1">
                                <button
                                    type="button"
                                    className="text-accent underline"
                                    onClick={() => onRequestMerge(refusal.collision!)}
                                >
                                    Merge “{tag.name}” into “{refusal.collision.name}” instead…
                                </button>
                            </div>
                        ) : null}
                    </div>
                ) : null}

                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        pending={saveMut.isPending}>
                        Cancel
                    </Button>
                    <Button ref={saveRef} type="button" variant="primary" size="sm" onClick={handleSave}
                        pending={saveMut.isPending} pendingLabel="Saving…">
                        Save
                    </Button>
                </div>
            </div>
        </Modal>
    );
}

// ---------------------------------------------------------------------------
// Merge
// ---------------------------------------------------------------------------

export interface MergeTagDialogProps {
    ledgerId: string;
    tag: TagDto;
    allTags: readonly TagDto[];
    /** Preselect a target (e.g. from a rename-collision flow). */
    presetTargetId?: string | null;
    onClose: () => void;
    onSaved: () => void;
    /** What the merge did, for the panel's confirmation line. */
    onMerged?: (summary: MergeSummary) => void;
}

export function MergeTagDialog({
    ledgerId, tag, allTags, presetTargetId, onClose, onSaved, onMerged,
}: MergeTagDialogProps) {
    const targets = useMemo(
        () => allTags.filter((t) => t.id !== tag.id).sort((a, b) => a.name.localeCompare(b.name)),
        [allTags, tag.id],
    );
    const [targetId, setTargetId] = useState<string | null>(presetTargetId ?? null);
    const [error, setError] = useState<string | null>(null);

    const target = targets.find((t) => t.id === targetId) ?? null;

    const mergeMut = useMutation({
        mutationFn: () => mergeTag(ledgerId, tag.id, { intoTagId: targetId! }),
        onSuccess: (response) => {
            onMerged?.({
                from: tag.name,
                into: target?.name ?? '',
                assignmentsMoved: response.transactionsRepointed,
                // Counts only the reports this user can see; a member's private
                // report is repointed too, and never named or counted.
                savedReportsRepointed: response.savedReportsRepointed,
            });
            onSaved();
            onClose();
        },
        onError: (e) => setError(errorMessage(e, 'Could not merge the tag.')),
    });

    return (
        <Modal open onClose={onClose} titleId="tag-merge-title" className="max-w-sm">
            <div className="flex flex-col gap-3 p-5">
                <h2 id="tag-merge-title" className="text-base font-semibold text-text">
                    Merge “{tag.name}”
                </h2>

                {targets.length === 0 ? (
                    <p className="text-sm text-text-muted">
                        There’s no other tag to merge into. Create another tag first.
                    </p>
                ) : (
                    <>
                        <div>
                            <FieldLabel htmlFor="tag-merge-target">Merge into</FieldLabel>
                            <select
                                id="tag-merge-target"
                                className={inputClass}
                                value={targetId ?? ''}
                                onChange={(e) => setTargetId(e.target.value || null)}
                            >
                                <option value="">Pick a tag…</option>
                                {targets.map((t) => (
                                    <option key={t.id} value={t.id}>
                                        {t.name}
                                    </option>
                                ))}
                            </select>
                        </div>
                        {target !== null ? (
                            <p className="rounded border border-border bg-surface-muted/40 p-2 text-xs leading-relaxed text-text-muted">
                                {/* The stored pairings, not the transactions a register shows the tag on: the
                                    merge repoints every one, including those behind a line's own tags, and the
                                    confirmation afterwards counts the same. */}
                                Moves{' '}
                                <span className="font-medium text-text">
                                    {tag.usageCount} tag assignment{tag.usageCount === 1 ? '' : 's'}
                                </span>{' '}
                                onto <span className="font-medium text-text">{target.name}</span>,
                                then permanently deletes “{tag.name}”. Transactions already carrying
                                “{target.name}” keep a single tag, and saved reports filtered on
                                “{tag.name}” filter on “{target.name}” instead.
                            </p>
                        ) : null}
                    </>
                )}

                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}

                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        pending={mergeMut.isPending}>
                        Cancel
                    </Button>
                    <Button type="button" variant="primary" size="sm"
                        onClick={() => mergeMut.mutate()}
                        disabled={targetId === null} pending={mergeMut.isPending} pendingLabel="Merging…">
                        Merge
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
