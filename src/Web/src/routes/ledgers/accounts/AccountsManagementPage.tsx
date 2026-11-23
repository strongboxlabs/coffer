import { useMemo, useRef, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearch } from '@tanstack/react-router';

import { fetchAccounts } from '@/lib/api';
import type { AccountSummary } from '@/lib/types';
import { errorMessage } from '@/lib/errorMessage';
import { useLedgerRole } from '@/lib/useLedgerRole';
import { Button } from '@/components/ui/Button';
import { ContextMenu, type ContextMenuItem } from '@/components/ui/ContextMenu';
import { Panel } from '@/components/ui/Panel';
import { useRowRemovalFocus } from '@/components/ui/rowRemovalFocus';

import { DeactivateConfirm } from '../structure/DeactivateConfirm';
import { matchesQuery, normalizeQuery, rowLinkClass } from '../structure/rowStyles';
import { StructureGroupHeading } from '../structure/StructureGroupHeading';
import { StructureBadge, StructureRow } from '../structure/StructureRow';
import { ActionError, ListEmpty, ListError, ListLoading, ListNoMatches } from '../structure/StructureStates';
import { StructureToolbar } from '../structure/StructureToolbar';
import { useRowMenu } from '../structure/useRowMenu';
import { useSetActive } from '../structure/useSetActive';
import { AccountEditorDialog } from './AccountEditorDialog';
import type { AccountsSearch } from './accountsSearch';

// The Accounts tab of Structure (ADR-0050, ADR-0111) — accounts grouped by type, on the same
// toolbar, row and menu as the other two tabs. "Show inactive" starts off, matching the
// sidebar. Create and edit go through AccountEditorDialog, for a reader who can edit; a
// system account (Holdings siblings are filtered server-side; Uncategorized etc. carry
// is_system) is read-only. The trail, heading and tab strip are the Structure layout's.

// Display order + labels for the type sections. Real accounts only —
// categories are NOT managed here; they get their own tab (ADR-0050).
const GROUPS: ReadonlyArray<{ type: string; label: string }> = [
    { type: 'bank', label: 'Banking' },
    { type: 'credit_card', label: 'Credit cards' },
    { type: 'investment', label: 'Investments' },
    { type: 'asset', label: 'Assets' },
    { type: 'liability', label: 'Liabilities' },
    { type: 'loan', label: 'Loans' },
];
const REAL_TYPES: ReadonlySet<string> = new Set(GROUPS.map((g) => g.type));

type EditorTarget = { mode: 'create' } | { mode: 'edit'; account: AccountSummary };

export function AccountsManagementPage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const navigate = useNavigate();
    const { canEdit } = useLedgerRole(ledgerId);
    const [editor, setEditor] = useState<EditorTarget | null>(null);
    const [showInactive, setShowInactive] = useState(false);
    const [query, setQuery] = useState('');
    const [deactivating, setDeactivating] = useState<AccountSummary | null>(null);
    const rowMenu = useRowMenu<AccountSummary>();
    const setActive = useSetActive(ledgerId);

    // `?edit=<id>` opens that account's editor, as the row's Edit… does: a link elsewhere
    // (the consistency check's advisories) names the account whose figure or currency it
    // is about, inactive or not. The param holds the dialog open, so closing it clears
    // the param, and Back does not reopen it. An id that names no account this tab
    // edits opens nothing and says nothing, and for a viewer it opens nothing either.
    const search = useSearch({ strict: false }) as AccountsSearch;
    const linkedQuery = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: true }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: true }),
        enabled: canEdit && search.edit !== undefined,
    });
    const linked = canEdit && search.edit !== undefined
        ? linkedQuery.data?.find((a) => a.id === search.edit && REAL_TYPES.has(a.accountType) && !a.isSystem) ?? null
        : null;
    // The tab has no other search to keep, so clearing it is clearing the param.
    const closeLinked = () => void navigate({ to: '.', search: {}, replace: true });

    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: showInactive }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: showInactive }),
        // Ticking "Show inactive" changes the key. Keeping the rows already drawn until the new
        // ones land means the list does not blink to a skeleton and the toolbar, with the box the
        // reader is standing on, is not disabled under them.
        placeholderData: keepPreviousData,
    });

    // The accounts read also carries every category; this tab lists the real accounts.
    const accounts = useMemo<AccountSummary[]>(
        () => (accountsQuery.data ?? []).filter((a) => REAL_TYPES.has(a.accountType)),
        [accountsQuery.data],
    );
    const q = normalizeQuery(query);
    const groups = useMemo(
        () => GROUPS
            .map((g) => ({
                ...g,
                rows: accounts.filter((a) =>
                    a.accountType === g.type && matchesQuery(q, a.name, a.institutionName, a.currencyCode)),
            }))
            .filter((g) => g.rows.length > 0),
        [accounts, q],
    );

    // An account deactivated while inactive ones are hidden leaves the list, and
    // its row's kebab with it: focus goes to the next row's, the previous row's,
    // or New account (ADR-0023 §L).
    const newAccountRef = useRef<HTMLButtonElement | null>(null);
    const rowControl = useRowRemovalFocus(
        useMemo(() => groups.flatMap((g) => g.rows.map((a) => a.id)), [groups]),
        newAccountRef,
    );

    const openRegister = (a: AccountSummary) => void navigate({
        to: '/ledgers/$ledgerId/accounts/$accountId',
        params: { ledgerId, accountId: a.id },
    });
    const menuItems = (a: AccountSummary): ContextMenuItem[] => [
        { id: 'open', label: 'Open register', onSelect: () => openRegister(a) },
        { id: 'edit', label: 'Edit…', onSelect: () => setEditor({ mode: 'edit', account: a }) },
        a.isActive
            ? { id: 'deactivate', label: 'Deactivate', onSelect: () => { setActive.reset(); setDeactivating(a); } }
            : { id: 'reactivate', label: 'Reactivate', onSelect: () => setActive.mutate({ accountId: a.id, active: true }) },
    ];

    const loading = accountsQuery.isPending;
    const failed = accountsQuery.isError;
    const newAccount = canEdit
        ? (
            <Button type="button" variant="primary" size="sm" onClick={() => setEditor({ mode: 'create' })}>
                New account
            </Button>
        )
        : undefined;

    return (
        <div className="space-y-4">
            <StructureToolbar
                noun="accounts"
                query={query}
                onQuery={setQuery}
                showInactive={showInactive}
                onShowInactive={setShowInactive}
                createLabel={canEdit ? 'New account' : undefined}
                onCreate={canEdit ? () => setEditor({ mode: 'create' }) : undefined}
                createRef={newAccountRef}
                disabled={failed}
                pending={loading}
            />

            {/* Reactivating asks nothing, so it has no dialog to say it failed in. */}
            {setActive.isError && setActive.variables?.active === true ? (
                <ActionError message={errorMessage(setActive.error, 'Could not reactivate the account.')} />
            ) : null}

            {failed ? (
                <ListError
                    message={errorMessage(accountsQuery.error, 'Could not load accounts.')}
                    onRetry={() => void accountsQuery.refetch()}
                />
            ) : loading ? (
                <ListLoading noun="accounts" />
            ) : accounts.length === 0 ? (
                <ListEmpty
                    message="No accounts yet"
                    hint="Add the bank, card, investment and loan accounts you want to track."
                    action={newAccount}
                />
            ) : groups.length === 0 ? (
                <ListNoMatches query={query} noun="account" onClear={() => setQuery('')} />
            ) : (
                groups.map((group) => (
                    <section key={group.type} className="space-y-1">
                        <StructureGroupHeading label={group.label} count={group.rows.length} />
                        <Panel>
                            <ul className="divide-y divide-border">
                                {group.rows.map((a) => (
                                    <StructureRow
                                        key={a.id}
                                        name={(
                                            <Link
                                                to="/ledgers/$ledgerId/accounts/$accountId"
                                                params={{ ledgerId, accountId: a.id }}
                                                className={rowLinkClass(!a.isActive)}
                                            >
                                                {a.name}
                                            </Link>
                                        )}
                                        badges={(
                                            <>
                                                {!a.isActive ? <StructureBadge>Inactive</StructureBadge> : null}
                                                {a.isSystem ? <StructureBadge>System</StructureBadge> : null}
                                            </>
                                        )}
                                        secondary={[a.institutionName, a.currencyCode].filter(Boolean).join(' · ') || null}
                                        count={a.needsReviewCount > 0 ? (
                                            <span className="rounded-full bg-accent-soft px-2 py-px font-medium text-accent-soft-text">
                                                {a.needsReviewCount} to review
                                            </span>
                                        ) : null}
                                        menu={!canEdit
                                            ? undefined
                                            : a.isSystem
                                                ? 'none'
                                                : { label: `Actions for ${a.name}`, onOpen: (anchor) => rowMenu.open(a, anchor) }}
                                        menuControl={rowControl(a.id)}
                                    />
                                ))}
                            </ul>
                        </Panel>
                    </section>
                ))
            )}

            {rowMenu.menu ? (
                <ContextMenu
                    anchor={rowMenu.menu.anchor}
                    label={`Actions for ${rowMenu.menu.row.name}`}
                    items={menuItems(rowMenu.menu.row)}
                    onClose={rowMenu.close}
                />
            ) : null}

            {deactivating !== null ? (
                <DeactivateConfirm
                    kind="account"
                    name={deactivating.name}
                    isConfirming={setActive.isPending}
                    error={setActive.isError ? errorMessage(setActive.error, 'Could not deactivate the account.') : null}
                    onConfirm={() => setActive.mutate(
                        { accountId: deactivating.id, active: false },
                        { onSuccess: () => setDeactivating(null) },
                    )}
                    onCancel={() => { setActive.reset(); setDeactivating(null); }}
                />
            ) : null}

            {editor !== null ? (
                <AccountEditorDialog
                    ledgerId={ledgerId}
                    account={editor.mode === 'edit' ? editor.account : null}
                    onClose={() => setEditor(null)}
                    onSaved={() => { /* dialog invalidated the accounts cache; the list refetches */ }}
                />
            ) : linked !== null ? (
                <AccountEditorDialog
                    ledgerId={ledgerId}
                    account={linked}
                    onClose={closeLinked}
                    onSaved={() => { /* dialog invalidated the accounts cache; the list refetches */ }}
                />
            ) : null}
        </div>
    );
}
