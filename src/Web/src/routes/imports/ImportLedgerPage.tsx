import { useEffect, useId, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { FileUp, CheckCircle2, AlertTriangle } from 'lucide-react';

import {
    previewMoneydanceImport,
    startMoneydanceImport,
    fetchImportJob,
    type ImportPreview,
} from '@/lib/api/import';
import { errorMessage } from '@/lib/errorMessage';
import { Breadcrumb } from '@/components/ui/Breadcrumb';
import { Button } from '@/components/ui/Button';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { MainArea, MainPane, TopBar } from '@/components/ui/SidebarLayout';

/**
 * Create a new ledger from a Moneydance export (ADR-0071 D2). A four-step
 * wizard: pick the file → preview per-type counts + name the ledger → the
 * import runs as a background job we poll → open the new ledger. Import is
 * always into a *new* ledger, which also satisfies the seed-once guard.
 */
export function ImportLedgerPage() {
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const nameId = useId();

    const [file, setFile] = useState<File | null>(null);
    const [preview, setPreview] = useState<ImportPreview | null>(null);
    const [ledgerName, setLedgerName] = useState('');
    const [jobId, setJobId] = useState<string | null>(null);

    const previewMutation = useMutation({
        mutationFn: (f: File) => previewMoneydanceImport(f),
        onSuccess: (p) => setPreview(p),
    });

    const startMutation = useMutation({
        mutationFn: (args: { f: File; name: string }) => startMoneydanceImport(args.f, args.name),
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

    function reset() {
        setFile(null);
        setPreview(null);
        setLedgerName('');
        setJobId(null);
    }

    const step: 'select' | 'preview' | 'running' =
        jobId ? 'running' : preview ? 'preview' : 'select';

    const previewError = previewMutation.error
        ? errorMessage(previewMutation.error, 'Could not read the export.')
        : null;
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
                                    <p role="alert" className="text-sm text-state-danger">
                                        {previewError}
                                    </p>
                                ) : null}
                                <div className="flex justify-end">
                                    <Button
                                        type="button"
                                        disabled={!file || previewMutation.isPending}
                                        onClick={() => file && previewMutation.mutate(file)}
                                    >
                                        <FileUp className="mr-1 h-4 w-4" aria-hidden />
                                        {previewMutation.isPending ? 'Analyzing…' : 'Analyze export'}
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
                                        disabled={startMutation.isPending}
                                        onChange={(e) => setLedgerName(e.target.value)}
                                    />
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
                                        disabled={ledgerName.trim().length === 0 || startMutation.isPending}
                                        onClick={() =>
                                            file && startMutation.mutate({ f: file, name: ledgerName.trim() })
                                        }
                                    >
                                        {startMutation.isPending ? 'Starting…' : 'Create ledger'}
                                    </Button>
                                </div>
                            </PanelBody>
                        </Panel>
                    ) : null}

                    {step === 'running' ? (
                        <Panel>
                            <PanelBody className="space-y-4">
                                {job?.state === 'succeeded' ? (
                                    <div className="space-y-4">
                                        <p className="flex items-center gap-2 text-sm font-medium text-state-success">
                                            <CheckCircle2 className="h-5 w-5" aria-hidden />
                                            Import complete.
                                        </p>
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
                                            <AlertTriangle className="h-5 w-5" aria-hidden />
                                            {job.error ?? 'The import failed.'}
                                        </p>
                                        <div className="flex justify-end">
                                            <Button type="button" variant="secondary" size="sm" onClick={reset}>
                                                Start over
                                            </Button>
                                        </div>
                                    </div>
                                ) : (
                                    <div className="space-y-2" aria-live="polite">
                                        <p className="text-sm text-text-muted">
                                            Importing… {job ? `${job.completed} of ${job.total} steps` : 'starting'}
                                            {job?.step ? ` — ${job.step}` : ''}
                                        </p>
                                        <div className="h-2 w-full overflow-hidden rounded bg-surface-hover">
                                            <div
                                                className="h-full bg-accent transition-all"
                                                style={{
                                                    width: `${job && job.total > 0 ? Math.round((job.completed / job.total) * 100) : 5}%`,
                                                }}
                                            />
                                        </div>
                                    </div>
                                )}
                            </PanelBody>
                        </Panel>
                    ) : null}
                </div>
            </MainPane>
        </MainArea>
    );
}
