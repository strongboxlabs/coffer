import { useRef, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';

import {
    importOfx,
    importQif,
    previewOfx,
    previewQif,
} from '@/lib/api';
import type {
    OfxImportResponse,
    OfxPreviewAccount,
    OfxPreviewResponse,
} from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { errorMessage } from '@/lib/errorMessage';

/**
 * File-import wizard for the register surface. One affordance for
 * every supported statement format — the format is distinguished by
 * the picked file's extension, not by separate buttons:
 *
 *   `.ofx` / `.qfx` → OFX provider (multi-account; ADR-0031 Phase 4)
 *   `.qif`          → QIF provider (single-account; ADR-0042)
 *
 * Both providers return the same preview/import wire shape, so the
 * three-step flow (pick → confirm → result) is format-agnostic. QIF
 * always surfaces exactly one account, which the preview step
 * auto-picks; OFX may surface several for the user to choose among.
 *
 * Imported rows land needs-review; the editor is where the user
 * finalizes each transaction (the importers never impose a cash
 * model — ADR-0042).
 */
export function ImportFileDialog({
    ledgerId,
    accountId,
    accountName,
    onClose,
    onImported,
}: {
    ledgerId: string;
    accountId: string;
    /** Display name for the target Coffer account, shown in steps 2
     *  and 3 so the user can confirm where transactions will land. */
    accountName: string;
    onClose: () => void;
    /** Fires after a successful import. Caller invalidates register
     *  + balance queries so the new rows show up. */
    onImported: (result: OfxImportResponse) => void;
}) {
    type Step =
        | { name: 'pick' }
        | { name: 'preview'; preview: OfxPreviewResponse }
        | { name: 'result'; result: OfxImportResponse };

    const queryClient = useQueryClient();
    const fileInputRef = useRef<HTMLInputElement | null>(null);
    const [file, setFile] = useState<File | null>(null);
    const [step, setStep] = useState<Step>({ name: 'pick' });
    const [selectedProviderId, setSelectedProviderId] = useState<string | null>(null);

    const previewMutation = useMutation({
        // QIF responses are shape-compatible with the OFX preview
        // type (QIF's accountType union is a subset), so the dialog
        // works against the OFX type for both.
        mutationFn: (): Promise<OfxPreviewResponse> => {
            if (file === null) throw new Error('No file selected.');
            return isQif(file) ? previewQif(ledgerId, file) : previewOfx(ledgerId, file);
        },
        onSuccess: (preview) => {
            const importable = preview.accounts.filter(canImport);
            const autoPick = importable.length === 1
                ? importable[0]!.providerAccountId
                : null;
            setSelectedProviderId(autoPick);
            setStep({ name: 'preview', preview });
        },
    });

    const importMutation = useMutation({
        mutationFn: (): Promise<OfxImportResponse> => {
            if (file === null) throw new Error('No file selected.');
            if (selectedProviderId === null) {
                throw new Error('Pick an account from the file.');
            }
            return isQif(file)
                ? importQif(ledgerId, file, accountId, selectedProviderId)
                : importOfx(ledgerId, file, accountId, selectedProviderId);
        },
        onSuccess: (result) => {
            // The register page's `onImported` owns the row refresh — it calls
            // register.refresh() directly (import is a local, in-register action,
            // like an edit). We deliberately do NOT invalidate the ADR-0079
            // canonical ['register', …] key here, or the controller's sentinel
            // would reload the window a second time; and we drop the old dead
            // ['index-buckets', …] typo (the real key is register-index-buckets,
            // which onImported already invalidates). Just refresh the sibling
            // caches the dialog reaches directly.
            queryClient.invalidateQueries({ queryKey: ['accounts', ledgerId] });
            queryClient.invalidateQueries({ queryKey: ['holdings', ledgerId, accountId] });
            setStep({ name: 'result', result });
            onImported(result);
        },
    });

    function reset() {
        setFile(null);
        setSelectedProviderId(null);
        setStep({ name: 'pick' });
        previewMutation.reset();
        importMutation.reset();
        if (fileInputRef.current) fileInputRef.current.value = '';
    }

    return (
        <Modal
            open
            onClose={onClose}
            titleId="import-file-title"
            className="max-w-lg overflow-hidden"
        >
            <header className="border-b border-border px-4 py-3">
                <h2 id="import-file-title" className="text-base font-semibold">Import statement file</h2>
            </header>

            {step.name === 'pick' ? (
                <PickStep
                    file={file}
                    onPickFile={setFile}
                    fileInputRef={fileInputRef}
                    previewing={previewMutation.isPending}
                    previewError={
                        previewMutation.isError
                            ? errorMessage(previewMutation.error, 'Upload failed.')
                            : null
                    }
                    onCancel={onClose}
                    onUpload={() => previewMutation.mutate()}
                />
            ) : null}

            {step.name === 'preview' ? (
                <PreviewStep
                    preview={step.preview}
                    accountName={accountName}
                    selectedProviderId={selectedProviderId}
                    onSelect={setSelectedProviderId}
                    importing={importMutation.isPending}
                    importError={
                        importMutation.isError
                            ? errorMessage(importMutation.error, 'Upload failed.')
                            : null
                    }
                    onBack={() => {
                        setStep({ name: 'pick' });
                        previewMutation.reset();
                    }}
                    onCancel={onClose}
                    onImport={() => importMutation.mutate()}
                />
            ) : null}

            {step.name === 'result' ? (
                <ResultStep
                    result={step.result}
                    accountName={accountName}
                    onDone={onClose}
                    onImportAnother={reset}
                />
            ) : null}
        </Modal>
    );
}

/** Format dispatch by extension — QIF vs OFX/QFX. */
function isQif(file: File): boolean {
    return /\.qif$/i.test(file.name);
}

/** All discovered account types are importable end-to-end. */
function canImport(account: OfxPreviewAccount): boolean {
    return account.accountType === 'bank'
        || account.accountType === 'credit_card'
        || account.accountType === 'investment';
}

function PickStep({
    file, onPickFile, fileInputRef, previewing, previewError, onCancel, onUpload,
}: {
    file: File | null;
    onPickFile: (file: File | null) => void;
    fileInputRef: React.MutableRefObject<HTMLInputElement | null>;
    previewing: boolean;
    previewError: string | null;
    onCancel: () => void;
    onUpload: () => void;
}) {
    return (
        <>
            <div className="space-y-3 p-4">
                <p className="text-sm text-text-muted">
                    Choose a statement file exported from your bank,
                    brokerage, or retirement-plan provider. Supported
                    formats: OFX, QFX, and QIF.
                </p>
                <div>
                    <input
                        ref={fileInputRef}
                        type="file"
                        accept=".ofx,.qfx,.qif"
                        onChange={(e) => onPickFile(e.target.files?.[0] ?? null)}
                        className="block w-full text-sm file:mr-3 file:rounded file:border-0 file:bg-accent file:px-3 file:py-1.5 file:text-on-accent hover:file:cursor-pointer hover:file:opacity-90"
                    />
                    {file !== null ? (
                        <p className="mt-2 text-xs text-text-subtle">
                            {file.name} — {formatBytes(file.size)}
                        </p>
                    ) : null}
                </div>
                <p className="text-xs text-text-subtle">Maximum file size: 5 MB.</p>
                {previewError !== null ? (
                    <p role="alert" className="text-xs text-state-danger">
                        {previewError}
                    </p>
                ) : null}
            </div>
            <footer className="flex justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                <Button type="button" variant="secondary" size="sm" onClick={onCancel}>
                    Cancel
                </Button>
                <Button
                    type="button"
                    variant="primary"
                    size="sm"
                    onClick={onUpload}
                    disabled={file === null || previewing}
                >
                    {previewing ? 'Uploading…' : 'Upload & preview →'}
                </Button>
            </footer>
        </>
    );
}

function PreviewStep({
    preview, accountName, selectedProviderId, onSelect,
    importing, importError, onBack, onCancel, onImport,
}: {
    preview: OfxPreviewResponse;
    accountName: string;
    selectedProviderId: string | null;
    onSelect: (id: string) => void;
    importing: boolean;
    importError: string | null;
    onBack: () => void;
    onCancel: () => void;
    onImport: () => void;
}) {
    const selected = preview.accounts.find(
        (a) => a.providerAccountId === selectedProviderId,
    );
    const accountsByImportable = {
        importable: preview.accounts.filter(canImport),
        unsupported: preview.accounts.filter((a) => !canImport(a)),
    };
    // Single-account files (every QIF, single-account OFX) skip the
    // chooser — the one account is auto-picked, so just confirm.
    const singleAccount = accountsByImportable.importable.length === 1
        && accountsByImportable.unsupported.length === 0;
    return (
        <>
            <div className="space-y-3 p-4">
                {preview.accounts.length === 0 ? (
                    <p className="text-sm text-state-danger" role="alert">
                        The file contained no recognizable account blocks.
                    </p>
                ) : singleAccount ? (
                    <p className="text-sm">
                        This file contains{' '}
                        <strong className="font-mono tabular-nums">
                            {accountsByImportable.importable[0]!.transactionCount}
                        </strong>{' '}
                        transaction
                        {accountsByImportable.importable[0]!.transactionCount === 1 ? '' : 's'}.
                        Import into <strong>{accountName}</strong>?
                    </p>
                ) : (
                    <>
                        <p className="text-sm">
                            This file contains <strong>{preview.accounts.length}</strong>{' '}
                            account{preview.accounts.length === 1 ? '' : 's'}. Pick the
                            one to import into <strong>{accountName}</strong>:
                        </p>
                        <fieldset className="space-y-1">
                            <legend className="sr-only">Provider account</legend>
                            {accountsByImportable.importable.map((a) => (
                                <label
                                    key={a.providerAccountId}
                                    className="flex cursor-pointer items-center gap-3 rounded border border-border px-3 py-2 text-sm hover:bg-surface-hover"
                                >
                                    <input
                                        type="radio"
                                        name="provider-account"
                                        value={a.providerAccountId}
                                        checked={a.providerAccountId === selectedProviderId}
                                        onChange={() => onSelect(a.providerAccountId)}
                                    />
                                    <span className="flex-1 truncate font-mono text-xs">
                                        {a.providerAccountId}
                                    </span>
                                    <span className="text-xs uppercase tracking-wider text-text-muted">
                                        {a.accountType.replace('_', ' ')}
                                    </span>
                                    {a.currency !== null ? (
                                        <span className="text-xs text-text-muted">
                                            {a.currency}
                                        </span>
                                    ) : null}
                                    <span className="font-mono tabular-nums text-xs">
                                        {a.transactionCount} txn
                                        {a.transactionCount === 1 ? '' : 's'}
                                    </span>
                                </label>
                            ))}
                            {accountsByImportable.unsupported.map((a) => (
                                <div
                                    key={a.providerAccountId}
                                    className="flex items-center gap-3 rounded border border-border/60 bg-surface-muted/30 px-3 py-2 text-sm text-text-subtle"
                                >
                                    <input type="radio" disabled aria-disabled="true" />
                                    <span className="flex-1 truncate font-mono text-xs">
                                        {a.providerAccountId}
                                    </span>
                                    <span className="text-xs uppercase tracking-wider">
                                        {a.accountType.replace('_', ' ')}
                                    </span>
                                    <span className="text-xs italic">unsupported</span>
                                </div>
                            ))}
                        </fieldset>
                    </>
                )}
                {preview.errors.length > 0 ? (
                    <div className="rounded border border-state-warn/40 bg-state-warn-soft px-3 py-2 text-xs">
                        <p className="font-medium">Preview warnings:</p>
                        <ul className="mt-1 list-disc pl-4">
                            {preview.errors.map((e, i) => (
                                <li key={i}>
                                    <span className="font-mono">{e.code}</span>: {e.message}
                                </li>
                            ))}
                        </ul>
                    </div>
                ) : null}
                {importError !== null ? (
                    <p role="alert" className="text-xs text-state-danger">
                        {importError}
                    </p>
                ) : null}
            </div>
            <footer className="flex justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                <Button type="button" variant="secondary" size="sm" onClick={onCancel}>
                    Cancel
                </Button>
                <Button type="button" variant="ghost" size="sm" onClick={onBack}>
                    ← Back
                </Button>
                <Button
                    type="button"
                    variant="primary"
                    size="sm"
                    onClick={onImport}
                    disabled={selectedProviderId === null || importing}
                >
                    {importing
                        ? 'Importing…'
                        : selected !== undefined
                            ? `Import ${selected.transactionCount} txn${
                                selected.transactionCount === 1 ? '' : 's'
                            } →`
                            : 'Import →'}
                </Button>
            </footer>
        </>
    );
}

function ResultStep({
    result, accountName, onDone, onImportAnother,
}: {
    result: OfxImportResponse;
    accountName: string;
    onDone: () => void;
    onImportAnother: () => void;
}) {
    const imported = result.transactionsForReview + result.alreadyKnown;
    return (
        <>
            <div className="space-y-2 p-4 text-sm">
                <p>
                    <span className="text-state-success">✓</span> Imported{' '}
                    <strong>{imported}</strong> transaction
                    {imported === 1 ? '' : 's'} into <strong>{accountName}</strong>.
                </p>
                <ul className="ml-6 list-disc text-text-muted">
                    <li>
                        <strong className="font-mono tabular-nums">
                            {result.transactionsForReview}
                        </strong>{' '}
                        need review
                    </li>
                    <li>
                        <strong className="font-mono tabular-nums">
                            {result.alreadyKnown}
                        </strong>{' '}
                        already known (deduped)
                    </li>
                </ul>
                {result.errors.length > 0 ? (
                    <div className="rounded border border-state-warn/40 bg-state-warn-soft px-3 py-2 text-xs">
                        <p className="font-medium">Import warnings:</p>
                        <ul className="mt-1 list-disc pl-4">
                            {result.errors.map((e, i) => (
                                <li key={i}>
                                    <span className="font-mono">{e.code}</span>: {e.message}
                                </li>
                            ))}
                        </ul>
                    </div>
                ) : null}
            </div>
            <footer className="flex justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                <Button type="button" variant="ghost" size="sm" onClick={onImportAnother}>
                    Import another
                </Button>
                <Button type="button" variant="primary" size="sm" onClick={onDone}>
                    Done
                </Button>
            </footer>
        </>
    );
}

function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}
