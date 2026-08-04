import { useMemo, useState } from 'react';
import { useMutation } from '@tanstack/react-query';

import { ApiError, mergeTag, patchTag } from '@/lib/api';
import type { PatchTagRequest, TagDto } from '@/lib/types';
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
 *  picking a swatch sets a hex). There's no "clear to gray" on purpose:
 *  the PATCH colour is set-only in v1, so once coloured a tag changes
 *  among swatches. `value` is the current lower-cased hex or null. */
function ColorSwatchPicker({
    value,
    onChange,
}: {
    value: string | null;
    onChange: (color: string) => void;
}) {
    return (
        <div className="mt-1 flex flex-wrap items-center gap-1.5">
            {TAG_PALETTE.map((hex) => (
                <button
                    key={hex}
                    type="button"
                    onClick={() => onChange(hex)}
                    aria-label={`Colour ${hex}`}
                    aria-pressed={value === hex}
                    style={{ backgroundColor: hex }}
                    className={cn(
                        'h-6 w-6 rounded-full border border-black/10',
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
    const [error, setError] = useState<string | null>(null);
    // On a name collision, the existing tag with that name — surfaced so
    // the user can merge into it (decision 2).
    const [collision, setCollision] = useState<TagDto | null>(null);

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
                setCollision(existing ?? null);
                setError(`A tag named “${trimmed}” already exists.`);
                return;
            }
            setError(errorMessage(e, 'Could not save the tag.'));
        },
    });

    function handleSave() {
        setError(null);
        setCollision(null);
        if (trimmed.length === 0) { setError('Name is required.'); return; }
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
                        autoFocus
                        maxLength={64}
                        onChange={(e) => setName(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') handleSave(); }}
                    />
                </div>
                <div>
                    <FieldLabel>Colour</FieldLabel>
                    <ColorSwatchPicker value={color} onChange={setColor} />
                </div>

                {error !== null ? (
                    <div role="alert" className="text-xs text-state-danger">
                        {error}
                        {collision !== null ? (
                            <div className="mt-1">
                                <button
                                    type="button"
                                    className="text-accent underline"
                                    onClick={() => onRequestMerge(collision)}
                                >
                                    Merge “{tag.name}” into “{collision.name}” instead…
                                </button>
                            </div>
                        ) : null}
                    </div>
                ) : null}

                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        disabled={saveMut.isPending}>
                        Cancel
                    </Button>
                    <Button type="button" variant="primary" size="sm" onClick={handleSave}
                        disabled={saveMut.isPending}>
                        {saveMut.isPending ? 'Saving…' : 'Save'}
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
}

export function MergeTagDialog({
    ledgerId, tag, allTags, presetTargetId, onClose, onSaved,
}: MergeTagDialogProps) {
    const targets = useMemo(
        () => allTags.filter((t) => t.id !== tag.id).sort((a, b) => a.name.localeCompare(b.name)),
        [allTags, tag.id],
    );
    const [targetId, setTargetId] = useState<string | null>(presetTargetId ?? null);
    const [error, setError] = useState<string | null>(null);

    const mergeMut = useMutation({
        mutationFn: () => mergeTag(ledgerId, tag.id, { intoTagId: targetId! }),
        onSuccess: () => { onSaved(); onClose(); },
        onError: (e) => setError(errorMessage(e, 'Could not merge the tag.')),
    });

    const target = targets.find((t) => t.id === targetId) ?? null;

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
                                Moves{' '}
                                <span className="font-medium text-text">
                                    {tag.usageCount} assignment{tag.usageCount === 1 ? '' : 's'}
                                </span>{' '}
                                onto <span className="font-medium text-text">{target.name}</span>,
                                then permanently deletes “{tag.name}”. Transactions already carrying
                                “{target.name}” keep a single tag.
                            </p>
                        ) : null}
                    </>
                )}

                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}

                <div className="mt-1 flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}
                        disabled={mergeMut.isPending}>
                        Cancel
                    </Button>
                    <Button type="button" variant="primary" size="sm"
                        onClick={() => mergeMut.mutate()}
                        disabled={mergeMut.isPending || targetId === null}>
                        {mergeMut.isPending ? 'Merging…' : 'Merge'}
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
