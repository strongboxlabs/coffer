import { useId, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { Link, useNavigate } from '@tanstack/react-router';
import { AlertTriangle, Building2, FileUp, FlaskConical, Plus } from 'lucide-react';

import { createLedger, fetchVisibleLedgers } from '@/lib/api';
import type { LedgerSummary } from '@/lib/types';
import { CURRENCIES } from '@/lib/currencies';
import { errorMessage } from '@/lib/errorMessage';
import { AttentionCount } from '@/components/AttentionDot';
import { useLedgerAttention } from '@/lib/useLedgerAttention';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { EmptyState } from '@/components/ui/EmptyState';
import { usePendingButtonProps } from '@/components/ui/pending';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel';
import { pendingChoiceProps } from '@/components/ui/pending';
import {
    MainArea,
    MainPane,
    TopBar,
} from '@/components/ui/SidebarLayout';

import { DemoBuildRow } from './DemoBuildRow';
import { demoExamplesMissingText, type DemoRow } from './demoBuild';
import { useDemoBuild } from './useDemoBuild';

const ROW_LINK = 'flex items-center justify-between gap-3 px-4 py-3 transition-colors hover:bg-surface-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-1';
const ROLE = 'text-[0.6875rem] font-medium uppercase tracking-wider text-text-subtle';
// The setup chooser's card treatment (SetupPage), for the empty hub's two choices,
// plus the pending look a raw button maps for itself (pending.ts): the Demo card
// waits on the start request as the header button does.
const CHOICE = 'w-full rounded border border-border bg-surface px-4 py-3 text-left transition hover:border-accent hover:bg-surface-muted aria-disabled:cursor-default aria-disabled:opacity-50';

/**
 * Post-login landing page. Lists the ledgers the authenticated user
 * has any grant on (server-side filtered by RLS); each entry links
 * to its detail page (`/ledgers/$id`) where the accounts list lives.
 *
 * It is also where a ledger begins: import one, add a Demo, or create an empty
 * one, in that order in the header on every install (ADR-0115 D2). Setup creates
 * no ledger, so a fresh install lands here empty, and the empty state offers the
 * last two as cards. A Demo builds in the background; its row says how far it has
 * got, and whether it failed or was interrupted (ADR-0115 D9).
 *
 * Renders inside the persistent authed shell — this page owns the
 * `MainArea` (TopBar + MainPane) while the sidebar (in AuthedOutlet)
 * stays put. ADR-0021 D.2.
 */
export function LandingPage() {
    const queryClient = useQueryClient();
    const navigate = useNavigate();
    const pendingProps = usePendingButtonProps();
    const ledgersQuery = useQuery({
        queryKey: ['ledgers'],
        queryFn: fetchVisibleLedgers,
    });
    const demo = useDemoBuild(ledgersQuery);

    const nameId = useId();
    const currencyId = useId();
    const currencyHintId = useId();
    const [creating, setCreating] = useState(false);
    const [newName, setNewName] = useState('');
    const [seedCategories, setSeedCategories] = useState(true);
    // The reporting currency (ADR-0114 D7): USD unless changed, the account editor's
    // default too.
    const [currency, setCurrency] = useState('USD');

    const createMutation = useMutation({
        mutationFn: (vars: { name: string; seed: boolean; currency: string }) =>
            createLedger(vars.name, vars.seed, vars.currency),
        onSuccess: async (ledger) => {
            await queryClient.invalidateQueries({ queryKey: ['ledgers'] });
            setCreating(false);
            setNewName('');
            setSeedCategories(true);
            setCurrency('USD');
            navigate({ to: '/ledgers/$ledgerId', params: { ledgerId: ledger.id } });
        },
    });
    const createError = createMutation.error
        ? errorMessage(createMutation.error, 'Could not create the ledger.')
        : null;

    // A Demo still adding its examples is listed once, as its preparing row.
    const listed = ledgersQuery.data?.filter((l) => l.id !== demo.hiddenLedgerId) ?? [];

    return (
        <MainArea>
            <TopBar>
                {/* One name, all the way through (ADR-0090): the ledger
                    dropdown's "Manage ledgers…" leads here, so the crumb and the
                    heading say the same thing rather than two names visible at
                    once. It is ledger MANAGEMENT (create, import,
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
                                <FileUp className="mr-1 size-icon-md" aria-hidden />
                                Import from Moneydance
                            </Button>
                            <Button
                                type="button"
                                variant="secondary"
                                pending={demo.pending}
                                onClick={demo.start}
                            >
                                <FlaskConical className="mr-1 size-icon-md" aria-hidden />
                                Demo ledger
                            </Button>
                            <Button
                                type="button"
                                variant="secondary"
                                onClick={() => setCreating(true)}
                            >
                                <Plus className="mr-1 size-icon-md" aria-hidden />
                                New ledger
                            </Button>
                        </div>
                    </header>

                    {/* A Demo refusal that named no build (another import is running):
                        no row, because no Demo was reserved (ADR-0115 D9). */}
                    {demo.alert !== null ? (
                        <p role="alert" className="mb-4 flex items-center gap-2 text-sm text-state-danger">
                            <AlertTriangle className="size-icon-lg" aria-hidden />
                            {demo.alert}
                        </p>
                    ) : null}

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
                    ) : listed.length === 0 && demo.rows.length === 0 ? (
                            <EmptyState
                                className="border-dashed"
                                message="You don't have any ledgers yet."
                                hint="Start your own, or add a Demo to look around first."
                                action={
                                    <div className="grid w-full max-w-xl grid-cols-2 gap-3">
                                        <button
                                            type="button"
                                            onClick={() => setCreating(true)}
                                            className={CHOICE}
                                        >
                                            <span className="flex items-center gap-2 text-sm font-medium text-text">
                                                <Plus className="size-icon-md text-accent" aria-hidden />
                                                New ledger
                                            </span>
                                            <span className="mt-1 block text-[0.6875rem] text-text-muted">
                                                An empty ledger for your own books, with the default categories.
                                            </span>
                                        </button>
                                        <button
                                            type="button"
                                            {...pendingProps(demo.pending, demo.start)}
                                            className={CHOICE}
                                        >
                                            <span className="flex items-center gap-2 text-sm font-medium text-text">
                                                <FlaskConical className="size-icon-md text-accent" aria-hidden />
                                                Add a Demo ledger
                                            </span>
                                            <span className="mt-1 block text-[0.6875rem] text-text-muted">
                                                Five years of made-up household history, ending today. Safe to explore and delete.
                                            </span>
                                        </button>
                                    </div>
                                }
                            />
                        ) : (
                            <LedgerList
                                ledgers={listed}
                                demoRows={demo.rows}
                                onRetry={demo.retry}
                                retryPending={demo.pending}
                            />
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
                            createMutation.mutate({ name, seed: seedCategories, currency });
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
                            pending={createMutation.isPending}
                            onChange={(e) => setNewName(e.target.value)}
                        />
                    </div>
                    <div className="space-y-1.5">
                        <FieldLabel htmlFor={currencyId}>Reporting currency</FieldLabel>
                        <select
                            id={currencyId}
                            aria-describedby={currencyHintId}
                            value={currency}
                            {...pendingChoiceProps<HTMLSelectElement>(createMutation.isPending, {
                                onChange: (e) => setCurrency(e.target.value),
                            })}
                            className="flex h-control-40px w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-accent aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                        >
                            {CURRENCIES.map((c) => <option key={c} value={c}>{c}</option>)}
                        </select>
                        <p id={currencyHintId} className="text-[0.6875rem] text-text-muted">
                            Totals that combine accounts are shown in it. Each account keeps its own
                            currency. You can change it later in Settings.
                        </p>
                    </div>
                    <Checkbox
                        label="Start with default categories"
                        checked={seedCategories}
                        pending={createMutation.isPending}
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
                            disabled={newName.trim().length === 0}
                            pending={createMutation.isPending}
                            pendingLabel="Creating…"
                        >
                            Create
                        </Button>
                    </div>
                </form>
            </Modal>
        </MainArea>
    );
}

function LedgerList({
    ledgers,
    demoRows,
    onRetry,
    retryPending,
}: {
    ledgers: readonly LedgerSummary[];
    demoRows: readonly DemoRow[];
    onRetry: () => void;
    retryPending: boolean;
}) {
    // The surface the follow-up is really about: this is the one screen that shows
    // every ledger at once, so it is where "which of these needs me" is answerable at
    // a glance instead of by opening each one (FU-049).
    const attention = useLedgerAttention();

    // The count is of ledgers that can be opened: a Demo still building, or one that
    // failed or was interrupted, is not one yet.
    return (
        <Panel>
            <PanelHead>
                <h2 className="text-sm font-semibold">{ledgers.length} ledger{ledgers.length === 1 ? '' : 's'}</h2>
            </PanelHead>
            <ul aria-label="Ledgers" className="divide-y divide-border">
                {ledgers.map((ledger) => {
                    const missing = ledger.demoExamplesMissing ?? [];
                    return (
                        <li key={ledger.id}>
                            <Link
                                to="/ledgers/$ledgerId"
                                params={{ ledgerId: ledger.id }}
                                className={missing.length > 0 ? `${ROW_LINK} flex-wrap` : ROW_LINK}
                            >
                                <span className="flex items-center gap-2">
                                    <Building2
                                        className="size-icon-md text-text-muted"
                                        aria-hidden
                                    />
                                    <span className="text-sm font-medium text-text">
                                        {ledger.name}
                                    </span>
                                </span>
                                <span className="flex items-center gap-3">
                                    {/* With its count, unlike the rail's aggregate: here
                                        the number is actionable, because it belongs to one
                                        named ledger the reader can open. */}
                                    <AttentionCount
                                        severity={attention.severityFor(ledger.id)}
                                        count={attention.countFor(ledger.id)}
                                        context={ledger.name}
                                    />
                                    <span className={ROLE}>{ledger.role}</span>
                                </span>
                                {/* A Demo whose history imported but whose examples
                                    were not all added (ADR-0115 D9): a real ledger that
                                    opens as any other, saying what it lacks. */}
                                {missing.length > 0 ? (
                                    <span role="status" className="mt-1 flex w-full items-center gap-2 text-sm text-state-warning">
                                        <AlertTriangle className="size-icon-lg" aria-hidden />
                                        {demoExamplesMissingText(missing)}
                                    </span>
                                ) : null}
                            </Link>
                        </li>
                    );
                })}
                {demoRows.map((row) => (
                    <DemoBuildRow key={row.key} row={row} onRetry={onRetry} retryPending={retryPending} />
                ))}
            </ul>
        </Panel>
    );
}
