import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';

import { ApiError, fetchAccounts, fetchVisibleLedgers } from '@/lib/api';
import type { AccountSummary } from '@/lib/types';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

import { AccountEditorDialog } from './AccountEditorDialog';

// Accounts management page (ADR-0050) — the Ledger Hub's "Manage accounts →"
// target. Accounts grouped by type, with a "Show inactive" toggle (default
// hides inactive, matching the sidebar). Create + per-row edit via
// AccountEditorDialog. System accounts (Holdings siblings are filtered
// server-side; Uncategorized etc. carry is_system) are read-only.

// Display order + labels for the type sections. Real accounts only —
// categories are NOT managed here; they get their own surface (ADR-0050).
const GROUPS: ReadonlyArray<{ type: string; label: string }> = [
    { type: 'bank', label: 'Banking' },
    { type: 'credit_card', label: 'Credit cards' },
    { type: 'investment', label: 'Investments' },
    { type: 'asset', label: 'Assets' },
    { type: 'liability', label: 'Liabilities' },
    { type: 'loan', label: 'Loans' },
];

type EditorTarget = { mode: 'create' } | { mode: 'edit'; account: AccountSummary };

export function AccountsManagementPage() {
    const { ledgerId } = useParams({ strict: false }) as { ledgerId: string };
    const [editor, setEditor] = useState<EditorTarget | null>(null);
    const [showInactive, setShowInactive] = useState(false);

    const ledgersQuery = useQuery({ queryKey: ['ledgers'], queryFn: fetchVisibleLedgers });
    const accountsQuery = useQuery({
        queryKey: ['accounts', ledgerId, { includeInactive: showInactive }],
        queryFn: () => fetchAccounts(ledgerId, { includeInactive: showInactive }),
    });

    const ledger = ledgersQuery.data?.find((l) => l.id === ledgerId);
    const accounts = useMemo<AccountSummary[]>(() => accountsQuery.data ?? [], [accountsQuery.data]);
    const groups = useMemo(
        () => GROUPS
            .map((g) => ({ ...g, rows: accounts.filter((a) => a.accountType === g.type) }))
            .filter((g) => g.rows.length > 0),
        [accounts],
    );

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    items={[
                        {
                            label: ledger?.name ?? 'Ledger',
                            node: (
                                <Link to="/ledgers/$ledgerId" params={{ ledgerId }} className="hover:text-text">
                                    {ledger?.name ?? 'Ledger'}
                                </Link>
                            ),
                        },
                        { label: 'Accounts' },
                    ]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-5xl space-y-4 p-5">
                    <header className="flex items-center justify-between gap-4">
                        <h1 className="text-xl font-semibold tracking-tight">Accounts</h1>
                        <div className="flex items-center gap-3">
                            <label className="flex items-center gap-1.5 text-xs text-text-muted">
                                <input
                                    type="checkbox"
                                    checked={showInactive}
                                    onChange={(e) => setShowInactive(e.target.checked)}
                                />
                                Show inactive
                            </label>
                            <Button type="button" variant="primary" size="sm" onClick={() => setEditor({ mode: 'create' })}>
                                + New account
                            </Button>
                        </div>
                    </header>

                    {accountsQuery.isError ? (
                        <Panel className="border-state-danger/40 bg-state-danger-soft">
                            <PanelBody>
                                <p role="alert" className="text-sm text-state-danger">
                                    {accountsQuery.error instanceof ApiError
                                        ? accountsQuery.error.detail
                                        : 'Could not load accounts.'}
                                </p>
                            </PanelBody>
                        </Panel>
                    ) : accountsQuery.isPending ? (
                        <p className="text-sm text-text-subtle">Loading…</p>
                    ) : groups.length === 0 ? (
                        <Panel>
                            <PanelBody>
                                <p className="text-sm text-text-subtle">No accounts yet — create one.</p>
                            </PanelBody>
                        </Panel>
                    ) : (
                        groups.map((group) => (
                            <section key={group.type} className="space-y-1">
                                <h2 className="px-1 text-[0.625rem] font-semibold uppercase tracking-wider text-text-subtle">
                                    {group.label} · {group.rows.length}
                                </h2>
                                <Panel>
                                    <PanelBody className="p-0">
                                        <ul className="divide-y divide-border">
                                            {group.rows.map((a) => (
                                                <li key={a.id} className="flex items-center justify-between gap-3 px-3 py-2">
                                                    <span className="flex min-w-0 flex-col">
                                                        <span className="flex items-center gap-2">
                                                            <span className="truncate text-sm text-text">{a.name}</span>
                                                            {!a.isActive ? (
                                                                <span className="shrink-0 rounded bg-surface-muted px-1.5 py-0.5 text-[0.625rem] uppercase tracking-wider text-text-subtle">
                                                                    Inactive
                                                                </span>
                                                            ) : null}
                                                        </span>
                                                        {a.institutionName || a.currencyCode ? (
                                                            <span className="truncate text-xs text-text-subtle">
                                                                {[a.institutionName, a.currencyCode].filter(Boolean).join(' · ')}
                                                            </span>
                                                        ) : null}
                                                    </span>
                                                    {a.isSystem ? (
                                                        <span className="shrink-0 text-[0.625rem] uppercase tracking-wider text-text-subtle">
                                                            System
                                                        </span>
                                                    ) : (
                                                        <Button
                                                            type="button"
                                                            variant="ghost"
                                                            size="sm"
                                                            onClick={() => setEditor({ mode: 'edit', account: a })}
                                                        >
                                                            Edit
                                                        </Button>
                                                    )}
                                                </li>
                                            ))}
                                        </ul>
                                    </PanelBody>
                                </Panel>
                            </section>
                        ))
                    )}
                </div>
            </MainPane>

            {editor !== null ? (
                <AccountEditorDialog
                    ledgerId={ledgerId}
                    account={editor.mode === 'edit' ? editor.account : null}
                    onClose={() => setEditor(null)}
                    onSaved={() => { /* dialog invalidated the accounts cache; the list refetches */ }}
                />
            ) : null}
        </MainArea>
    );
}
