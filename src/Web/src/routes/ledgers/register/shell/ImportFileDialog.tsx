import { useCallback, useEffect, useRef, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';

import {
    importCsv,
    importOfx,
    importQif,
    previewCsv,
    previewOfx,
    previewQif,
    undoImport,
} from '@/lib/api';
import type {
    OfxImportResponse,
    OfxPreviewAccount,
    OfxPreviewResponse,
    UndoImportResult,
} from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { CsvMappingStep, type CsvMappingChoice } from './CsvMappingStep';
import { BROKERAGES, brokerageFor, type Brokerage } from './brokerages';
import { EMPTY_CSV_DRAFT, type CsvDraft } from './csvDraft';
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
    onUndone,
    accountKind,
    importProviderKey,
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
    /**
     * Fires after a successful UNDO, for the same reason `onImported` exists: the
     * windowed register does not re-read on cache invalidation, it re-reads when the
     * page calls `register.refresh()`. The dialog cannot do that itself, and inventing
     * a query key here is how the first attempt silently did nothing at all —
     * `['transactions', ledgerId]` is not a key anything registers.
     */
    onUndone: () => void;
    /**
     * Which register opened this. Investment accounts import a brokerage's own CSV via
     * a per-brokerage provider; bank accounts describe a delimited file with a mapping.
     * Offering both everywhere is what made the generic bank mapping available on an
     * investment account, where it cannot express shares, price or action — so it would
     * not fail, it would post a share purchase as a cash withdrawal.
     */
    accountKind: 'bank' | 'investment';
    /** The provider this account last imported with (mig 223), to preselect. */
    importProviderKey?: string | null;
}) {
    // A delimited file cannot be previewed until something says how to read it, so it
    // gets a step the other formats skip. OFX and QIF describe themselves.
    type Step =
        | { name: 'pick' }
        | { name: 'mapping' }
        | { name: 'preview'; preview: OfxPreviewResponse }
        | { name: 'result'; result: OfxImportResponse };

    const queryClient = useQueryClient();
    const fileInputRef = useRef<HTMLInputElement | null>(null);
    const [file, setFile] = useState<File | null>(null);
    const [step, setStep] = useState<Step>({ name: 'pick' });
    const [selectedProviderId, setSelectedProviderId] = useState<string | null>(null);
    // Which mapping the delimited path settled on — a saved id or draft YAML. Held here
    // rather than in the mapping step because IMPORT needs it again after the preview,
    // and re-deriving it would risk importing under a different mapping than was
    // previewed.
    const [csvMapping, setCsvMapping] = useState<CsvMappingChoice | null>(null);
    // The wizard's own answers live HERE, not inside the step, because the dialog
    // renders its steps conditionally: moving to the preview unmounts the wizard, and
    // every answer with it. Back from a bad preview used to land on the file picker
    // with nothing kept, so there was no route back to the mapping that caused it.
    const [csvDraft, setCsvDraft] = useState<CsvDraft>(EMPTY_CSV_DRAFT);
    // Preselected from what this account last imported with, so saying "this account is
    // at Fidelity" is a once-only act. A single supported brokerage is still SHOWN
    // rather than assumed: the list is also how someone learns what is supported.
    const [brokerage, setBrokerage] = useState<Brokerage | null>(
        () => brokerageFor(importProviderKey));
    const headingRef = useRef<HTMLHeadingElement | null>(null);

    const previewMutation = useMutation({
        // QIF responses are shape-compatible with the OFX preview
        // type (QIF's accountType union is a subset), so the dialog
        // works against the OFX type for both.
        mutationFn: (): Promise<OfxPreviewResponse> => {
            if (file === null) throw new Error('No file selected.');
            if (isDelimited(file)) {
                // An investment account's delimited file is a brokerage export, read by
                // that brokerage's provider; there is no mapping to choose.
                if (accountKind === 'investment') {
                    if (brokerage === null) throw new Error('Choose the brokerage this file came from.');
                    return brokerage.preview(ledgerId, file);
                }
                if (csvMapping === null) throw new Error('Choose or write a mapping first.');
                return previewCsv(ledgerId, file, csvMapping);
            }
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
            if (isDelimited(file)) {
                if (accountKind === 'investment') {
                    if (brokerage === null) throw new Error('Choose the brokerage this file came from.');
                    return brokerage.import(ledgerId, file, accountId, selectedProviderId);
                }
                if (csvMapping === null) throw new Error('Choose or write a mapping first.');
                return importCsv(
                    ledgerId, file, accountId, selectedProviderId, csvMapping);
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
        setCsvMapping(null);
        setCsvDraft(EMPTY_CSV_DRAFT);
        setStep({ name: 'pick' });
        clearAttempt();
        if (fileInputRef.current) fileInputRef.current.value = '';
    }

    /**
     * Both failures belong to ONE attempt at ONE file, so they expire together.
     *
     * Resetting only the preview left a failed import's error alive across Back and
     * across picking a different file: the next file previewed fine and rendered the
     * previous file's failure underneath it, before Import had been pressed at all. It
     * cleared only on the next mutate(), so the message on screen was about a file the
     * user could no longer see.
     */
    function clearAttempt() {
        previewMutation.reset();
        importMutation.reset();
    }

    /**
     * Taking the mapping AND clearing the last failure it caused.
     *
     * The preview error sat on screen through every edit that fixed it, because only a
     * new attempt cleared it — so the wizard read as broken while the document was
     * already correct. useCallback is load-bearing, not tidiness: the step pushes its
     * document up from an effect keyed on this function, so a fresh identity each
     * render would re-fire it forever.
     */
    // Through a ref, with EMPTY deps. `previewMutation` is a fresh object every render,
    // so depending on it gave this callback a new identity every render, which re-fired
    // the effect that calls it, which reset the mutation, which re-rendered... The first
    // attempt hung three tests at five seconds apiece.
    //
    // The ref is synced in an EFFECT, not during render. Assigning during render is
    // what react-hooks/refs forbids and the gate caught: a render may be thrown away
    // or replayed, and a write that happened anyway outlives the render that made it.
    // Reading a one-render-old mutation here is harmless — only `isError` and `reset`
    // are touched, and both are about a request that has already settled.
    const previewRef = useRef(previewMutation);
    useEffect(() => { previewRef.current = previewMutation; });
    const chooseCsvMapping = useCallback((mapping: CsvMappingChoice) => {
        setCsvMapping(mapping);
        if (previewRef.current.isError) previewRef.current.reset();
    }, []);

    /**
     * Each step is a different screen behind one dialog, so it needs a name of its own
     * — which doubles as the thing focus lands on.
     */
    const STEP_TITLES: Record<Step['name'], string> = {
        pick: 'Import statement file',
        mapping: 'Describe this file',
        preview: 'Confirm the import',
        result: 'Import finished',
    };

    // Advancing a step unmounts the focused button and leaves activeElement on <body>,
    // which is neither the first nor last item in the panel — so Modal's focus trap
    // stops redirecting and the next Tab walks into the register BEHIND the backdrop,
    // where rows and menus are still clickable. Moving focus to the step's heading puts
    // it back inside the dialog and, because the heading is the dialog's accessible
    // name, announces the new step instead of transitioning in silence.
    useEffect(() => { headingRef.current?.focus(); }, [step.name]);

    // The panel is a height-capped flex column so each step's body is
    // the single scroll region: header and footer stay pinned and the
    // primary button is always reachable, however many accounts or
    // warnings the file produces. The cap subtracts the backdrop's p-4.
    return (
        <Modal
            open
            onClose={onClose}
            titleId="import-file-title"
            className="flex max-h-[calc(100vh-2rem)] max-w-lg flex-col overflow-hidden"
        >
            <header className="shrink-0 border-b border-border px-4 py-3">
                <h2
                    id="import-file-title"
                    ref={headingRef}
                    tabIndex={-1}
                    className="text-base font-semibold outline-none"
                >
                    {STEP_TITLES[step.name]}
                </h2>
            </header>

            {step.name === 'pick' ? (
                <PickStep
                    accountKind={accountKind}
                    brokerage={brokerage}
                    onPickBrokerage={setBrokerage}
                    file={file}
                    onPickFile={(picked) => {
                        setFile(picked);
                        // A mapping describes ONE file's shape. Keeping it across a file
                        // change meant the next file was previewed and then imported
                        // under the previous file's document — plausible-looking
                        // garbage, with nothing on screen naming what produced it.
                        setCsvMapping(null);
                        setCsvDraft(EMPTY_CSV_DRAFT);
                        clearAttempt();
                    }}
                    fileInputRef={fileInputRef}
                    previewing={previewMutation.isPending}
                    previewError={
                        previewMutation.isError
                            ? errorMessage(previewMutation.error, 'Upload failed.')
                            : null
                    }
                    onCancel={onClose}
                    onUpload={() => {
                        // A delimited file has nothing to preview until a mapping says
                        // how to read it; every other format describes itself. A
                        // brokerage export is the third case: its provider already knows
                        // the format, so it needs no mapping step at all.
                        if (file !== null && isDelimited(file) && accountKind === 'bank') {
                            setStep({ name: 'mapping' });
                            return;
                        }
                        previewMutation.mutate();
                    }}
                />
            ) : null}

            {step.name === 'mapping' ? (
                <CsvMappingStep
                    ledgerId={ledgerId}
                    file={file!}
                    previewing={previewMutation.isPending}
                    previewError={
                        previewMutation.isError
                            ? errorMessage(previewMutation.error, 'Could not read the file.')
                            : null
                    }
                    draft={csvDraft}
                    onDraftChange={setCsvDraft}
                    onChoose={chooseCsvMapping}
                    onContinue={() => previewMutation.mutate()}
                    onBack={() => {
                        // Keeps the draft. A mapping belongs to a FILE, not to a
                        // position in the dialog, and picking a file is the only thing
                        // that invalidates one — so that is the only place it is
                        // cleared. Clearing here as well made the rule unfalsifiable:
                        // every route to the picker went through this handler, so
                        // deleting the real rule changed nothing a test could see.
                        setStep({ name: 'pick' });
                        clearAttempt();
                    }}
                    onCancel={onClose}
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
                        // A bad preview is a mapping problem, so Back goes to the
                        // MAPPING for a delimited file. It went to the file picker, and
                        // the picker looked untouched — same filename, same size — so
                        // nothing said the wizard's answers had just been thrown away.
                        setStep(file !== null && isDelimited(file) && accountKind === 'bank'
                            ? { name: 'mapping' }
                            : { name: 'pick' });
                        clearAttempt();
                    }}
                    onCancel={onClose}
                    onImport={() => importMutation.mutate()}
                />
            ) : null}

            {step.name === 'result' ? (
                <ResultStep
                    ledgerId={ledgerId}
                    result={step.result}
                    accountName={accountName}
                    onDone={onClose}
                    onImportAnother={reset}
                    onUndone={onUndone}
                />
            ) : null}
        </Modal>
    );
}

/** Format dispatch by extension — QIF vs OFX/QFX. */
function isQif(file: File): boolean {
    return /\.qif$/i.test(file.name);
}

/**
 * A delimited file — one that needs a mapping to be readable at all.
 *
 * Extension only, and .csv is NOT a promise about commas: the first real target file is
 * tab-separated with a .csv name. What the extension decides is which PATH the dialog
 * takes; the delimiter itself comes from the mapping.
 */
function isDelimited(file: File): boolean {
    return /\.(csv|tsv|txt)$/i.test(file.name);
}

/** All discovered account types are importable end-to-end. */
function canImport(account: OfxPreviewAccount): boolean {
    return account.accountType === 'bank'
        || account.accountType === 'credit_card'
        || account.accountType === 'investment';
}

function PickStep({
    accountKind, brokerage, onPickBrokerage,
    file, onPickFile, fileInputRef, previewing, previewError, onCancel, onUpload,
}: {
    accountKind: 'bank' | 'investment';
    brokerage: Brokerage | null;
    onPickBrokerage: (b: Brokerage | null) => void;
    file: File | null;
    onPickFile: (file: File | null) => void;
    fileInputRef: React.MutableRefObject<HTMLInputElement | null>;
    previewing: boolean;
    previewError: string | null;
    onCancel: () => void;
    onUpload: () => void;
}) {
    // A brokerage is needed only for a CSV: OFX, QFX and QIF describe themselves, and
    // demanding one for them would be asking a question with no bearing on the answer.
    //
    // So the question is asked AFTER the file, not before it. Asking first put a
    // CSV-only question above the control that decides whether it applies — the
    // brokerage list was the first thing on screen even for someone about to pick a
    // QFX, who then had to work out that it did not concern them. Picking the file
    // first means the list appears only once it is the actual next thing to answer,
    // and "other brokerages aren't supported yet" lands when it is actionable rather
    // than as a caveat about a file that has not been chosen.
    const showBrokeragePicker = accountKind === 'investment'
        && file !== null
        && isDelimited(file);
    const needsBrokerage = showBrokeragePicker && brokerage === null;

    return (
        <>
            <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-4">
                <p className="text-sm text-text-muted">
                    {accountKind === 'investment'
                        ? 'Choose a statement file exported from your brokerage or '
                          + 'retirement-plan provider. Supported formats: OFX, QFX, QIF, '
                          + 'and CSV from supported brokerages.'
                        : 'Choose a statement file exported from your bank, brokerage, or '
                          + 'retirement-plan provider. Supported formats: OFX, QFX, QIF, '
                          + 'and delimited text (CSV/TSV), which needs a mapping '
                          + 'describing its columns.'}
                </p>

                <div>
                    {/* The prose above describes the control but is not tied to it,
                        so this input had no accessible name — nothing to announce
                        beyond "file upload button". */}
                    <input
                        ref={fileInputRef}
                        type="file"
                        aria-label="Statement file"
                        accept=".ofx,.qfx,.qif,.csv,.tsv,.txt"
                        onChange={(e) => onPickFile(e.target.files?.[0] ?? null)}
                        className="block w-full text-sm file:mr-3 file:rounded file:border-0 file:bg-accent file:px-3 file:py-1.5 file:text-text-inverse hover:file:cursor-pointer hover:file:opacity-90"
                    />
                    {file !== null ? (
                        <p className="mt-2 text-xs text-text-subtle">
                            {file.name} — {formatBytes(file.size)}
                        </p>
                    ) : null}
                </div>
                {showBrokeragePicker ? (
                    <fieldset className="rounded border border-border p-3">
                        <legend className="px-1 text-xs text-text-muted">
                            Which brokerage is this CSV from?
                        </legend>
                        <div className="space-y-2">
                            {BROKERAGES.map((b) => (
                                <label key={b.key} className="flex items-start gap-2 text-sm">
                                    <input
                                        type="radio"
                                        className="mt-1"
                                        name="brokerage"
                                        checked={brokerage?.key === b.key}
                                        onChange={() => onPickBrokerage(b)}
                                    />
                                    <span>
                                        {b.label}
                                        <span className="block text-xs text-text-muted">
                                            Their “{b.exportName}” export.
                                        </span>
                                    </span>
                                </label>
                            ))}
                        </div>
                        {/* Says where the boundary is. Someone holding an export from
                            somewhere else otherwise learns only that their file could
                            not be read, which is indistinguishable from a bug — and now
                            that it shows only once a CSV is in hand, the second sentence
                            is a route they can actually take. */}
                        <p className="mt-2 text-xs text-text-subtle">
                            Other brokerages aren&rsquo;t supported yet — but OFX, QFX and
                            QIF files work from any provider.
                        </p>
                        {/* role="alert" earns its keep here: the whole fieldset appears
                            only on picking a CSV, so a screen-reader user gets no other
                            signal that a new question just became the reason Upload is
                            dead. It sits INSIDE the fieldset because it is about this
                            question, not about the dialog. */}
                        {needsBrokerage ? (
                            <p role="alert" className="mt-2 text-xs text-state-warning">
                                Choose the brokerage this CSV came from before uploading.
                            </p>
                        ) : null}
                    </fieldset>
                ) : null}
                <p className="text-xs text-text-subtle">Maximum file size: 5 MB.</p>
                {previewError !== null ? (
                    <p role="alert" className="text-xs text-state-danger">
                        {previewError}
                    </p>
                ) : null}
            </div>
            <footer className="flex shrink-0 justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                <Button type="button" variant="secondary" size="sm" onClick={onCancel}>
                    Cancel
                </Button>
                <Button
                    type="button"
                    variant="primary"
                    size="sm"
                    onClick={onUpload}
                    // Refused HERE rather than at preview time. Uploading only to be
                    // told "choose the brokerage" is a round trip whose answer was
                    // already on screen.
                    disabled={file === null || previewing || needsBrokerage}
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
            <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-4">
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
                {/* state-warning, not state-warn — see the note on the result step's
                    twin. Both boxes carried the non-existent token; this is the one
                    that shows FIRST, on the screen where a skipped row still has time
                    to change the user's mind about importing. */}
                {preview.errors.length > 0 ? (
                    <div className="rounded border border-state-warning/40 bg-state-warning-soft px-3 py-2 text-xs text-state-warning">
                        <p className="font-medium">
                            Preview warnings ({preview.errors.length}):
                        </p>
                        <ul className="mt-1 max-h-fixed-160px list-disc overflow-y-auto pl-4">
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
            <footer className="flex shrink-0 justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
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
    ledgerId, result, accountName, onDone, onImportAnother, onUndone,
}: {
    ledgerId: string;
    result: OfxImportResponse;
    accountName: string;
    onDone: () => void;
    onImportAnother: () => void;
    onUndone: () => void;
}) {
    const imported = result.transactionsForReview + result.alreadyKnown;

    // Undo lives HERE, on the result, because this is the moment the mistake is
    // discovered — "wrong account", "wrong file", "I already did this one". Anywhere
    // else and the operation id has to be hunted for.
    //
    // Two-phase deliberately: the dry run counts without touching anything, so the
    // confirm can state how many transactions will go and how many have been edited
    // since. A single click that deletes money is not an affordance worth having.
    const [undone, setUndone] = useState<UndoImportResult | null>(null);
    const preview = useMutation({
        mutationFn: () => undoImport(ledgerId, result.syncRunId, { dryRun: true }),
    });
    const commit = useMutation({
        mutationFn: () => undoImport(ledgerId, result.syncRunId),
        onSuccess: (r) => {
            setUndone(r);
            // The PAGE owns the refresh, exactly as it does after an import. The
            // windowed register (ADR-0079) re-reads when the page calls
            // register.refresh(), not when a cache key is invalidated — so the first
            // version of this, which invalidated ['transactions', ledgerId], refreshed
            // nothing whatever: no such key is registered anywhere. The rows stayed on
            // screen until a manual reload.
            onUndone();
        },
    });

    if (undone !== null) {
        return (
            <>
                <div className="min-h-0 flex-1 space-y-2 overflow-y-auto p-4 text-sm">
                    <p>
                        Undone. <strong>{undone.deleted}</strong> transaction
                        {undone.deleted === 1 ? '' : 's'} removed from{' '}
                        <strong>{accountName}</strong>.
                    </p>
                    {/* The honest recovery, and the reason the rows are removed rather
                        than hidden: a hidden row keeps its external_id and would make
                        this very file un-importable. */}
                    <p className="text-xs text-text-muted">
                        Import the file again if you need them back.
                    </p>
                </div>
                <footer className="flex shrink-0 justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
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

    return (
        <>
            <div className="min-h-0 flex-1 space-y-2 overflow-y-auto p-4 text-sm">
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
                {/* state-warning, not state-warn: there is no --color-state-warn
                    token, so this box previously rendered with no border colour and
                    no background — an invisible warning panel. */}
                {result.errors.length > 0 ? (
                    <div className="rounded border border-state-warning/40 bg-state-warning-soft px-3 py-2 text-xs">
                        <p className="font-medium">
                            Import warnings ({result.errors.length}):
                        </p>
                        <ul className="mt-1 max-h-fixed-160px list-disc overflow-y-auto pl-4">
                            {result.errors.map((e, i) => (
                                <li key={i}>
                                    <span className="font-mono">{e.code}</span>: {e.message}
                                </li>
                            ))}
                        </ul>
                    </div>
                ) : null}
            </div>
            <footer className="shrink-0 space-y-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                {preview.data !== undefined ? (
                    <div
                        role="alert"
                        className="rounded border border-state-warning/40 bg-state-warning-soft px-3 py-2 text-xs text-state-warning"
                    >
                        {preview.data.tooLarge ? (
                            <p>
                                This import is too large to undo in one go and nothing
                                was changed. Remove the rows from the register instead.
                            </p>
                        ) : (
                            <>
                                <p>
                                    Remove <strong>{preview.data.found}</strong>{' '}
                                    transaction{preview.data.found === 1 ? '' : 's'} from{' '}
                                    {accountName}?
                                </p>
                                {/* An undo that silently discards someone's edits is
                                    the kind of helpful delete nobody forgives. Said,
                                    never used to refuse. */}
                                {/* Now a DESTRUCTIVE warning, not a note. Undo removes
                                    the rows outright — it has to, or the same file
                                    could never be imported again — so any editing done
                                    since is gone with them. Re-importing brings the
                                    transactions back, not the edits. */}
                                {preview.data.edited > 0 ? (
                                    <p className="mt-1">
                                        <strong>{preview.data.edited}</strong> of them
                                        {preview.data.edited === 1 ? ' has' : ' have'}{' '}
                                        been edited since — those edits will be lost.
                                    </p>
                                ) : null}
                            </>
                        )}
                    </div>
                ) : null}
                {preview.isError ? (
                    <p role="alert" className="text-xs text-state-danger">
                        {errorMessage(preview.error)}
                    </p>
                ) : null}
                {commit.isError ? (
                    <p role="alert" className="text-xs text-state-danger">
                        {errorMessage(commit.error)}
                    </p>
                ) : null}
                <div className="flex justify-end gap-2">
                    {preview.data !== undefined && !preview.data.tooLarge ? (
                        <>
                            {/* ASKING WHAT UNDO WOULD DO IS NOT AGREEING TO IT. The dry
                                run is the only way to find out how many rows are at
                                stake, and it permanently swapped the safe button for a
                                red one sitting next to Import another and Done — one
                                mis-click from deleting the rows, with no way back short
                                of closing the dialog. Backing out has to be as available
                                as going ahead. */}
                            <Button
                                type="button"
                                variant="ghost"
                                size="sm"
                                disabled={commit.isPending}
                                onClick={() => { preview.reset(); commit.reset(); }}
                            >
                                Keep them
                            </Button>
                            <Button
                                type="button"
                                variant="danger"
                                size="sm"
                                disabled={commit.isPending}
                                onClick={() => commit.mutate()}
                            >
                                {commit.isPending ? 'Removing…' : 'Confirm undo'}
                            </Button>
                        </>
                    ) : (
                        <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            disabled={preview.isPending || imported === 0}
                            onClick={() => preview.mutate()}
                        >
                            {preview.isPending ? 'Checking…' : 'Undo this import'}
                        </Button>
                    )}
                    <Button type="button" variant="ghost" size="sm" onClick={onImportAnother}>
                        Import another
                    </Button>
                    <Button type="button" variant="primary" size="sm" onClick={onDone}>
                        Done
                    </Button>
                </div>
            </footer>
        </>
    );
}

function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}
