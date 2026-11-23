import { useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { Link, useNavigate } from '@tanstack/react-router';

import { cleanupUnusedTags, deleteTag, fetchTags } from '@/lib/api';
import type { TagDto } from '@/lib/types';
import { errorMessage } from '@/lib/errorMessage';
import { cn } from '@/lib/cn';
import { invalidateHomeReads } from '@/lib/registerInvalidation';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ContextMenu, type ContextMenuItem } from '@/components/ui/ContextMenu';
import { Panel } from '@/components/ui/Panel';
import { useFocusHandOff, useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';

import { matchesQuery, normalizeQuery, rowLinkClass, transactionCountLabel } from '../structure/rowStyles';
import { StructureRow } from '../structure/StructureRow';
import { ActionError, ListEmpty, ListError, ListLoading, ListNoMatches } from '../structure/StructureStates';
import { StructureToolbar } from '../structure/StructureToolbar';
import { useRowMenu } from '../structure/useRowMenu';
import { MergeTagDialog, TagCreateDialog, TagEditDialog } from './TagDialogs';
import { mergeSummaryText } from './mergeSummary';

/**
 * The Tags tab of Structure (Tags v1, ADR-0111). The per-ledger tag dictionary that's
 * otherwise only reachable inline in the transaction editor + register
 * filter: every tag with its colour + how many transactions carry it, and create / rename
 * / recolour / merge / delete (delete-in-use allowed, confirmed) / remove-unused. A tag's
 * name opens the list of its transactions.
 * Reads + writes the dedicated /tags endpoints (the ledger tag list is
 * the shared ['tags', ledgerId] query the editor, filter, and register
 * chip colours also use, so a change here repaints all of them). It uses the toolbar, row,
 * menu and states the other two tabs use.
 */
export function TagsPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const navigate = useNavigate();
    const { canEdit } = useLedgerRole(ledgerId);
    const [query, setQuery] = useState('');
    const tagsQuery = useQuery({
        queryKey: ['tags', ledgerId],
        queryFn: () => fetchTags(ledgerId),
    });
    const tags = useMemo(() => tagsQuery.data ?? [], [tagsQuery.data]);
    const unusedCount = useMemo(() => tags.filter((t) => t.usageCount === 0).length, [tags]);
    const q = normalizeQuery(query);
    const shown = useMemo(() => tags.filter((t) => matchesQuery(q, t.name)), [tags, q]);

    // A tag mutation ripples into the register (row chips read tag names +
    // colours) and the editor/filter autocomplete. ['register', …] is the
    // ADR-0079 canonical key that reloads a mounted register (not a dead key).
    const onSaved = () => {
        void queryClient.invalidateQueries({ queryKey: ['tags', ledgerId] });
        void queryClient.invalidateQueries({ queryKey: ['register', ledgerId] });
        // What a tag's transactions list shows is the same ledger data.
        void queryClient.invalidateQueries({ queryKey: ['tag-transactions', ledgerId] });
        // The home screen's recent and to-review rows draw each row's tags by name and colour.
        invalidateHomeReads(queryClient, ledgerId);
    };

    type Dialog =
        | { kind: 'create' }
        | { kind: 'edit'; tag: TagDto }
        | { kind: 'merge'; tag: TagDto; presetTargetId?: string | null };
    const [dialog, setDialog] = useState<Dialog | null>(null);
    // The confirmation of the last merge, until the next action replaces it.
    const [mergeNotice, setMergeNotice] = useState<string | null>(null);
    const [deleteTarget, setDeleteTarget] = useState<TagDto | null>(null);
    const rowMenu = useRowMenu<TagDto>();
    // A tag deleted or merged away takes its row's kebab with it: focus goes to
    // the next row's, the previous row's, or New tag (ADR-0023 §L).
    const newTagRef = useRef<HTMLButtonElement | null>(null);
    const rowControl = useRowRemovalFocus(useMemo(() => shown.map((t) => t.id), [shown]), newTagRef);
    // Remove N unused goes once its cleanup has left nothing unused: focus
    // goes on to New tag, beside it, not to the page (ADR-0023 §L).
    const cleanupControl = useFocusHandOff(() => newTagRef.current);

    const deleteMut = useMutation({
        mutationFn: (id: string) => deleteTag(ledgerId, id),
        onSuccess: () => { onSaved(); setDeleteTarget(null); },
    });
    const cleanupMut = useMutation({
        mutationFn: () => cleanupUnusedTags(ledgerId),
        onSuccess: onSaved,
    });

    const openTransactions = (tag: TagDto) => void navigate({
        to: '/ledgers/$ledgerId/tags/$tagId',
        params: { ledgerId, tagId: tag.id },
    });
    const menuItems = (tag: TagDto): ContextMenuItem[] => [
        { id: 'open', label: 'Open transactions', onSelect: () => openTransactions(tag) },
        { id: 'edit', label: 'Edit…',
            onSelect: () => setDialog({ kind: 'edit', tag }) },
        { id: 'merge', label: 'Merge…', disabled: tags.length < 2,
            onSelect: () => setDialog({ kind: 'merge', tag }) },
        { id: 'delete', label: 'Delete', danger: true,
            onSelect: () => { deleteMut.reset(); setDeleteTarget(tag); } },
    ];

    const loading = tagsQuery.isPending;
    const failed = tagsQuery.isError;

    return (
        <div className="space-y-4">
            <StructureToolbar
                noun="tags"
                query={query}
                onQuery={setQuery}
                createLabel={canEdit ? 'New tag' : undefined}
                onCreate={canEdit ? () => setDialog({ kind: 'create' }) : undefined}
                createRef={newTagRef}
                disabled={failed}
                pending={loading}
            >
                {canEdit && unusedCount > 0 ? (
                    <Button
                        ref={cleanupControl}
                        type="button"
                        variant="secondary"
                        size="sm"
                        onClick={() => cleanupMut.mutate()}
                        pending={cleanupMut.isPending}
                        pendingLabel="Removing…"
                    >
                        {`Remove ${unusedCount} unused`}
                    </Button>
                ) : null}
            </StructureToolbar>

            {mergeNotice !== null ? (
                <p role="status" className="rounded border border-border bg-surface-muted/40 px-2 py-1 text-xs text-text-muted">
                    {mergeNotice}
                </p>
            ) : null}
            {cleanupMut.isError ? (
                <ActionError message={errorMessage(cleanupMut.error, 'Could not remove unused tags.')} />
            ) : null}

            {failed ? (
                <ListError
                    message={errorMessage(tagsQuery.error, 'Could not load tags.')}
                    onRetry={() => void tagsQuery.refetch()}
                />
            ) : loading ? (
                <ListLoading noun="tags" />
            ) : tags.length === 0 ? (
                <ListEmpty
                    message="No tags yet"
                    hint="Tags are labels you can put on any transaction, across accounts."
                    action={canEdit ? (
                        <Button type="button" variant="primary" size="sm" onClick={() => setDialog({ kind: 'create' })}>
                            New tag
                        </Button>
                    ) : undefined}
                />
            ) : shown.length === 0 ? (
                <ListNoMatches query={query} noun="tag" onClear={() => setQuery('')} />
            ) : (
                <Panel>
                    <ul className="divide-y divide-border">
                        {shown.map((tag) => (
                            <StructureRow
                                key={tag.id}
                                name={(
                                    <>
                                        <span
                                            aria-hidden
                                            className={cn(
                                                'size-dot-lg shrink-0 rounded-full border border-border/50',
                                                tag.color ? '' : 'bg-surface-hover',
                                            )}
                                            style={tag.color ? { backgroundColor: tag.color } : undefined}
                                        />
                                        <Link
                                            to="/ledgers/$ledgerId/tags/$tagId"
                                            params={{ ledgerId, tagId: tag.id }}
                                            className={rowLinkClass()}
                                        >
                                            {tag.name}
                                        </Link>
                                    </>
                                )}
                                count={transactionCountLabel(tag.transactionCount)}
                                menu={canEdit
                                    ? { label: `Actions for ${tag.name}`, onOpen: (anchor) => rowMenu.open(tag, anchor) }
                                    : undefined}
                                menuControl={rowControl(tag.id)}
                            />
                        ))}
                    </ul>
                </Panel>
            )}

            {rowMenu.menu ? (
                <ContextMenu
                    anchor={rowMenu.menu.anchor}
                    label={`Actions for ${rowMenu.menu.row.name}`}
                    items={menuItems(rowMenu.menu.row)}
                    onClose={rowMenu.close}
                />
            ) : null}

            {dialog?.kind === 'create' ? (
                <TagCreateDialog
                    ledgerId={ledgerId}
                    onClose={() => setDialog(null)}
                    onSaved={onSaved}
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
                    onMerged={(summary) => setMergeNotice(mergeSummaryText(summary))}
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
                            {deleteTarget.transactionCount > 0 ? (
                                <>
                                    This tag is on{' '}
                                    <span className="font-medium text-text">
                                        {transactionCountLabel(deleteTarget.transactionCount)}
                                    </span>
                                    ; deleting it removes it from{' '}
                                    {deleteTarget.transactionCount === 1 ? 'it' : 'them'} (the transactions
                                    themselves are kept). A side left with no tags of its own then
                                    shows the other side’s as inherited.
                                </>
                            ) : deleteTarget.usageCount > 0 ? (
                                <>
                                    No register shows this tag on a transaction. It is on hidden or
                                    merged-away transactions, on reminders, or behind a line’s own
                                    tags; deleting it removes it from all of those.
                                </>
                            ) : (
                                <>This permanently deletes the tag. It isn’t used on any transaction.</>
                            )}
                            {deleteMut.isError ? (
                                <p role="alert" className="mt-2 text-state-danger">
                                    {errorMessage(deleteMut.error, 'Could not delete the tag.')}
                                </p>
                            ) : null}
                        </>
                    }
                    onConfirm={() => deleteMut.mutate(deleteTarget.id)}
                    onCancel={() => setDeleteTarget(null)}
                />
            ) : null}
        </div>
    );
}
