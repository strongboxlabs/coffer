import { useId, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { Building2, FileUp, Plus } from 'lucide-react';

import { createLedger, fetchVisibleLedgers } from '@/lib/api';
import type { LedgerSummary } from '@/lib/types';
import { errorMessage } from '@/lib/errorMessage';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { EmptyState } from '@/components/ui/EmptyState';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';
import {
    MainArea,
    MainPane,
    TopBar,
} from '@/components/ui/SidebarLayout';

/**
 * Post-login landing page. Lists the ledgers the authenticated user
 * has any grant on (server-side filtered by RLS); each entry links
 * to its detail page (`/ledgers/$id`) where the accounts list lives.
 *
 * Renders inside the persistent authed shell — this page owns the
 * `MainArea` (TopBar + MainPane) while the sidebar (in AuthedOutlet)
 * stays put. ADR-0021 D.2.
 */
export function LandingPage() {
    const queryClient = useQueryClient();
    const navigate = useNavigate();
    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });

    const nameId = useId();
    const [creating, setCreating] = useState(false);
    const [newName, setNewName] = useState('');
    const [seedCategories, setSeedCategories] = useState(true);

    const createMutation = useMutation({
        mutationFn: (vars: { name: string; seed: boolean }) =>
            createLedger(vars.name, vars.seed),
        onSuccess: async (ledger) => {
            await queryClient.invalidateQueries({ queryKey: ['ledgers'] });
            setCreating(false);
            setNewName('');
            setSeedCategories(true);
            navigate({ to: '/ledgers/$ledgerId', params: { ledgerId: ledger.id } });
        },
    });
    const createError = createMutation.error
        ? errorMessage(createMutation.error, 'Could not create the ledger.')
        : null;

    return (
        <MainArea>
            <TopBar>
                {/* One name, all the way through (ADR-0090): the ledger
                    dropdown's "Manage ledgers…" leads here, so the crumb and the
                    heading say the same thing. This page used to call itself
                    "All ledgers" in the crumb and "Your ledgers" in the heading —
                    both visible at once. It is ledger MANAGEMENT (create, import,
                    open), unrelated to the install-wide System settings. */}
                <Breadcrumb items={[{ label: 'Manage ledgers' }]} />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-3xl p-5">
                    <header className="mb-4 flex items-start justify-between gap-3">
                        <div>
                            <h1 className="text-xl font-semibold tracking-tight">
                                Manage ledgers
                            </h1>
                            <p className="mt-1 text-sm text-text-muted">
                                Every book you have access to. Click into one to
                                see its accounts.
                            </p>
                        </div>
                        <div className="flex shrink-0 gap-2">
                            <Button
                                type="button"
                                variant="secondary"
                                onClick={() => navigate({ to: '/imports/moneydance' })}
                            >
                                <FileUp className="mr-1 h-4 w-4" aria-hidden />
                                Import from Moneydance
                            </Button>
                            <Button
                                type="button"
                                variant="secondary"
                                onClick={() => setCreating(true)}
                            >
                                <Plus className="mr-1 h-4 w-4" aria-hidden />
                                New ledger
                            </Button>
                        </div>
                    </header>

                    {ledgersQuery.isPending ? (
                        <p className="text-sm text-text-subtle">Loading…</p>
                    ) : ledgersQuery.isError ? (
                        <Panel className="border-state-danger/40 bg-state-danger-soft">
                            <PanelBody>
                                <p
                                    role="alert"
                                    className="text-sm text-state-danger"
                                >
                                    Could not load your ledgers. Try refreshing
                                    the page.
                                </p>
                            </PanelBody>
                        </Panel>
                    ) : ledgersQuery.data.length === 0 ? (
                        <EmptyState
                            className="border-dashed"
                            message="You don't have any ledgers yet."
                            hint='Use "New ledger" above to create one.'
                        />
                    ) : (
                        <LedgerList ledgers={ledgersQuery.data} />
                    )}
                </div>
            </MainPane>

            <Modal
                open={creating}
                onClose={() => setCreating(false)}
                titleId="create-ledger-title"
                className="max-w-md"
            >
                <form
                    className="flex flex-col gap-4 p-5"
                    onSubmit={(e) => {
                        e.preventDefault();
                        const name = newName.trim();
                        if (name.length > 0 && !createMutation.isPending) {
                            createMutation.mutate({ name, seed: seedCategories });
                        }
                    }}
                >
                    <h2 id="create-ledger-title" className="text-base font-semibold">
                        Create a ledger
                    </h2>
                    <div className="space-y-1.5">
                        <FieldLabel htmlFor={nameId}>Name</FieldLabel>
                        <Input
                            id={nameId}
                            autoFocus
                            placeholder="e.g. Personal"
                            value={newName}
                            disabled={createMutation.isPending}
                            onChange={(e) => setNewName(e.target.value)}
                        />
                    </div>
                    <Checkbox
                        label="Start with default categories"
                        checked={seedCategories}
                        disabled={createMutation.isPending}
                        onChange={(e) => setSeedCategories(e.target.checked)}
                    />
                    {createError ? (
                        <p role="alert" className="text-sm text-state-danger">
                            {createError}
                        </p>
                    ) : null}
                    <div className="flex justify-end gap-2">
                        <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            onClick={() => setCreating(false)}
                        >
                            Cancel
                        </Button>
                        <Button
                            type="submit"
                            size="sm"
                            disabled={createMutation.isPending || newName.trim().length === 0}
                        >
                            {createMutation.isPending ? 'Creating…' : 'Create'}
                        </Button>
                    </div>
                </form>
            </Modal>
        </MainArea>
    );
}

function LedgerList({ ledgers }: { ledgers: readonly LedgerSummary[] }) {
    return (
        <Panel>
            <PanelHead>
                <h2 className="text-sm font-semibold">{ledgers.length} ledger{ledgers.length === 1 ? '' : 's'}</h2>
            </PanelHead>
            <ul aria-label="Ledgers" className="divide-y divide-border">
                {ledgers.map((ledger) => (
                    <li key={ledger.id}>
                        <Link
                            to="/ledgers/$ledgerId"
                            params={{ ledgerId: ledger.id }}
                            className="flex items-center justify-between gap-3 px-4 py-3 transition-colors hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1"
                        >
                            <span className="flex items-center gap-2">
                                <Building2
                                    className="h-4 w-4 text-text-muted"
                                    aria-hidden
                                />
                                <span className="text-sm font-medium text-text">
                                    {ledger.name}
                                </span>
                            </span>
                            <span className="text-[0.6875rem] font-medium uppercase tracking-wider text-text-subtle">
                                {ledger.role}
                            </span>
                        </Link>
                    </li>
                ))}
            </ul>
        </Panel>
    );
}
