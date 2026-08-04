import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { cleanupUnusedTags, deleteTag, fetchTags } from '@/lib/api';
import type { TagDto } from '@/lib/types';
import { errorMessage } from '@/lib/errorMessage';
import { cn } from '@/lib/cn';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ContextMenu, type ContextMenuItem } from '@/components/ui/ContextMenu';

import { MergeTagDialog, TagEditDialog } from './TagDialogs';

/**
 * Tags management (Tags v1). The per-ledger tag dictionary that's
 * otherwise only reachable inline in the transaction editor + register
 * filter: every tag with its colour + usage count, and rename / recolour
 * / merge / delete (delete-in-use allowed, confirmed) / remove-unused.
 * Reads + writes the dedicated /tags endpoints (the ledger tag list is
 * the shared ['tags', ledgerId] query the editor, filter, and register
 * chip colours also use, so a change here repaints all of them). Mirrors
 * {@link CategoriesPanel}'s shape (right-click a row for actions).
 */
export function TagsPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const tagsQuery = useQuery({
        queryKey: ['tags', ledgerId],
        queryFn: () => fetchTags(ledgerId),
    });
    const tags = useMemo(() => tagsQuery.data ?? [], [tagsQuery.data]);
    const unusedCount = useMemo(() => tags.filter((t) => t.usageCount === 0).length, [tags]);

    // A tag mutation ripples into the register (row chips read tag names +
    // colours) and the editor/filter autocomplete. ['register', …] is the
    // ADR-0079 canonical key that reloads a mounted register (not a dead key).
    const onSaved = () => {
        void queryClient.invalidateQueries({ queryKey: ['tags', ledgerId] });
        void queryClient.invalidateQueries({ queryKey: ['register', ledgerId] });
    };

    type Dialog =
        | { kind: 'edit'; tag: TagDto }
        | { kind: 'merge'; tag: TagDto; presetTargetId?: string | null };
    const [dialog, setDialog] = useState<Dialog | null>(null);
    const [deleteTarget, setDeleteTarget] = useState<TagDto | null>(null);
    const [menu, setMenu] = useState<{ tag: TagDto; x: number; y: number } | null>(null);

    const deleteMut = useMutation({
        mutationFn: (id: string) => deleteTag(ledgerId, id),
        onSuccess: () => { onSaved(); setDeleteTarget(null); },
    });
    const cleanupMut = useMutation({
        mutationFn: () => cleanupUnusedTags(ledgerId),
        onSuccess: onSaved,
    });

    const menuItems = (tag: TagDto): ContextMenuItem[] => [
        { id: 'edit', label: 'Rename / recolour…',
            onSelect: () => setDialog({ kind: 'edit', tag }) },
        { id: 'merge', label: 'Merge…', disabled: tags.length < 2,
            onSelect: () => setDialog({ kind: 'merge', tag }) },
        { id: 'delete', label: 'Delete', danger: true,
            onSelect: () => setDeleteTarget(tag) },
    ];

    return (
        <Panel>
            <PanelHead>
                <div className="flex w-full items-center justify-between gap-3">
                    <span className="font-medium">Tags</span>
                    {unusedCount > 0 ? (
                        <button
                            type="button"
                            onClick={() => cleanupMut.mutate()}
                            disabled={cleanupMut.isPending}
                            className="rounded px-1.5 py-0.5 text-xs text-text-muted hover:bg-surface-muted hover:text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        >
                            {cleanupMut.isPending ? 'Removing…' : `Remove ${unusedCount} unused`}
                        </button>
                    ) : null}
                </div>
            </PanelHead>
            <PanelBody>
                {tagsQuery.isPending ? (
                    <p className="text-sm text-text-muted">Loading tags…</p>
                ) : tagsQuery.isError ? (
                    <p role="alert" className="text-sm text-state-danger">
                        {errorMessage(tagsQuery.error, 'Could not load tags.')}
                    </p>
                ) : tags.length === 0 ? (
                    <p className="text-sm text-text-subtle">
                        No tags yet. Tags are created as you add them to transactions.
                    </p>
                ) : (
                    <ul className="divide-y divide-border">
                        {tags.map((tag) => (
                            <li
                                key={tag.id}
                                onContextMenu={(e) => {
                                    e.preventDefault();
                                    setMenu({ tag, x: e.clientX, y: e.clientY });
                                }}
                                title="Right-click for actions"
                                className="flex cursor-context-menu items-center justify-between gap-2 py-1.5 pr-1 hover:bg-surface-hover"
                            >
                                <span className="flex min-w-0 items-center gap-2">
                                    <span
                                        aria-hidden
                                        className={cn(
                                            'h-3 w-3 shrink-0 rounded-full border border-border/50',
                                            tag.color ? '' : 'bg-surface-hover',
                                        )}
                                        style={tag.color ? { backgroundColor: tag.color } : undefined}
                                    />
                                    <span className="truncate text-sm text-text">{tag.name}</span>
                                </span>
                                <span
                                    className="shrink-0 text-[0.6875rem] tabular-nums text-text-subtle"
                                    title={`${tag.usageCount} transaction${tag.usageCount === 1 ? '' : 's'}`}
                                >
                                    {tag.usageCount} txns
                                </span>
                            </li>
                        ))}
                    </ul>
                )}
                {cleanupMut.isError ? (
                    <p role="alert" className="mt-2 text-xs text-state-danger">
                        {errorMessage(cleanupMut.error, 'Could not remove unused tags.')}
                    </p>
                ) : null}
            </PanelBody>

            {menu ? (
                <ContextMenu
                    anchor={{ x: menu.x, y: menu.y }}
                    items={menuItems(menu.tag)}
                    onClose={() => setMenu(null)}
                />
            ) : null}

            {dialog?.kind === 'edit' ? (
                <TagEditDialog
                    ledgerId={ledgerId}
                    tag={dialog.tag}
                    allTags={tags}
                    onClose={() => setDialog(null)}
                    onSaved={onSaved}
                    onRequestMerge={(target) =>
                        setDialog({ kind: 'merge', tag: dialog.tag, presetTargetId: target.id })}
                />
            ) : null}
            {dialog?.kind === 'merge' ? (
                <MergeTagDialog
                    ledgerId={ledgerId}
                    tag={dialog.tag}
                    allTags={tags}
                    presetTargetId={dialog.presetTargetId}
                    onClose={() => setDialog(null)}
                    onSaved={onSaved}
                />
            ) : null}

            {deleteTarget ? (
                <ConfirmDialog
                    open
                    variant="danger"
                    title={`Delete “${deleteTarget.name}”?`}
                    confirmLabel="Delete"
                    isConfirming={deleteMut.isPending}
                    body={
                        <>
                            {deleteTarget.usageCount > 0 ? (
                                <>
                                    This tag is on{' '}
                                    <span className="font-medium text-text">
                                        {deleteTarget.usageCount} transaction
                                        {deleteTarget.usageCount === 1 ? '' : 's'}
                                    </span>
                                    ; deleting it removes the tag from{' '}
                                    {deleteTarget.usageCount === 1 ? 'it' : 'them'} (the transactions
                                    themselves are kept).
                                </>
                            ) : (
                                <>This permanently deletes the tag. It isn’t used on any transaction.</>
                            )}
                            {deleteMut.isError ? (
                                <p className="mt-2 text-state-danger">
                                    {errorMessage(deleteMut.error, 'Could not delete the tag.')}
                                </p>
                            ) : null}
                        </>
                    }
                    onConfirm={() => deleteMut.mutate(deleteTarget.id)}
                    onCancel={() => setDeleteTarget(null)}
                />
            ) : null}
        </Panel>
    );
}
