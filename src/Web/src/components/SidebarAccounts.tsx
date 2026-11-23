import { useEffect, useMemo, useRef, useState, type DragEvent, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { Link } from '@tanstack/react-router';
import { ChevronDown, ChevronRight, Folder, FolderPlus, X } from 'lucide-react';

import {
    createAccountFolder,
    deleteAccountFolder,
    moveAccountFolder,
    renameAccountFolder,
    setAccountFolder,
} from '@/lib/api';
import {
    folderOfAccount,
    formatTotal,
    moveDestinations,
    PATH_SEPARATOR,
    resolveMove,
    withAccountMoved,
    withFolderMoved,
    type AccountNode,
    type FolderNode,
    type MoveItem,
    type MoveTarget,
    type Total,
    type TreeNode,
    type TypeSection,
} from '@/lib/accountTree';
import { cn } from '@/lib/cn';
import { errorMessage } from '@/lib/errorMessage';
import { rateUnavailableTitle } from '@/lib/conversion';
import { formatCurrency } from '@/lib/money';
import { readClosedFolders, saveClosedFolders } from '@/lib/sidebarRail';
import type { AccountFolder, AccountSummary } from '@/lib/types';
import { SidebarNavLink, SidebarSection } from '@/components/ui/SidebarLayout';
import { IconButton } from '@/components/ui/IconButton';
import { InlineNameInput, type InlineNameEnd } from '@/components/ui/InlineNameInput';
import { RowActionsButton } from '@/components/ui/RowActionsButton';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';
import {
    ContextMenu,
    type ContextMenuAnchor,
    type ContextMenuItem,
} from '@/components/ui/ContextMenu';

// The sidebar's account list (FU-006, FU-052): one section per account type,
// folders nested to any depth inside it, a figure on every row. The tree is
// built in lib/accountTree.ts; this file draws it and owns the folder edits.
//
// Moving an account or a folder, two ways that do the same thing:
//   • Drag it onto a folder, or onto its section heading for the top level.
//     The primary path. A target of another type, or a folder dropped on
//     itself or beneath itself, shows a not-allowed state and does nothing.
//   • Its menu: one "Move to: <path>" per valid folder, and "Move to top
//     level". The keyboard and touch path, since neither can drag.
// A folder is created from the new-folder button on a section heading, or
// "New folder inside…" on a folder's menu.
//
// Every edit is owner/editor only: folders are ledger data, not a view, and the
// API refuses a viewer, so a viewer is never offered one. A move is optimistic:
// the rail shows the result at once and puts it back, with a notice, if the
// server refuses.
//
// Every row is the rail's existing row: SidebarNavLink's type, padding and
// hover, the review dot after the name, the reserved Rule 10 slot at the end,
// and the dashboard's figure (formatCurrency, mono, muted, liabilities
// negative). A folder's contents sit under the same faint left rail that sets
// a section's accounts off from its heading (ADR-0021), one rail per level,
// drawn from the same place: the left edge of the heading's leading glyph.
//
// Focus: an inline name input, a menu and a moved row all unmount the element
// that had focus. Each hands focus on to the row or control the reader was on
// (data-focus-key below), so the keyboard path never drops to <body>.

/** Where an inline name input is open, and where focus goes when it closes. */
type Editing =
    | { mode: 'create'; accountType: string; parentId: string | null; returnTo: string }
    | { mode: 'rename'; folderId: string; returnTo: string };

type DropState = 'valid' | 'invalid';

const DRAG_TYPE = 'text/x-coffer-sidebar-item';

/** The rail's grouped-block device: indent under a faint left border. */
const NESTED_BLOCK = 'ml-2 space-y-px border-l border-border pl-1.5';

/**
 * The same device under a folder, at the same step: the compact indent rather
 * than aligning children under the folder's name, because folders nest without limit in a narrow rail, and the rail's existing step
 * fits about twice the depth before names are cut off.
 */
const FOLDER_BLOCK = NESTED_BLOCK;

/** A figure's type, as the dashboard's account list sets it, rows and subtotals alike. */
const FIGURE_TYPE = 'font-mono text-[0.6875rem] tabular-nums';

/** The figure on every row, exactly as the dashboard's account list renders one. */
const FIGURE = cn('shrink-0 text-text-muted', FIGURE_TYPE);

function targetKey(target: MoveTarget): string {
    return target.kind === 'folder' ? `folder:${target.folderId}` : `section:${target.accountType}`;
}

/** The data-focus-key of a row, or of a section's new-folder button. */
const focusKeyOf = {
    folder: (folderId: string) => `folder:${folderId}`,
    account: (accountId: string) => `account:${accountId}`,
    newFolder: (accountType: string) => `new-folder:${accountType}`,
    item: (item: MoveItem) => `${item.kind}:${item.id}`,
};

export function SidebarAccounts({
    ledgerId,
    sections,
    folders,
    activeAccountId,
    canEdit,
    onToggleActive,
}: {
    ledgerId: string;
    sections: readonly TypeSection[];
    folders: readonly AccountFolder[];
    activeAccountId: string | undefined;
    /** Owner or editor on this ledger. */
    canEdit: boolean;
    /** Deactivate asks first, so the caller owns the dialog. */
    onToggleActive: (account: AccountSummary, active: boolean) => void;
}) {
    const queryClient = useQueryClient();
    const foldersKey = ['account-folders', ledgerId] as const;

    // Folders start open; only the closed ones are remembered, per browser.
    // The parent keys this component by ledger, so this initializer runs again
    // on a ledger switch rather than carrying one ledger's set into another.
    const [closed, setClosed] = useState<ReadonlySet<string>>(() => readClosedFolders(ledgerId));
    const setOpen = (folderId: string, open: boolean) => {
        setClosed((prev) => {
            if (open !== prev.has(folderId)) return prev;
            const next = new Set(prev);
            if (open) next.delete(folderId);
            else next.add(folderId);
            saveClosedFolders(ledgerId, next);
            return next;
        });
    };

    const [notice, setNotice] = useState<string | null>(null);
    const [editing, setEditing] = useState<Editing | null>(null);
    const [menu, setMenu] = useState<
        | { anchor: ContextMenuAnchor; kind: 'account'; account: AccountSummary }
        | { anchor: ContextMenuAnchor; kind: 'folder'; folder: AccountFolder }
        | null
    >(null);
    const [drag, setDrag] = useState<{
        item: MoveItem;
        /** The dragged row's name, for the notice if the server refuses. */
        label: string;
        over: string | null;
        state: DropState | null;
    } | null>(null);

    // Focus hand-off. `pendingFocus` names the row or control that holds focus
    // until the reader moves on; the effect after every render puts focus there
    // whenever it is not. That covers each way the element can arrive or be
    // replaced: a renamed folder's row is back on the next render, a moved row
    // remounts in its new place (and again if the move is put back), and a new
    // folder's row arrives only with the refetch.
    //
    // Any key or pointer press is the reader moving on, and clears it. Those
    // listeners are on the document in the capture phase, so they run before
    // the handler of the very press that starts the next hand-off (Enter in the
    // name input, a menu item's click), which then sets it again.
    const rootRef = useRef<HTMLDivElement | null>(null);
    const [pendingFocus, setPendingFocus] = useState<string | null>(null);
    useEffect(() => {
        // Clearing an already-clear key is a no-op render, so a key press
        // with nothing pending costs nothing.
        const clear = () => setPendingFocus(null);
        document.addEventListener('keydown', clear, true);
        document.addEventListener('pointerdown', clear, true);
        return () => {
            document.removeEventListener('keydown', clear, true);
            document.removeEventListener('pointerdown', clear, true);
        };
    }, []);
    useEffect(() => {
        if (pendingFocus === null) return;
        const el = rootRef.current?.querySelector<HTMLElement>(`[data-focus-key="${pendingFocus}"]`);
        if (el && document.activeElement !== el) el.focus();
    });

    // A row the rail stops drawing while its link or kebab holds focus (an
    // account deactivated while inactive accounts are hidden) hands focus to
    // the next drawn row's link or folder button, the previous one's, or the
    // first section's new-folder button, never to the page (ADR-0023 §L). The
    // rows are read in the order they are drawn: section by section, a
    // folder's rows only while it is open.
    const drawnRows = useMemo(() => {
        const out: string[] = [];
        const walk = (nodes: readonly TreeNode[]) => {
            for (const node of nodes) {
                if (node.kind === 'account') {
                    out.push(focusKeyOf.account(node.account.id));
                } else {
                    out.push(focusKeyOf.folder(node.folder.id));
                    if (!closed.has(node.folder.id)) walk(node.children);
                }
            }
        };
        for (const section of sections) walk(section.children);
        return out;
    }, [sections, closed]);
    const firstNewFolder = useMemo(() => ({
        get current() { return rootRef.current?.querySelector<HTMLElement>('[data-focus-key^="new-folder:"]') ?? null; },
    }), []);
    const rowControl = useRowRemovalFocus(drawnRows, firstNewFolder);

    const invalidateFolders = () => queryClient.invalidateQueries({ queryKey: foldersKey });

    const moveMutation = useMutation({
        mutationFn: async (args: { item: MoveItem; parentId: string | null; label: string }) => {
            if (args.item.kind === 'account') await setAccountFolder(ledgerId, args.item.id, args.parentId);
            else await moveAccountFolder(ledgerId, args.item.id, args.parentId);
        },
        onMutate: async (args) => {
            await queryClient.cancelQueries({ queryKey: foldersKey });
            const previous = queryClient.getQueryData<AccountFolder[]>(foldersKey);
            if (previous !== undefined) {
                queryClient.setQueryData<AccountFolder[]>(
                    foldersKey,
                    args.item.kind === 'account'
                        ? withAccountMoved(previous, args.item.id, args.parentId)
                        : withFolderMoved(previous, args.item.id, args.parentId),
                );
            }
            // Open the destination, so what was moved is still on screen.
            if (args.parentId !== null) setOpen(args.parentId, true);
            setNotice(null);
            return { previous };
        },
        onError: (error, args, context) => {
            if (context?.previous !== undefined) queryClient.setQueryData(foldersKey, context.previous);
            setNotice(`Could not move ${args.label}: ${errorMessage(error)}`);
        },
        onSettled: invalidateFolders,
    });

    const createMutation = useMutation({
        mutationFn: (args: {
            accountType: string;
            parentId: string | null;
            name: string;
            /** The opener focus went back to, when naming ended on a key. */
            returnTo: string | null;
        }) =>
            createAccountFolder(ledgerId, {
                accountType: args.accountType,
                name: args.name,
                parentId: args.parentId,
            }),
        onSuccess: (created, args) => {
            setNotice(null);
            // On from the opener to the new folder's row once the refetch
            // draws it, unless the reader has moved on in the meantime.
            if (args.returnTo !== null) {
                const opener = args.returnTo;
                setPendingFocus((key) => (key === opener ? focusKeyOf.folder(created.id) : key));
            }
        },
        onError: (error, args) => setNotice(`Could not create "${args.name}": ${errorMessage(error)}`),
        onSettled: invalidateFolders,
    });

    const renameMutation = useMutation({
        mutationFn: (args: { folderId: string; name: string }) =>
            renameAccountFolder(ledgerId, args.folderId, { name: args.name }),
        onSuccess: () => setNotice(null),
        onError: (error, args) => setNotice(`Could not rename to "${args.name}": ${errorMessage(error)}`),
        onSettled: invalidateFolders,
    });

    const deleteMutation = useMutation({
        mutationFn: (folder: AccountFolder) => deleteAccountFolder(ledgerId, folder.id),
        onSuccess: () => setNotice(null),
        onError: (error, folder) => setNotice(`Could not delete "${folder.name}": ${errorMessage(error)}`),
        onSettled: invalidateFolders,
    });

    function move(item: MoveItem, target: MoveTarget, label: string) {
        const outcome = resolveMove(item, target, folders);
        if (outcome.kind !== 'move') return;
        moveMutation.mutate({ item, parentId: outcome.parentId, label });
    }

    /** A move chosen from a menu: the moved row remounts, and focus follows it. */
    function moveFromMenu(item: MoveItem, target: MoveTarget, label: string) {
        setPendingFocus(focusKeyOf.item(item));
        move(item, target, label);
    }

    function startCreate(accountType: string, parentId: string | null) {
        if (parentId !== null) setOpen(parentId, true);
        setEditing({
            mode: 'create',
            accountType,
            parentId,
            returnTo: parentId === null ? focusKeyOf.newFolder(accountType) : focusKeyOf.folder(parentId),
        });
    }

    /** Close the name input; after Enter or Escape, focus goes back where it came from. */
    function stopEditing(how: InlineNameEnd) {
        if (how === 'key' && editing !== null) setPendingFocus(editing.returnTo);
        setEditing(null);
    }

    // --- Drag and drop -------------------------------------------------------
    //
    // HTML5 drag and drop, as SplitsGrid does it: the source names itself in
    // dataTransfer with effectAllowed 'move' and fades while it travels, each
    // target decides on dragover whether it accepts, and the state here only
    // drives the highlight. Accepting is preventDefault on dragover; a target
    // that refuses leaves it alone, so the browser shows its own no-drop cursor.

    const endDrag = () => setDrag(null);

    function dragSourceProps(item: MoveItem, label: string) {
        if (!canEdit) return {};
        return {
            draggable: true,
            onDragStart: (e: DragEvent<HTMLElement>) => {
                e.dataTransfer?.setData(DRAG_TYPE, `${item.kind}:${item.id}`);
                if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
                setMenu(null);
                setDrag({ item, label, over: null, state: null });
            },
            onDragEnd: endDrag,
        };
    }

    function dropTargetProps(target: MoveTarget) {
        if (!canEdit) return {};
        const key = targetKey(target);
        return {
            onDragOver: (e: DragEvent<HTMLElement>) => {
                if (drag === null) return;
                const outcome = resolveMove(drag.item, target, folders).kind;
                const state: DropState | null =
                    outcome === 'move' ? 'valid' : outcome === 'invalid' ? 'invalid' : null;
                if (state === 'valid') {
                    e.preventDefault();
                    if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
                } else if (e.dataTransfer) {
                    e.dataTransfer.dropEffect = 'none';
                }
                if (drag.over !== key || drag.state !== state) {
                    setDrag({ ...drag, over: key, state });
                }
            },
            onDragLeave: (e: DragEvent<HTMLElement>) => {
                // dragleave fires for every child crossed; only leaving the
                // target itself ends its highlight.
                const next = e.relatedTarget as Node | null;
                if (next !== null && e.currentTarget.contains(next)) return;
                setDrag((d) => (d === null || d.over !== key ? d : { ...d, over: null, state: null }));
            },
            onDrop: (e: DragEvent<HTMLElement>) => {
                e.preventDefault();
                if (drag !== null) move(drag.item, target, drag.label);
                endDrag();
            },
        };
    }

    function dropState(target: MoveTarget): DropState | null {
        return drag !== null && drag.over === targetKey(target) ? drag.state : null;
    }

    // --- Menus ---------------------------------------------------------------

    function moveItems(item: MoveItem, label: string, inFolder: boolean): ContextMenuItem[] {
        const items: ContextMenuItem[] = moveDestinations(item, folders).map((d) => ({
            id: `move:${d.folderId}`,
            label: `Move to: ${d.path}`,
            onSelect: () => moveFromMenu(item, { kind: 'folder', folderId: d.folderId }, label),
        }));
        if (inFolder) {
            items.push({
                id: 'move:top',
                label: 'Move to top level',
                onSelect: () => moveFromMenu(item, { kind: 'section', accountType: item.accountType }, label),
            });
        }
        return items;
    }

    function accountMenuItems(account: AccountSummary): ContextMenuItem[] {
        if (!canEdit) {
            return [{ id: 'noop', label: 'No actions available', disabled: true, onSelect: () => {} }];
        }
        const item: MoveItem = { kind: 'account', id: account.id, accountType: account.accountType };
        const items = moveItems(item, account.name, folderOfAccount(account.id, folders) !== null);
        // Holdings siblings and Uncategorized never reach the rail, and the API
        // refuses them anyway, so the item is hidden rather than offered.
        if (!account.isSystem) {
            items.push({
                id: 'toggle-active',
                label: account.isActive ? 'Deactivate' : 'Reactivate',
                danger: account.isActive,
                onSelect: () => onToggleActive(account, !account.isActive),
            });
        }
        return items;
    }

    function folderMenuItems(folder: AccountFolder): ContextMenuItem[] {
        const item: MoveItem = { kind: 'folder', id: folder.id, accountType: folder.accountType };
        return [
            {
                id: 'rename',
                label: 'Rename',
                onSelect: () => setEditing({
                    mode: 'rename',
                    folderId: folder.id,
                    returnTo: focusKeyOf.folder(folder.id),
                }),
            },
            {
                id: 'new-inside',
                label: 'New folder inside…',
                onSelect: () => startCreate(folder.accountType, folder.id),
            },
            ...moveItems(item, folder.name, folder.parentId !== null),
            {
                id: 'delete',
                label: 'Delete folder',
                danger: true,
                // Deletes nothing else: what it held moves up one level.
                // Focus goes to the level it moved up to: the parent folder's
                // row, or the section's new-folder button at the top level.
                onSelect: () => {
                    setPendingFocus(folder.parentId === null
                        ? focusKeyOf.newFolder(folder.accountType)
                        : focusKeyOf.folder(folder.parentId));
                    deleteMutation.mutate(folder);
                },
            },
        ];
    }

    // --- Rendering -----------------------------------------------------------

    function renderCreateInput(accountType: string, parentId: string | null) {
        if (editing?.mode !== 'create' || editing.accountType !== accountType || editing.parentId !== parentId) {
            return null;
        }
        return (
            <FolderNameRow>
                <InlineNameInput
                    label="New folder name"
                    initial=""
                    maxLength={80}
                    className="w-full text-[0.8125rem]"
                    onCommit={(name, how) => {
                        createMutation.mutate({
                            accountType,
                            parentId,
                            name,
                            returnTo: how === 'key' ? editing.returnTo : null,
                        });
                        stopEditing(how);
                    }}
                    onCancel={stopEditing}
                />
                <p className="pt-0.5 text-[0.625rem] text-text-subtle">Enter to create, Esc to cancel</p>
            </FolderNameRow>
        );
    }

    function renderNodes(nodes: readonly TreeNode[], accountType: string, parentId: string | null): ReactNode {
        return (
            <>
                {renderCreateInput(accountType, parentId)}
                {nodes.map((node) => {
                    if (node.kind === 'account') {
                        return (
                            <AccountRow
                                key={node.account.id}
                                node={node}
                                ledgerId={ledgerId}
                                linkRef={rowControl(focusKeyOf.account(node.account.id))}
                                kebabRef={rowControl(focusKeyOf.account(node.account.id), 'companion')}
                                active={node.account.id === activeAccountId}
                                dragging={drag?.item.kind === 'account' && drag.item.id === node.account.id}
                                sourceProps={dragSourceProps({
                                    kind: 'account',
                                    id: node.account.id,
                                    accountType: node.account.accountType,
                                }, node.account.name)}
                                onMenu={(anchor) => setMenu({ anchor, kind: 'account', account: node.account })}
                            />
                        );
                    }
                    const { folder } = node;
                    const open = !closed.has(folder.id);
                    const target: MoveTarget = { kind: 'folder', folderId: folder.id };
                    const hasRows = node.children.length > 0
                        || (editing?.mode === 'create' && editing.parentId === folder.id);
                    return (
                        <div key={folder.id} className="space-y-px">
                            {editing?.mode === 'rename' && editing.folderId === folder.id ? (
                                <FolderNameRow>
                                    <InlineNameInput
                                        label="Folder name"
                                        initial={folder.name}
                                        maxLength={80}
                                        className="w-full text-[0.8125rem]"
                                        onCommit={(name, how) => {
                                            if (name !== folder.name) {
                                                renameMutation.mutate({ folderId: folder.id, name });
                                            }
                                            stopEditing(how);
                                        }}
                                        onCancel={stopEditing}
                                    />
                                </FolderNameRow>
                            ) : (
                                <FolderRow
                                    node={node}
                                    rowRef={rowControl(focusKeyOf.folder(folder.id))}
                                    open={open}
                                    onToggle={() => setOpen(folder.id, !open)}
                                    canEdit={canEdit}
                                    dropState={dropState(target)}
                                    dragging={drag?.item.kind === 'folder' && drag.item.id === folder.id}
                                    sourceProps={dragSourceProps({
                                        kind: 'folder',
                                        id: folder.id,
                                        accountType: folder.accountType,
                                    }, folder.name)}
                                    targetProps={dropTargetProps(target)}
                                    onMenu={(anchor) => setMenu({ anchor, kind: 'folder', folder })}
                                />
                            )}
                            {open && hasRows ? (
                                <div className={FOLDER_BLOCK}>
                                    {renderNodes(node.children, accountType, folder.id)}
                                </div>
                            ) : null}
                        </div>
                    );
                })}
            </>
        );
    }

    return (
        <div ref={rootRef}>
            {notice !== null ? (
                <div className="mx-2 mt-1 flex items-start gap-1">
                    <p role="alert" className="min-w-0 flex-1 text-[0.6875rem] text-state-danger">
                        {notice}
                    </p>
                    <IconButton size="sm" aria-label="Dismiss" onClick={() => setNotice(null)}>
                        <X aria-hidden className="size-icon-xs" />
                    </IconButton>
                </div>
            ) : null}

            {sections.map((section) => {
                const Icon = section.icon;
                const target: MoveTarget = { kind: 'section', accountType: section.accountType };
                return (
                    <div key={section.accountType}>
                        <SidebarSection
                            {...dropTargetProps(target)}
                            className={cn('group/section rounded', dropHighlight(dropState(target)))}
                        >
                            <span className="flex items-center gap-1.5">
                                <Icon className="size-icon-xs shrink-0" aria-hidden />
                                <span className="min-w-0 flex-1 truncate">{section.label}</span>
                                {canEdit ? (
                                    // A folder-plus, never a bare "+", which on a
                                    // list of accounts reads as "add account".
                                    // ADR-0021 Rule 10's third answer, drawn as the
                                    // split rows' row-actions button is: a plain
                                    // IconButton, present and reachable by Tab at
                                    // rest, quiet until the heading is hovered or
                                    // focused; on touch it is the only way to a
                                    // top-level folder. -my-1 keeps a 20px button
                                    // from making the heading taller.
                                    <IconButton
                                        size="sm"
                                        aria-label={`New folder in ${section.label}`}
                                        title={`New folder in ${section.label}`}
                                        data-focus-key={focusKeyOf.newFolder(section.accountType)}
                                        onClick={() => startCreate(section.accountType, null)}
                                        className="-my-1 shrink-0 opacity-50 transition-opacity group-hover/section:opacity-100 group-focus-within/section:opacity-100"
                                    >
                                        <FolderPlus aria-hidden className="size-icon-xs" />
                                    </IconButton>
                                ) : null}
                                {/* Lined up with the row figures below: the same
                                    gap, then the width of their reserved slot.
                                    The same type size as those figures, as the
                                    dashboard sets a subtotal over its rows; the
                                    heading's weight and colour carry over. */}
                                <span className="flex shrink-0 items-center gap-2">
                                    <TotalFigure total={section.total} className={FIGURE_TYPE} />
                                    <span aria-hidden className="w-control-20px" />
                                </span>
                            </span>
                        </SidebarSection>
                        {/* Offset each type's rows under a faint left rail so the
                            sections read as distinct blocks (ADR-0021); folders
                            nest the same rail inside it. */}
                        <div className={cn('mb-1', NESTED_BLOCK)}>
                            {renderNodes(section.children, section.accountType, null)}
                        </div>
                    </div>
                );
            })}

            {menu !== null ? (
                <ContextMenu
                    anchor={menu.anchor}
                    items={menu.kind === 'account' ? accountMenuItems(menu.account) : folderMenuItems(menu.folder)}
                    onClose={() => setMenu(null)}
                />
            ) : null}
        </div>
    );
}

/** A drop target's highlight while something hovers it. */
function dropHighlight(state: DropState | null): string | undefined {
    if (state === 'valid') return 'bg-accent-soft ring-2 ring-inset ring-accent';
    if (state === 'invalid') return 'cursor-not-allowed bg-state-danger-soft/40';
    return undefined;
}

/**
 * A section's or a folder's figure: the server's total in the reporting currency
 * (ADR-0114 D7), "no rate" with each missing pair and day on hover when it needs a
 * missing rate (ADR-0114 D9), or nothing while it is not known.
 */
function TotalFigure({ total, className }: { total: Total; className?: string }) {
    const text = formatTotal(total);
    if (text === '') return null;
    const noRate = total.rateUnavailable.length > 0;
    return (
        <span
            className={cn('shrink-0', className, noRate && 'text-text-subtle')}
            title={noRate ? rateUnavailableTitle(total.rateUnavailable) : undefined}
        >
            {text}
        </span>
    );
}

/** Where a folder is being named, laid out as a folder row. */
function FolderNameRow({ children }: { children: ReactNode }) {
    return (
        <div className="flex items-start gap-2 px-2 py-0.5">
            <span aria-hidden className="flex h-control-24px shrink-0 items-center gap-2 text-text-subtle">
                <span className="size-icon-xs" />
                <Folder className="size-icon-sm" />
            </span>
            <div className="min-w-0 flex-1">{children}</div>
        </div>
    );
}

interface DragSourceProps {
    draggable?: boolean;
    onDragStart?: (e: DragEvent<HTMLElement>) => void;
    onDragEnd?: () => void;
}

interface DropTargetProps {
    onDragOver?: (e: DragEvent<HTMLElement>) => void;
    onDragLeave?: (e: DragEvent<HTMLElement>) => void;
    onDrop?: (e: DragEvent<HTMLElement>) => void;
}

/** Whether any account under the folder, at any depth, has rows to review. */
function holdsReview(folder: FolderNode): boolean {
    return folder.children.some((c) =>
        c.kind === 'folder' ? holdsReview(c) : c.account.needsReviewCount > 0,
    );
}

function FolderRow({
    node,
    rowRef,
    open,
    onToggle,
    canEdit,
    dropState,
    dragging,
    sourceProps,
    targetProps,
    onMenu,
}: {
    node: FolderNode;
    /** The row's button, which takes focus when a row before or after it goes. */
    rowRef: (el: HTMLElement | null) => void;
    open: boolean;
    onToggle: () => void;
    canEdit: boolean;
    dropState: DropState | null;
    dragging: boolean;
    sourceProps: DragSourceProps;
    targetProps: DropTargetProps;
    onMenu: (anchor: ContextMenuAnchor) => void;
}) {
    const name = node.folder.name;
    const hidesReview = !open && holdsReview(node);
    return (
        <div className="relative">
            {/* A folder holds no transactions and has no register, so the row
                is a disclosure, not a link: clicking it opens or closes it.
                Never active, so SidebarNavLink gives it the rail's row and
                nothing else. */}
            {/* The ref goes on SidebarNavLink, which gives its own to the child. */}
            <SidebarNavLink ref={rowRef} asChild className={cn('w-full py-0.5 text-left', dragging && 'opacity-40', dropHighlight(dropState))}>
                <button
                    type="button"
                    aria-expanded={open}
                    data-focus-key={focusKeyOf.folder(node.folder.id)}
                    title={node.path.join(PATH_SEPARATOR)}
                    onClick={onToggle}
                    onContextMenu={(e) => {
                        if (!canEdit) return;
                        e.preventDefault();
                        onMenu({ x: e.clientX, y: e.clientY });
                    }}
                    {...sourceProps}
                    {...targetProps}
                >
                    {open ? (
                        <ChevronDown aria-hidden className="size-icon-xs shrink-0 text-text-subtle" />
                    ) : (
                        <ChevronRight aria-hidden className="size-icon-xs shrink-0 text-text-subtle" />
                    )}
                    {/* Closed, the folder hides its accounts, so it carries
                        their review mark, at any depth. Open, the accounts
                        say it themselves. */}
                    <Folder
                        aria-hidden
                        className={cn(
                            'size-icon-sm shrink-0',
                            hidesReview ? 'fill-accent/20 text-accent' : 'text-text-subtle',
                        )}
                    />
                    <span className="min-w-0 flex-1 truncate font-medium text-text">{name}</span>
                    {hidesReview ? <span className="sr-only">contains accounts to review</span> : null}
                    <TotalFigure total={node.total} className={FIGURE} />
                    {/* The reserved Rule 10 slot, as on every account row. */}
                    <span aria-hidden className="size-control-20px shrink-0" />
                </button>
            </SidebarNavLink>
            {/* ADR-0021 Rule 10 puts the kebab on the selected row only, but a
                folder is never selected (it has no page), so the rule would
                leave its menu, the keyboard and touch path for every folder
                edit, behind a right-click. Folder rows therefore always carry
                it, for whoever may edit them. */}
            {canEdit ? (
                <RowActionsButton
                    size="sm"
                    label={`Actions for folder ${name}`}
                    onOpen={onMenu}
                    className="absolute inset-y-0 right-2 my-auto"
                />
            ) : null}
        </div>
    );
}

function AccountRow({
    node,
    ledgerId,
    linkRef,
    kebabRef,
    active,
    dragging,
    sourceProps,
    onMenu,
}: {
    node: AccountNode;
    ledgerId: string;
    /** The row's link and, on the selected row, its kebab: a row the rail
     *  stops drawing hands their focus to the next row's (useRowRemovalFocus). */
    linkRef: (el: HTMLElement | null) => void;
    kebabRef: (el: HTMLElement | null) => void;
    active: boolean;
    dragging: boolean;
    sourceProps: DragSourceProps;
    onMenu: (anchor: ContextMenuAnchor) => void;
}) {
    const { account, balance } = node;
    const reviewCount = account.needsReviewCount;
    // One wording with the home header and the Accounts page: the count is of register
    // entries awaiting review, so no noun to inflect.
    const reviewLabel = `${reviewCount} to review`;
    return (
        <div className="relative">
            {/* The ref goes on SidebarNavLink, which gives its own to the child. */}
            <SidebarNavLink
                ref={linkRef}
                asChild
                active={active}
                // Denser account list (overrides SidebarNavLink's default
                // py-1): the rail-grouped rows read fine tighter and fit more
                // accounts per screen.
                className={cn('py-0.5', dragging && 'opacity-40')}
            >
                <Link
                    to="/ledgers/$ledgerId/accounts/$accountId"
                    params={{ ledgerId, accountId: account.id }}
                    data-focus-key={focusKeyOf.account(account.id)}
                    title={[...node.folderPath, account.name].join(PATH_SEPARATOR)}
                    className="flex w-full items-center gap-2"
                    {...sourceProps}
                    onContextMenu={(e) => {
                        e.preventDefault();
                        onMenu({ x: e.clientX, y: e.clientY });
                    }}
                >
                    {/* The bank-feed review mark per ADR-0021 (present or
                        absent, not a number) is this dot's colour, so a row
                        has one dot, not two. The title carries the count. */}
                    <span
                        className={cn(
                            'shrink-0 rounded-full',
                            reviewCount > 0 ? 'size-dot-sm bg-accent' : 'size-dot-xs bg-text-subtle',
                        )}
                        title={reviewCount > 0 ? reviewLabel : undefined}
                        aria-hidden
                    />
                    {/* Inactive accounts (shown only with "Show inactive")
                        render opacity-60 + strikethrough, so they read as
                        outside the default set. */}
                    <span
                        className={cn(
                            'min-w-0 flex-1 truncate',
                            !account.isActive && 'opacity-60 line-through',
                        )}
                    >
                        {account.name}
                    </span>
                    {reviewCount > 0 ? (
                        <span className="sr-only">{reviewLabel}</span>
                    ) : null}
                    {balance !== undefined ? (
                        // Liabilities are negative, as stored and as the
                        // dashboard shows them; no colour, matching it.
                        <span className={FIGURE}>{formatCurrency(balance.balance, balance.currencyCode)}</span>
                    ) : null}
                    {/* Reserved slot for the row-actions kebab. Present on
                        EVERY row so the names and figures stay in one column,
                        and sized from the same pinned token as the button, so
                        it still lines up at every density. */}
                    <span aria-hidden className="size-control-20px shrink-0" />
                </Link>
            </SidebarNavLink>
            {/* ADR-0021 Rule 10. Only the SELECTED row carries it: always
                visible where it is, so it is discoverable without being
                hover-only, and without a control on every row of a dense rail.
                Right-click still works everywhere, as the accelerator. */}
            {active ? (
                <RowActionsButton
                    ref={kebabRef}
                    size="sm"
                    label={`Actions for ${account.name}`}
                    onOpen={onMenu}
                    className="absolute inset-y-0 right-2 my-auto"
                />
            ) : null}
        </div>
    );
}
