import { useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import {
    createCsvMapping, fetchCsvMappings, updateCsvMapping, validateCsvMapping,
} from '@/lib/api';
import type { CsvDelimiterName, CsvMapping, CsvMappingProblem } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { LayoutNameDialog } from '@/components/imports/LayoutNameDialog';
import { errorMessage } from '@/lib/errorMessage';

import { csvDraftYaml, type CsvDraft } from './csvDraft';
import {
    DATE_FORMATS,
    SAMPLE_ROWS,
    choicesFromShape,
    guessChoices,
    plausibleDateFormats,
    sampleGrid,
    type WizardChoices,
} from './csvSniff';

/** A saved mapping by id, or a draft that exists nowhere yet. Never both. */
export type CsvMappingChoice = { mappingId?: string; mappingYaml?: string };

const DELIMITER_LABELS: Record<CsvDelimiterName, string> = {
    comma: 'Comma  ,',
    tab: 'Tab',
    semicolon: 'Semicolon  ;',
    pipe: 'Pipe  |',
};

/**
 * Describe a delimited file by pointing at its columns.
 *
 * <b>There is always a document on screen, and the dropdown only chooses where it came
 * from.</b> Selecting a saved layout used to hide the grid, the tabs, every control and
 * the document itself — so "reuse" meant picking a name and hoping, and "override" did
 * not exist at all: the only route to a corrected layout was copying YAML out of
 * settings, saving it under a new name, and deleting the old one. A saved layout now
 * opens in the form exactly like a draft, and can be updated in place.
 *
 * <b>The form is a view of the document, never a second source of truth.</b> A saved
 * document is loaded by asking the SERVER what it says — the browser does not parse
 * YAML and should not start, because a second reader could disagree with the one that
 * decides what actually gets imported.
 *
 * <b>Guided and YAML are TABS.</b> Two views of one document, only one of them the
 * source at a time; showing both at once invites editing the text while the controls
 * above quietly contradict it. The tab strip also makes the bypass discoverable, which
 * a collapsed section under a long form is not.
 *
 * The sample grid sits ABOVE the tabs, not inside one. Hand-writing YAML is exactly
 * when you most need to see which column is which, so switching to the editor must not
 * take the data away.
 */
export function CsvMappingStep({
    ledgerId, file, draft, onDraftChange, previewing, previewError,
    onChoose, onContinue, onBack, onCancel,
}: {
    ledgerId: string;
    file: File;
    draft: CsvDraft;
    onDraftChange: Dispatch<SetStateAction<CsvDraft>>;
    previewing: boolean;
    previewError: string | null;
    onChoose: (mapping: CsvMappingChoice) => void;
    onContinue: () => void;
    onBack: () => void;
    onCancel: () => void;
}) {
    const queryClient = useQueryClient();
    const saved = useQuery({
        queryKey: ['csv-mappings', ledgerId],
        queryFn: () => fetchCsvMappings(ledgerId),
        retry: false,
    });

    const [sample, setSample] = useState<string | null>(null);
    const [readError, setReadError] = useState<string | null>(null);
    const [problems, setProblems] = useState<CsvMappingProblem[] | null>(null);
    // Naming happens in a dialog. Revealed inline it had no way of going away again:
    // once shown it sat there through everything else, and the bar changed height
    // under the reader.
    const [naming, setNaming] = useState(false);

    const { choices, source, tab } = draft;
    const yaml = csvDraftYaml(draft);
    const savedYaml = source.kind === 'saved' ? source.savedYaml : null;
    const dirty = savedYaml !== null && yaml !== savedYaml;

    // Read a slice once. Only the head is needed: the grid shows a handful of rows and a
    // statement can be megabytes.
    useEffect(() => {
        let cancelled = false;
        setReadError(null);
        file.slice(0, 64 * 1024).text().then((text) => {
            if (cancelled) return;
            setSample(text);
            if (text.trim() === '') {
                setReadError('This file is empty.');
                return;
            }
            // Only when the draft has nothing yet. Coming BACK from the preview must not
            // re-sniff over the answers that produced it.
            onDraftChange((d) => (d.choices === null
                ? { ...d, choices: guessChoices(text) }
                : d));
        }).catch((e: unknown) => {
            if (cancelled) return;
            setSample('');
            // Previously swallowed, which left an empty grid, option-less dropdowns and
            // no clue why. A file can genuinely become unreadable between being chosen
            // and being read — moved, renamed, overwritten.
            setReadError(errorMessage(e, 'This file could not be read.'));
        });
        return () => { cancelled = true; };
    }, [file, onDraftChange]);

    const grid = useMemo(
        () => (sample !== null && choices !== null ? sampleGrid(sample, choices.delimiter) : []),
        [sample, choices]);
    const bodyRows = useMemo(() => grid.slice(choices?.headerRows ?? 0), [grid, choices]);
    const columnCount = grid.reduce((n, r) => Math.max(n, r.length), 0);

    // Pushed up as it changes rather than read at click time: the import must run under
    // the same mapping the preview used. A saved layout travels as its ID until it is
    // edited — after that the EDIT is what must be imported, not what is stored.
    const sourceId = source.kind === 'saved' ? source.id : null;
    useEffect(() => {
        if (sourceId !== null && !dirty) onChoose({ mappingId: sourceId });
        else if (yaml !== '') onChoose({ mappingYaml: yaml });
    }, [sourceId, dirty, yaml, onChoose]);

    /**
     * Re-read the document so the form shows what it actually says.
     *
     * The two tabs are two views of ONE document, which only holds if editing the text
     * updates the controls. It did not: the controls kept whatever they last held, and
     * a yellow box warned that they "show what it says" — which was untrue, and was
     * also shown after merely OPENING a saved layout, accusing the reader of a hand
     * edit they had not made. Re-reading is the honest version of that promise, and it
     * goes through the server because the browser must not parse YAML.
     */
    const reread = useMutation({
        mutationFn: (document: string) => validateCsvMapping(ledgerId, document),
        onSuccess: (verdict, document) => {
            setProblems(verdict.valid ? null : verdict.errors);
            onDraftChange((d) => ({
                ...d,
                choices: verdict.mapping !== null ? choicesFromShape(verdict.mapping) : d.choices,
                parsedFrom: verdict.mapping !== null ? document : d.parsedFrom,
            }));
        },
    });

    // Only when the form is actually on screen, and only when it has fallen behind.
    const stale = draft.override !== null && draft.override !== draft.parsedFrom;
    useEffect(() => {
        if (tab === 'guided' && stale && !reread.isPending) reread.mutate(yaml);
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [tab, stale, yaml]);

    /** Load a stored document, asking the server what it says so the form can show it. */
    const load = useMutation({
        mutationFn: async (row: CsvMapping) => ({
            row, verdict: await validateCsvMapping(ledgerId, row.definitionYaml),
        }),
        onSuccess: ({ row, verdict }) => {
            setProblems(verdict.valid ? null : verdict.errors);
            onDraftChange((d) => ({
                ...d,
                source: { kind: 'saved', id: row.id, name: row.name, savedYaml: row.definitionYaml },
                // Verbatim, so reopening a layout shows what its author wrote — comments,
                // spacing and all — rather than the form's re-rendering of it.
                override: row.definitionYaml,
                // A document the gate cannot read leaves the form showing the last thing
                // it could. The problem list says why, and the YAML tab can fix it.
                choices: verdict.mapping !== null ? choicesFromShape(verdict.mapping) : d.choices,
                parsedFrom: verdict.mapping !== null ? row.definitionYaml : d.parsedFrom,
                // Cleared, not set to the layout's name: this field is the value of
                // the name INPUT and nothing else, and Save-as-copy supplies its own
                // prefill, so a preloaded name was never read. Dead state rather than
                // a bug — the clash warning that appeared over an unopened name box
                // was fixed by scoping the warning to the input, and no test can tell
                // this line from its predecessor.
                name: '',
            }));
        },
    });

    const check = useMutation({
        mutationFn: () => validateCsvMapping(ledgerId, yaml),
        onSuccess: (verdict) => setProblems(verdict.errors),
    });

    // Save VALIDATES FIRST. The create endpoint refuses an invalid document, but its
    // 422 body is not ProblemDetails, so the thrown ApiError cannot carry the problem
    // list — validating first is what lets one click both save and explain itself.
    async function validated(): Promise<boolean> {
        const verdict = await validateCsvMapping(ledgerId, yaml);
        setProblems(verdict.errors);
        return verdict.valid;
    }

    const saveAs = useMutation({
        // The name comes in as an argument rather than being read off the draft: the
        // dialog owns the box, and a state update queued in the same tick would not
        // be visible here yet.
        mutationFn: async (name: string) => {
            if (!await validated()) {
                // Step out of the way. The problem list belongs beside the document it
                // describes, and leaving the dialog up would put it behind a modal.
                setNaming(false);
                return null;
            }
            return createCsvMapping(ledgerId, name, yaml);
        },
        onSuccess: (created) => { if (created !== null) { adopt(created); setNaming(false); } },
    });

    const overwrite = useMutation({
        mutationFn: async () => (source.kind === 'saved' && await validated()
            ? updateCsvMapping(ledgerId, source.id, source.name, yaml)
            : null),
        onSuccess: (row) => { if (row !== null) adopt(row); },
    });

    /** Become the stored row — so continuing imports under what now exists. */
    function adopt(row: CsvMapping) {
        queryClient.invalidateQueries({ queryKey: ['csv-mappings', ledgerId] });
        setNaming(false);
        onDraftChange((d) => ({
            ...d,
            source: { kind: 'saved', id: row.id, name: row.name, savedYaml: row.definitionYaml },
            override: row.definitionYaml,
            name: '',
        }));
    }

    function edit(patch: Partial<WizardChoices>) {
        onDraftChange((d) => (d.choices === null ? d : {
            ...d,
            choices: clampColumns({ ...d.choices, ...patch }, sample),
            // The form is the source again. A hand edit is discarded deliberately rather
            // than kept while the controls above contradict it.
            override: null,
        }));
        setProblems(null);
        check.reset();
        saveAs.reset();
        overwrite.reset();
    }

    /**
     * Keep every column number inside the file it describes.
     *
     * Changing the separator re-splits the grid, and a file that had four columns as
     * tab-separated may have one as comma-separated. The dropdowns then had no option
     * matching the stored number and rendered BLANK, while the document quietly kept
     * pointing at a column that no longer exists — and Check said nothing, because the
     * validator sees a document, not a file.
     */
    function clampColumns(next: WizardChoices, text: string | null): WizardChoices {
        if (text === null || text === '') return next;
        const width = sampleGrid(text, next.delimiter)
            .reduce((n, r) => Math.max(n, r.length), 0);
        if (width < 1) return next;
        const fit = (c: number) => Math.min(Math.max(1, c), width);
        return {
            ...next,
            dateColumn: fit(next.dateColumn),
            payeeColumn: fit(next.payeeColumn),
            memoColumn: next.memoColumn === null ? null : fit(next.memoColumn),
            amountColumn: fit(next.amountColumn),
            debitColumn: fit(next.debitColumn),
            creditColumn: fit(next.creditColumn),
        };
    }

    function update(patch: Partial<CsvDraft>) {
        onDraftChange((d) => ({ ...d, ...patch }));
    }

    function chooseSource(id: string) {
        setProblems(null);
        setNaming(false);
        check.reset();
        saveAs.reset();
        overwrite.reset();
        if (id === '') {
            // Back to describing this file. Re-guessed rather than left holding the
            // saved layout's answers, which would silently describe a different file.
            onDraftChange((d) => ({
                ...d,
                source: { kind: 'new' },
                override: null,
                name: '',
                choices: sample !== null && sample !== '' ? guessChoices(sample) : d.choices,
            }));
            return;
        }
        const row = (saved.data ?? []).find((m) => m.id === id);
        if (row !== undefined) load.mutate(row);
    }

    function columnOptions() {
        const first = bodyRows[0] ?? [];
        return Array.from({ length: columnCount }, (_, i) => {
            const value = (first[i] ?? '').trim();
            const shown = value.length > 22 ? value.slice(0, 22) + '…' : value;
            return (
                <option key={i + 1} value={i + 1}>
                    {i + 1}{shown === '' ? '' : ` — ${shown}`}
                </option>
            );
        });
    }

    const dateSamples = useMemo(
        () => bodyRows.slice(0, 5).map((r) => r[(choices?.dateColumn ?? 1) - 1] ?? ''),
        [bodyRows, choices]);
    const likelyFormats = useMemo(() => plausibleDateFormats(dateSamples), [dateSamples]);

    const tabClass = (active: boolean) =>
        'border-b-2 px-3 py-1.5 text-sm ' + (active
            ? 'border-accent font-medium text-text'
            : 'border-transparent text-text-muted hover:text-text');

    const busy = saveAs.isPending || overwrite.isPending || load.isPending;
    const saveError = saveAs.error ?? overwrite.error ?? load.error;
    // EVERY existing name, the loaded layout's own included. This button always
    // CREATES — Update is what changes a layout in place — so the name it is sitting
    // under is as unavailable as anyone else's. Excluding it let the one name most
    // likely to be typed sail past the check and fail at the unique index instead.
    const takenNames = (saved.data ?? []).map((m) => m.name.toLowerCase());

    return (
        <>
            <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-4 text-sm">
                {/* ONE PLACE FOR THE LAYOUT. Choosing one, saving one, updating one
                    and reverting one were spread across the top, middle and bottom of
                    the panel — three controls for one idea, read in three places. They
                    are all the same question: which layout is this, and what do you
                    want done with it. */}
                <div className="space-y-2 rounded border border-border bg-surface-muted/30 p-3">
                    <label className="block">
                        <span className="text-text-muted">Layout</span>
                        <select
                            className="mt-1 w-full rounded border border-border bg-surface p-2"
                            value={sourceId ?? ''}
                            onChange={(e) => chooseSource(e.target.value)}
                        >
                            {/* Not "Describe this file…", which named the SCREEN rather
                                than the choice and read as an instruction in a list of
                                nouns. This option is the absence of a saved layout. */}
                            <option value="">Not saved</option>
                            {(saved.data ?? []).map((m) => (
                                <option key={m.id} value={m.id}>{m.name}</option>
                            ))}
                        </select>
                    </label>

                    {saved.isError ? (
                        // Distinguished from "none saved yet", which otherwise looks
                        // identical: an empty dropdown either way.
                        <p role="alert" className="text-xs text-state-danger">
                            {errorMessage(saved.error, 'Could not load your saved layouts.')}
                            {' '}You can still describe this file.
                        </p>
                    ) : null}

                    {/* One weight for the whole row. Revert and Save as copy were
                        ghosts beside a bordered Update, which read as two bits of text
                        that happened to be clickable next to one real button. */}
                    <div className="flex flex-wrap items-center gap-2">
                        <span className="flex-1 text-xs text-text-muted">
                            {source.kind === 'new'
                                ? 'Not saved. Name it to use it again next month.'
                                : dirty
                                    ? 'Changed from the saved copy.'
                                    : 'Saved. Rename or delete it in Settings.'}
                        </span>
                        {source.kind === 'saved' && dirty ? (
                            <>
                                <Button
                                    type="button" variant="secondary" size="sm" disabled={busy}
                                    onClick={() => update({ override: source.savedYaml })}
                                >
                                    Revert
                                </Button>
                                <Button
                                    type="button" variant="secondary" size="sm" disabled={busy}
                                    onClick={() => setNaming(true)}
                                >
                                    Save as copy…
                                </Button>
                                <Button
                                    type="button" variant="secondary" size="sm" disabled={busy}
                                    onClick={() => overwrite.mutate()}
                                >
                                    {overwrite.isPending ? 'Updating…' : 'Update'}
                                </Button>
                            </>
                        ) : null}
                        {source.kind === 'new' ? (
                            <Button
                                type="button" variant="secondary" size="sm"
                                disabled={busy || yaml === ''}
                                onClick={() => setNaming(true)}
                            >
                                Save…
                            </Button>
                        ) : null}
                    </div>

                    {saveError ? (
                        <p role="alert" className="text-xs text-state-danger">
                            {errorMessage(saveError, 'Could not save the layout.')}
                        </p>
                    ) : null}
                </div>

                {choices !== null ? (
                    <>
                        {/* The file's own rows, so a column is pointed at rather
                            than counted. Header rows are struck through instead of
                            hidden: seeing what is skipped is how a wrong count is
                            noticed. */}
                        <div className="overflow-x-auto rounded border border-border">
                            <table className="w-full text-xs">
                                <thead>
                                    <tr className="bg-surface-header">
                                        {Array.from({ length: columnCount }, (_, i) => (
                                            <th key={i} className="px-2 py-1 text-left font-medium text-text-muted">
                                                {i + 1}
                                            </th>
                                        ))}
                                    </tr>
                                </thead>
                                <tbody>
                                    {grid.slice(0, SAMPLE_ROWS).map((row, r) => (
                                        <tr
                                            key={r}
                                            className={r < choices.headerRows
                                                ? 'text-text-subtle line-through decoration-1'
                                                : ''}
                                        >
                                            {Array.from({ length: columnCount }, (_, i) => (
                                                <td key={i} className="whitespace-nowrap px-2 py-1 font-mono">
                                                    {(row[i] ?? '').trim()}
                                                </td>
                                            ))}
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>

                        <div role="tablist" className="flex gap-1 border-b border-border">
                            <button
                                type="button"
                                role="tab"
                                aria-selected={tab === 'guided'}
                                className={tabClass(tab === 'guided')}
                                onClick={() => update({ tab: 'guided' })}
                            >
                                Guided
                            </button>
                            <button
                                type="button"
                                role="tab"
                                aria-selected={tab === 'yaml'}
                                className={tabClass(tab === 'yaml')}
                                onClick={() => update({ tab: 'yaml' })}
                            >
                                YAML
                            </button>
                        </div>

                        {tab === 'guided' ? (
                            <div role="tabpanel" className="space-y-3">
                                <div className="grid grid-cols-2 gap-3">
                                    <label className="block">
                                        <span className="text-text-muted">Separator</span>
                                        <select
                                            className="mt-1 w-full rounded border border-border bg-surface p-2"
                                            value={choices.delimiter}
                                            onChange={(e) => edit({ delimiter: e.target.value as CsvDelimiterName })}
                                        >
                                            {(Object.keys(DELIMITER_LABELS) as CsvDelimiterName[]).map((d) => (
                                                <option key={d} value={d}>{DELIMITER_LABELS[d]}</option>
                                            ))}
                                        </select>
                                    </label>
                                    <label className="block">
                                        <span className="text-text-muted">Header rows to skip</span>
                                        <input
                                            type="number"
                                            min={0}
                                            className="mt-1 w-full rounded border border-border bg-surface p-2"
                                            value={choices.headerRows}
                                            onChange={(e) => edit({ headerRows: Math.max(0, Number(e.target.value)) })}
                                        />
                                    </label>
                                    <label className="block">
                                        <span className="text-text-muted">Date column</span>
                                        <select
                                            className="mt-1 w-full rounded border border-border bg-surface p-2"
                                            value={choices.dateColumn}
                                            onChange={(e) => edit({ dateColumn: Number(e.target.value) })}
                                        >
                                            {columnOptions()}
                                        </select>
                                    </label>
                                    <label className="block">
                                        <span className="text-text-muted">Date looks like</span>
                                        <select
                                            className="mt-1 w-full rounded border border-border bg-surface p-2"
                                            value={choices.dateFormat}
                                            onChange={(e) => edit({ dateFormat: e.target.value })}
                                        >
                                            {/* Shapes that match the sample first; the rest
                                                stay reachable, because five rows cannot tell
                                                MM/dd from dd/MM. */}
                                            {[...likelyFormats, ...DATE_FORMATS.filter((f) => !likelyFormats.includes(f))]
                                                .map((f) => (<option key={f} value={f}>{f}</option>))}
                                        </select>
                                    </label>
                                    <label className="block">
                                        <span className="text-text-muted">Payee column</span>
                                        <select
                                            className="mt-1 w-full rounded border border-border bg-surface p-2"
                                            value={choices.payeeColumn}
                                            onChange={(e) => edit({ payeeColumn: Number(e.target.value) })}
                                        >
                                            {columnOptions()}
                                        </select>
                                    </label>
                                    <label className="block">
                                        <span className="text-text-muted">Memo column</span>
                                        <select
                                            className="mt-1 w-full rounded border border-border bg-surface p-2"
                                            value={choices.memoColumn ?? ''}
                                            onChange={(e) => edit({
                                                memoColumn: e.target.value === '' ? null : Number(e.target.value),
                                            })}
                                        >
                                            <option value="">None</option>
                                            {columnOptions()}
                                        </select>
                                    </label>
                                </div>

                                <fieldset className="rounded border border-border p-3">
                                    <legend className="px-1 text-xs text-text-muted">Amount</legend>
                                    <div className="space-y-2">
                                        <label className="flex items-center gap-2">
                                            <input
                                                type="radio"
                                                checked={choices.amountShape === 'signed'}
                                                onChange={() => edit({ amountShape: 'signed' })}
                                            />
                                            <span>One column, with a minus sign for money in</span>
                                        </label>
                                        {choices.amountShape === 'signed' ? (
                                            <div className="ml-6 space-y-2">
                                                <label className="block">
                                                    <span className="text-text-muted">Amount column</span>
                                                    <select
                                                        className="mt-1 w-full rounded border border-border bg-surface p-2"
                                                        value={choices.amountColumn}
                                                        onChange={(e) => edit({ amountColumn: Number(e.target.value) })}
                                                    >
                                                        {columnOptions()}
                                                    </select>
                                                </label>
                                                {/* Phrased as a question about the FILE.
                                                    Nobody knows whether their mapping should
                                                    invert; everybody knows whether a purchase
                                                    shows up positive on their statement. */}
                                                <label className="flex items-start gap-2">
                                                    <input
                                                        type="checkbox"
                                                        className="mt-1"
                                                        checked={choices.amountInvert}
                                                        onChange={(e) => edit({ amountInvert: e.target.checked })}
                                                    />
                                                    <span>
                                                        A purchase shows as a <strong>positive</strong> number
                                                        in this file
                                                        <span className="block text-xs text-text-muted">
                                                            Usual for credit-card exports, where a charge
                                                            increases what you owe. Coffer records a purchase
                                                            as money out, so these get flipped.
                                                        </span>
                                                    </span>
                                                </label>
                                            </div>
                                        ) : null}

                                        <label className="flex items-center gap-2">
                                            <input
                                                type="radio"
                                                checked={choices.amountShape === 'debit_credit'}
                                                onChange={() => edit({ amountShape: 'debit_credit' })}
                                            />
                                            <span>Separate columns for money out and money in</span>
                                        </label>
                                        {choices.amountShape === 'debit_credit' ? (
                                            <div className="ml-6 grid grid-cols-2 gap-3">
                                                <label className="block">
                                                    <span className="text-text-muted">Money out (debit)</span>
                                                    <select
                                                        className="mt-1 w-full rounded border border-border bg-surface p-2"
                                                        value={choices.debitColumn}
                                                        onChange={(e) => edit({ debitColumn: Number(e.target.value) })}
                                                    >
                                                        {columnOptions()}
                                                    </select>
                                                </label>
                                                <label className="block">
                                                    <span className="text-text-muted">Money in (credit)</span>
                                                    <select
                                                        className="mt-1 w-full rounded border border-border bg-surface p-2"
                                                        value={choices.creditColumn}
                                                        onChange={(e) => edit({ creditColumn: Number(e.target.value) })}
                                                    >
                                                        {columnOptions()}
                                                    </select>
                                                </label>
                                            </div>
                                        ) : null}
                                    </div>
                                </fieldset>
                            </div>
                        ) : (
                            <div role="tabpanel" className="space-y-2">
                                <p className="text-xs text-text-muted">
                                    The same document the Guided tab writes. Editing here takes
                                    over; changing a control there hands it back.
                                </p>
                                {/* Scrolls sideways rather than wrapping. YAML is
                                    indentation-significant, and a soft-wrapped line puts
                                    its continuation at column 0 — which reads exactly
                                    like a new key at the wrong depth. Seeing the
                                    structure is most of what the editor is for. */}
                                <textarea
                                    aria-label="Mapping YAML"
                                    wrap="off"
                                    className="h-72 w-full whitespace-pre rounded border border-border bg-surface p-2 font-mono text-xs"
                                    value={yaml}
                                    spellCheck={false}
                                    onChange={(e) => {
                                        update({ override: e.target.value });
                                        setProblems(null);
                                        check.reset();
                                        saveAs.reset();
                                        overwrite.reset();
                                    }}
                                />
                            </div>
                        )}

                        {problems !== null && problems.length > 0 ? (
                            <div
                                role="alert"
                                className="rounded border border-state-warning/40 bg-state-warning-soft px-3 py-2 text-xs text-state-warning"
                            >
                                <p className="font-medium">
                                    {problems.length} problem{problems.length === 1 ? '' : 's'}:
                                </p>
                                <ul className="mt-1 list-disc pl-4">
                                    {problems.map((p, i) => (
                                        <li key={i}>
                                            {p.line !== null ? `line ${p.line}: ` : ''}
                                            <span className="font-mono">
                                                {p.path === '' ? '(document)' : p.path}
                                            </span>
                                            {' — '}
                                            {p.message}
                                        </li>
                                    ))}
                                </ul>
                            </div>
                        ) : null}

                        {problems !== null && problems.length === 0 ? (
                            <p role="status" className="text-xs text-state-success">
                                Looks valid.
                            </p>
                        ) : null}

                    </>
                ) : null}

                {naming ? (
                    <LayoutNameDialog
                        title={source.kind === 'saved' ? 'Save as a new layout' : 'Save this layout'}
                        label="Name"
                        initialName={source.kind === 'saved' ? `${source.name} copy` : ''}
                        submitLabel="Save"
                        pending={saveAs.isPending}
                        error={saveAs.isError
                            ? errorMessage(saveAs.error, 'Could not save the layout.')
                            : null}
                        // Every existing name, the loaded layout's own included: this
                        // always CREATES, and Update is what changes one in place.
                        validateName={(n) => (takenNames.includes(n.toLowerCase())
                            ? 'A layout is already called that.'
                            : null)}
                        onSubmit={(n) => { update({ name: n }); saveAs.mutate(n); }}
                        onClose={() => { setNaming(false); saveAs.reset(); }}
                    />
                ) : null}

                {readError !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{readError}</p>
                ) : null}

                {previewError !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{previewError}</p>
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
                    variant="secondary"
                    size="sm"
                    disabled={check.isPending || yaml === ''}
                    onClick={() => check.mutate()}
                >
                    {check.isPending ? 'Checking…' : 'Check'}
                </Button>
                <Button
                    type="button"
                    variant="primary"
                    size="sm"
                    // Dead until there IS a document. It was disabled only while
                    // previewing, so during the moment after a file change — before the
                    // new file had been read — Continue was live and ran the NEW file
                    // against the PREVIOUS file's mapping.
                    disabled={previewing || yaml === ''}
                    onClick={onContinue}
                >
                    {previewing ? 'Reading…' : 'Continue →'}
                </Button>
            </footer>
        </>
    );
}
