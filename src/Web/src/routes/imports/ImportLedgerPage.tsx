import { useEffect, useId, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';
import { useNavigate } from '@tanstack/react-router';
import { FileUp, CheckCircle2, AlertTriangle } from 'lucide-react';

import {
    previewMoneydanceImport,
    startMoneydanceImport,
    fetchImportJob,
    type ImportPreview,
} from '@/lib/api/import';
import { ApiError } from '@/lib/api';
import { currencyChoices } from '@/lib/currencies';
import { errorMessage } from '@/lib/errorMessage';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { pendingChoiceProps } from '@/components/ui/pending';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

import { ImportWarnings, ImportWarningsStatus } from './ImportWarnings';

/**
 * Create a new ledger from a Moneydance export (ADR-0071 D2). A four-step
 * wizard: pick the file → preview per-type counts + name the ledger → the
 * import runs as a background job we poll → open the new ledger. Import is
 * always into a *new* ledger, whatever other ledgers share its name. A finished
 * import whose validation checks failed says so on the last step, with a box per
 * failed check (ImportWarnings); one with nothing to report ends on "Import complete."
 */
export function ImportLedgerPage() {
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const nameId = useId();
    const currencyId = useId();
    const currencyHintId = useId();

    const [file, setFile] = useState<File | null>(null);
    const [preview, setPreview] = useState<ImportPreview | null>(null);
    const [ledgerName, setLedgerName] = useState('');
    // The new ledger's reporting currency (ADR-0114 D7): the export's base currency
    // unless changed. Null until the preview names it.
    const [currency, setCurrency] = useState<string | null>(null);
    const [jobId, setJobId] = useState<string | null>(null);
    const runRef = useRef<HTMLDivElement>(null);

    const previewMutation = useMutation({
        mutationFn: (f: File) => previewMoneydanceImport(f),
        onSuccess: (p) => {
            setPreview(p);
            setCurrency(p.baseCurrencyCode);
        },
    });

    const startMutation = useMutation({
        mutationFn: (args: { f: File; name: string; currency: string }) =>
            startMoneydanceImport(args.f, args.name, args.currency),
        onSuccess: (job) => setJobId(job.jobId),
    });

    const jobQuery = useQuery({
        queryKey: ['import-job', jobId],
        queryFn: () => fetchImportJob(jobId!),
        enabled: jobId !== null,
        // Poll while running; stop once the job settles.
        refetchInterval: (query) =>
            query.state.data && query.state.data.state !== 'running' ? false : 1000,
    });
    const job = jobQuery.data ?? null;

    // Surface the new ledger in the sidebar/landing list as soon as it exists.
    useEffect(() => {
        if (job?.state === 'succeeded') {
            void queryClient.invalidateQueries({ queryKey: ['ledgers'] });
        }
    }, [job?.state, queryClient]);

    useEffect(() => {
        if (jobId) runRef.current?.focus();
    }, [jobId]);

    function reset() {
        setFile(null);
        setPreview(null);
        setLedgerName('');
        setCurrency(null);
        setJobId(null);
    }

    const step: 'select' | 'preview' | 'running' =
        jobId ? 'running' : preview ? 'preview' : 'select';

    const previewError = previewMutation.error
        ? errorMessage(previewMutation.error, 'Could not read the export.')
        : null;
    // An export whose currencies cannot be told is refused by name (ADR-0114 D5): the
    // server's sentence names the accounts, and the way out is in Moneydance.
    const currencyUnresolved = previewMutation.error instanceof ApiError
        && previewMutation.error.code === 'import-currency-unresolved';
    const startError = startMutation.error
        ? errorMessage(startMutation.error, 'Could not start the import.')
        : null;

    return (
        <MainArea>
            <TopBar>
                <Breadcrumb
                    /* No "All ledgers /" root (ADR-0090). */
                    items={[{ label: 'Import from Moneydance' }]}
                />
            </TopBar>
            <MainPane>
                <div className="mx-auto max-w-2xl space-y-4 p-5">
                    <header>
                        <h1 className="text-xl font-semibold tracking-tight">
                            New ledger from Moneydance
                        </h1>
                        <p className="mt-1 text-sm text-text-muted">
                            Upload a Moneydance JSON export to seed a brand-new ledger. Your
                            existing ledgers are untouched.
                        </p>
                    </header>

                    {step === 'select' ? (
                        <Panel>
                            <PanelBody className="space-y-4">
                                <div className="space-y-1.5">
                                    <FieldLabel htmlFor="md-file">Moneydance export (.json)</FieldLabel>
                                    <input
                                        id="md-file"
                                        type="file"
                                        accept=".json,application/json"
                                        onChange={(e) => {
                                            setFile(e.target.files?.[0] ?? null);
                                            previewMutation.reset();
                                        }}
                                        className="block w-full text-sm text-text file:mr-3 file:rounded file:border-0 file:bg-surface-hover file:px-3 file:py-1.5 file:text-sm file:font-medium file:text-text"
                                    />
                                </div>
                                {previewError ? (
                                    <div role="alert" className="space-y-1 text-sm text-state-danger">
                                        <p>{previewError}</p>
                                        {currencyUnresolved ? (
                                            <p className="text-xs">
                                                Give each of these accounts its currency in
                                                Moneydance, export again, and choose the new file.
                                            </p>
                                        ) : null}
                                    </div>
                                ) : null}
                                <div className="flex justify-end">
                                    <Button
                                        type="button"
                                        disabled={!file}
                                        pending={previewMutation.isPending}
                                        onClick={() => file && previewMutation.mutate(file)}
                                        pendingLabel={<><FileUp className="mr-1 size-icon-md" aria-hidden />Analyzing…</>}
                                    >
                                        <FileUp className="mr-1 size-icon-md" aria-hidden />
                                        Analyze export
                                    </Button>
                                </div>
                            </PanelBody>
                        </Panel>
                    ) : null}

                    {step === 'preview' && preview ? (
                        <Panel>
                            <PanelBody className="space-y-4">
                                <div className="text-sm text-text-muted">
                                    Parsed <span className="font-medium text-text">{preview.totalItems.toLocaleString()}</span>{' '}
                                    items{preview.exporter ? <> from <span className="font-medium text-text">{preview.exporter}</span></> : null}.
                                </div>
                                <ul aria-label="Items by type" className="divide-y divide-border rounded border border-border text-sm">
                                    {preview.counts.map((c) => (
                                        <li key={c.objType} className="flex justify-between px-3 py-1.5">
                                            <span className="text-text-muted">{c.objType}</span>
                                            <span className="font-medium tabular-nums">{c.count.toLocaleString()}</span>
                                        </li>
                                    ))}
                                </ul>
                                <div className="space-y-1.5">
                                    <FieldLabel htmlFor={nameId}>New ledger name</FieldLabel>
                                    <Input
                                        id={nameId}
                                        autoFocus
                                        placeholder="e.g. Personal (imported)"
                                        value={ledgerName}
                                        pending={startMutation.isPending}
                                        onChange={(e) => setLedgerName(e.target.value)}
                                    />
                                </div>
                                <div className="space-y-1.5">
                                    <FieldLabel htmlFor={currencyId}>Reporting currency</FieldLabel>
                                    <select
                                        id={currencyId}
                                        aria-describedby={currencyHintId}
                                        value={currency ?? preview.baseCurrencyCode}
                                        {...pendingChoiceProps<HTMLSelectElement>(startMutation.isPending, {
                                            onChange: (e) => setCurrency(e.target.value),
                                        })}
                                        className="flex h-control-40px w-full rounded-md border border-border bg-surface px-3 py-2 text-sm text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:ring-accent aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                                    >
                                        {currencyChoices(currency ?? preview.baseCurrencyCode).map((c) => (
                                            <option key={c} value={c}>{c}</option>
                                        ))}
                                    </select>
                                    <p id={currencyHintId} className="text-[0.6875rem] text-text-muted">
                                        Totals that combine accounts are shown in it. It starts as
                                        the export&apos;s base currency. Each imported account keeps
                                        the currency it has in Moneydance. You can change it later
                                        in Settings.
                                    </p>
                                </div>
                                {startError ? (
                                    <p role="alert" className="text-sm text-state-danger">
                                        {startError}
                                    </p>
                                ) : null}
                                <div className="flex justify-between">
                                    <Button type="button" variant="secondary" size="sm" onClick={reset}>
                                        Choose a different file
                                    </Button>
                                    <Button
                                        type="button"
                                        disabled={ledgerName.trim().length === 0}
                                        pending={startMutation.isPending}
                                        onClick={() =>
                                            file && startMutation.mutate({
                                                f: file,
                                                name: ledgerName.trim(),
                                                currency: currency ?? preview.baseCurrencyCode,
                                            })
                                        }
                                        pendingLabel="Starting…"
                                    >
                                        Create ledger
                                    </Button>
                                </div>
                            </PanelBody>
                        </Panel>
                    ) : null}

                    {step === 'running' ? (
                        <Panel>
                            {/* Takes focus when the run starts, since the button that started it
                                is gone; it holds every state the run settles into. */}
                            <PanelBody
                                ref={runRef}
                                tabIndex={-1}
                                className="space-y-4 focus-visible:outline-none"
                            >
                                {/* One live region from the start of the run to its result, so the
                                    change is announced; a region that mounts already filled often
                                    is not. A failed run speaks through its alert instead. */}
                                <div
                                    role="status"
                                    hidden={job?.state === 'failed'}
                                    className="space-y-2"
                                >
                                    {job?.state === 'succeeded' ? (
                                        job.warnings.length === 0 ? (
                                            <p className="flex items-center gap-2 text-sm font-medium text-state-success">
                                                <CheckCircle2 className="size-icon-lg" aria-hidden />
                                                Import complete.
                                            </p>
                                        ) : (
                                            <ImportWarningsStatus job={job} />
                                        )
                                    ) : job?.state === 'failed' ? null : (
                                        <>
                                            <p className="text-sm text-text-muted">
                                                Importing… {job ? `${job.completed} of ${job.total} steps` : 'starting'}
                                                {job?.step ? ` — ${job.step}` : ''}
                                            </p>
                                            <div className="h-fixed-8px w-full overflow-hidden rounded bg-surface-hover">
                                                <div
                                                    className="h-full bg-accent transition-all"
                                                    style={{
                                                        width: `${job && job.total > 0 ? Math.round((job.completed / job.total) * 100) : 5}%`,
                                                    }}
                                                />
                                            </div>
                                        </>
                                    )}
                                </div>
                                {job?.state === 'succeeded' ? (
                                    <div className="space-y-4">
                                        {job.warnings.length > 0 ? <ImportWarnings job={job} /> : null}
                                        <div className="flex justify-end">
                                            <Button
                                                type="button"
                                                onClick={() =>
                                                    job.ledgerId &&
                                                    navigate({
                                                        to: '/ledgers/$ledgerId',
                                                        params: { ledgerId: job.ledgerId },
                                                    })
                                                }
                                            >
                                                Open ledger
                                            </Button>
                                        </div>
                                    </div>
                                ) : job?.state === 'failed' ? (
                                    <div className="space-y-4">
                                        <p role="alert" className="flex items-center gap-2 text-sm text-state-danger">
                                            <AlertTriangle className="size-icon-lg" aria-hidden />
                                            {job.error ?? 'The import failed.'}
                                        </p>
                                        <div className="flex justify-end">
                                            <Button type="button" variant="secondary" size="sm" onClick={reset}>
                                                Start over
                                            </Button>
                                        </div>
                                    </div>
                                ) : null}
                            </PanelBody>
                        </Panel>
                    ) : null}
                </div>
            </MainPane>
        </MainArea>
    );
}
