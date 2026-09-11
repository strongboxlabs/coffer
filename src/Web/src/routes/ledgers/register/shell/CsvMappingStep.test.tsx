import { useMemo, useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import * as apiModule from '@/lib/api';

import { withoutComments } from './csvSniff';
import { CsvMappingStep, type CsvMappingChoice } from './CsvMappingStep';
import { EMPTY_CSV_DRAFT, type CsvDraft } from './csvDraft';

/**
 * The guided mapping step (ADR-0031 Phase 5).
 *
 * The first version of this was a YAML textarea, which is not a wizard — nobody
 * importing a statement should have to know that columns count from 1 or that `M` is
 * month. These tests are about the guided path: seeing your own rows, pointing at
 * columns, and answering a question about the FILE rather than about the mapping.
 *
 * Synthetic throughout: invented merchants, `ANYTOWN ST`, tab-separated with a .csv
 * name because that is the shape a real export turned out to have.
 */
const STORE_CARD_TSV = [
    '09/02/2026\t$41.18\tSAMPLE.COM              ANYTOWN      ST\tpurchase',
    '09/03/2026\t$7.99\tSAMPLE STORE            ANYTOWN      ST\tpurchase',
    '09/04/2026\t-$23.50\tSAMPLE STORE            ANYTOWN      ST\tpurchase',
].join('\n');

/**
 * Stands in for the dialog, which owns the draft.
 *
 * Held here rather than inside the step because the real dialog unmounts the step on
 * every move between screens — so state kept inside it does not survive Back from the
 * preview, which is how an entire hand-written mapping used to be lost.
 */
function Harness({ body, filename, onChoose }: {
    body: string;
    filename: string;
    onChoose: (m: CsvMappingChoice) => void;
}) {
    const [draft, setDraft] = useState<CsvDraft>(EMPTY_CSV_DRAFT);
    // ONE File for the life of the harness. Built inline it was a new object on every
    // render, so the step's read effect re-fired on each one — which is also what the
    // real dialog does: it holds the File in state and hands the same one down.
    const file = useMemo(() => new File([body], filename, { type: 'text/csv' }), [body, filename]);
    return (
        <CsvMappingStep
            ledgerId="led-1"
            file={file}
            draft={draft}
            onDraftChange={setDraft}
            previewing={false}
            previewError={null}
            onChoose={onChoose}
            onContinue={vi.fn()}
            onBack={vi.fn()}
            onCancel={vi.fn()}
        />
    );
}

function renderStep(body = STORE_CARD_TSV, filename = 'unbilled.csv') {
    const onChoose = vi.fn<(m: CsvMappingChoice) => void>();
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    render(
        <QueryClientProvider client={queryClient}>
            <Harness body={body} filename={filename} onChoose={onChoose} />
        </QueryClientProvider>,
    );
    return { user: userEvent.setup(), onChoose };
}

/** The document most recently handed up, once the file has been read. */
/** The parsed shape a valid verdict carries, matching the sample file below. */
const SAMPLE_SHAPE = {
    version: 1,
    delimiter: 'tab',
    headerRows: 0,
    footerRows: 0,
    dateColumn: 1,
    dateFormat: 'MM/dd/yyyy',
    payeeColumn: 3,
    memoColumn: null,
    amountShape: 'signed',
    amountColumn: 2,
    amountInvert: false,
    debitColumn: null,
    creditColumn: null,
} as const;

/** Open the naming dialog from whichever button offers it, and fill it in. */
async function nameItAndSave(user: ReturnType<typeof userEvent.setup>, name: string) {
    await user.click(await screen.findByRole('button', { name: /^save(…| as copy…)$/i }));
    const box = await screen.findByLabelText(/^name$/i);
    await user.clear(box);
    await user.type(box, name);
    await user.click(screen.getByRole('button', { name: /^(save|rename)$/i }));
}

async function latestYaml(onChoose: ReturnType<typeof vi.fn>): Promise<string> {
    await waitFor(() => expect(onChoose).toHaveBeenCalled());
    return (onChoose.mock.calls.at(-1)![0] as CsvMappingChoice).mappingYaml ?? '';
}

describe('CsvMappingStep', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        vi.spyOn(apiModule, 'fetchCsvMappings').mockResolvedValue([
            {
                id: 'map-1',
                name: 'Store card statement',
                definitionYaml: SAVED_YAML,
                schemaVersion: 1,
                createdAt: '2026-09-01T00:00:00Z',
                updatedAt: '2026-09-01T00:00:00Z',
            },
        ]);
    });

    it('shows the file\'s own rows, so a column is pointed at rather than counted', async () => {
        // THE thing that makes this a wizard rather than a text editor.
        const { onChoose } = renderStep();
        await latestYaml(onChoose);

        const grid = await screen.findByRole('table');
        expect(within(grid).getByText('$41.18')).toBeTruthy();
        expect(within(grid).getByText('-$23.50')).toBeTruthy();
        // Column numbers are headers, so the pickers below can be matched to the grid.
        expect(within(grid).getByRole('columnheader', { name: '1' })).toBeTruthy();
    });

    it('keeps the data on screen when the YAML tab takes over', async () => {
        // The grid is context for BOTH views, not one tab's content. Hand-writing YAML
        // is exactly when you most need to see which column is which, so switching must
        // not take the file away — reported from a screenshot after the first attempt
        // put the grid inside the Guided panel.
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('table');

        await user.click(screen.getByRole('tab', { name: /yaml/i }));

        const grid = screen.getByRole('table');
        expect(within(grid).getByText('$41.18')).toBeTruthy();
        // ...while the controls it belongs to are gone.
        expect(screen.queryByLabelText(/date column/i)).toBeNull();
        expect(screen.getByLabelText(/mapping yaml/i)).toBeTruthy();
    });

    it('works out the separator without being told', async () => {
        // The file calls itself .csv and is tab-separated. Noticing that is the
        // wizard's job, not the user's.
        const { onChoose } = renderStep();
        expect(await latestYaml(onChoose)).toContain('delimiter: tab');
    });

    it('labels each column with one of its values', async () => {
        // "Column 2" means nothing; "2 — $41.18" is recognisable at a glance.
        const { onChoose } = renderStep();
        await latestYaml(onChoose);

        const dateColumn = await screen.findByLabelText(/date column/i);
        expect(within(dateColumn).getByRole('option', { name: /1 — 09\/02\/2026/ })).toBeTruthy();
        expect(within(dateColumn).getByRole('option', { name: /2 — \$41\.18/ })).toBeTruthy();
    });

    it('asks about the FILE, not about inverting, and writes invert from the answer', async () => {
        // Nobody knows whether their mapping should invert. Everybody knows whether a
        // purchase shows up positive on their own statement.
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);

        await user.click(screen.getByRole('checkbox', { name: /purchase shows as a positive number/i }));

        await waitFor(async () => expect(await latestYaml(onChoose)).toContain('invert: true'));
    });

    it('builds the document from the pickers', async () => {
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);

        await user.selectOptions(screen.getByLabelText(/payee column/i), '3');
        await user.selectOptions(screen.getByLabelText(/date looks like/i), 'MM/dd/yyyy');

        const yaml = await latestYaml(onChoose);
        expect(yaml).toContain('payee:\n  column: 3');
        expect(yaml).toContain('format: MM/dd/yyyy');
    });

    it('offers only the amount columns the chosen shape uses', async () => {
        // Emitting both shapes' keys would produce a document the validator refuses,
        // so the form must not let both be filled in.
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);

        // Exact label: the invert checkbox's explanation also says "money out", so a
        // loose match finds three elements.
        expect(screen.getByLabelText(/amount column/i)).toBeTruthy();
        expect(screen.queryByLabelText('Money out (debit)')).toBeNull();

        await user.click(screen.getByRole('radio', { name: /separate columns/i }));

        expect(screen.getByLabelText('Money out (debit)')).toBeTruthy();
        expect(screen.queryByLabelText(/amount column/i)).toBeNull();
        const yaml = await latestYaml(onChoose);
        expect(yaml).toContain('shape: debit_credit');
        // Past the comments: the document now OFFERS the signed shape as a
        // commented-out alternative, so a raw text search finds `invert` there. What
        // must not happen is emitting it as a key, which the validator rejects under
        // this shape.
        expect(withoutComments(yaml)).not.toContain('invert');
    });

    it('shows the YAML the form built, as a bypass rather than the front door', async () => {
        // One document either way. If the editor showed something the form had not
        // built, switching between them would silently change the mapping.
        const { onChoose } = renderStep();
        const handedUp = await latestYaml(onChoose);

        // Behind a tab, and NOT visible next to the form: two views of one document,
        // only one of them the source at a time.
        expect(screen.queryByLabelText(/mapping yaml/i)).toBeNull();

        await userEvent.setup().click(screen.getByRole('tab', { name: /yaml/i }));
        const editor = await screen.findByLabelText(/mapping yaml/i);
        expect((editor as HTMLTextAreaElement).value).toBe(handedUp);
        // ...and the guided controls are gone while it is showing.
        expect(screen.queryByLabelText(/date column/i)).toBeNull();
    });

    it('scrolls the editor sideways instead of wrapping it', async () => {
        // YAML is indentation-significant and a soft-wrapped line puts its continuation
        // at column 0, where it reads as a new key at the wrong depth. jsdom does no
        // layout, so the attribute is the only seam there is — this pins the mechanism,
        // not the pixels.
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);

        await user.click(screen.getByRole('tab', { name: /yaml/i }));
        const editor = await screen.findByLabelText(/mapping yaml/i);

        expect(editor.getAttribute('wrap')).toBe('off');
        expect(editor.className).toContain('whitespace-pre');
    });

    it('lets a hand edit take over, and a control take it back', async () => {
        // The bypass has to actually bypass; and a form control that silently did
        // nothing because an edit was still in force would be worse than no bypass.
        vi.spyOn(apiModule, 'validateCsvMapping').mockResolvedValue({
            valid: false, errors: [{ path: 'date', line: null, message: 'Required, and missing.' }],
            mapping: null,
        });
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);

        await user.click(screen.getByRole('tab', { name: /yaml/i }));
        const editor = await screen.findByLabelText(/mapping yaml/i);
        await user.clear(editor);
        await user.type(editor, 'version: 1');
        expect(await latestYaml(onChoose)).toBe('version: 1');

        await user.click(screen.getByRole('tab', { name: /guided/i }));
        await user.selectOptions(screen.getByLabelText(/payee column/i), '4');
        const rebuilt = await latestYaml(onChoose);
        expect(rebuilt).toContain('payee:\n  column: 4');
        expect(rebuilt).toContain('delimiter: tab');
    });

    it('re-reads a hand edit so the controls really do show the document', async () => {
        // The tabs claim to be two views of ONE document, and that only holds if
        // editing the text moves the controls. It did not — they kept whatever they
        // last held, while a yellow box asserted they "show what it says". The box was
        // also shown after merely OPENING a saved layout, accusing the reader of a
        // hand edit they had not made.
        vi.spyOn(apiModule, 'validateCsvMapping').mockResolvedValue({
            valid: true, errors: [], mapping: { ...SAMPLE_SHAPE, payeeColumn: 4 },
        });
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        expect((screen.getByLabelText(/payee column/i) as HTMLSelectElement).value).toBe('3');

        await user.click(screen.getByRole('tab', { name: /yaml/i }));
        const editor = await screen.findByLabelText(/mapping yaml/i);
        await user.clear(editor);
        await user.type(editor, 'version: 1');

        await user.click(screen.getByRole('tab', { name: /guided/i }));

        await waitFor(() =>
            expect((screen.getByLabelText(/payee column/i) as HTMLSelectElement).value).toBe('4'));
        // ...and no accusation of hand-editing anywhere on screen.
        expect(screen.queryByText(/edited by hand/i)).toBeNull();

        // IT HAS TO SETTLE. The re-read fires while the form is behind the document, so
        // forgetting to record what was read leaves it behind forever — an unbounded
        // loop of validate calls that no assertion about the rendered value would ever
        // notice, because the value is right on the first pass.
        const calls = vi.mocked(apiModule.validateCsvMapping).mock.calls.length;
        await new Promise((r) => setTimeout(r, 50));
        expect(vi.mocked(apiModule.validateCsvMapping).mock.calls.length).toBe(calls);
        expect(calls).toBeLessThanOrEqual(2);

        // And it REMEMBERS what it read: leaving the tab and coming back to the same
        // unchanged document must not read it again.
        await user.click(screen.getByRole('tab', { name: /yaml/i }));
        await screen.findByLabelText(/mapping yaml/i);
        await user.click(screen.getByRole('tab', { name: /guided/i }));
        await screen.findByLabelText(/payee column/i);
        await new Promise((r) => setTimeout(r, 50));

        expect(vi.mocked(apiModule.validateCsvMapping).mock.calls.length).toBe(calls);
    });

    it('names the empty choice after the choice, not after the screen', async () => {
        // It read "Describe this file…" — the heading of the screen it was already on,
        // phrased as an instruction, sitting in a list of layout names. The option
        // means "no saved layout", so that is what it says.
        const { onChoose } = renderStep();
        await latestYaml(onChoose);

        const picker = screen.getByLabelText(/^layout$/i) as HTMLSelectElement;
        expect(picker.options[0]!.textContent).toBe('Not saved');
        expect(picker.options[0]!.value).toBe('');
    });

    it('does not re-read a document that has not changed', async () => {
        // What `parsedFrom` is actually for. Not a loop guard — the effect is
        // deps-guarded, so I was wrong about that — but without it every trip to the
        // YAML tab and back spends a round trip re-reading a document nobody touched.
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });
        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');
        await screen.findByText(/rename or delete it in settings/i);

        // ONE. Loading already reads the document to fill the form; reading it again
        // immediately is pure waste, and the waste lands before any later snapshot —
        // so this has to be an absolute count, not a "did it grow" comparison. A
        // relative one passed happily at two.
        await new Promise((r) => setTimeout(r, 50));
        expect(vi.mocked(apiModule.validateCsvMapping).mock.calls.length).toBe(1);

        // ...and going to the YAML tab and back reads nothing, because nothing changed.
        await user.click(screen.getByRole('tab', { name: /yaml/i }));
        await screen.findByLabelText(/mapping yaml/i);
        await user.click(screen.getByRole('tab', { name: /guided/i }));
        await screen.findByLabelText(/payee column/i);
        await new Promise((r) => setTimeout(r, 50));

        expect(vi.mocked(apiModule.validateCsvMapping).mock.calls.length).toBe(1);
    });

    it('gives every button in the Layout bar one weight', async () => {
        // Reported: "the save as and revert styling is different than other buttons".
        // Revert and Save as copy were ghosts beside a bordered Update, so two of the
        // three read as text that happened to be clickable. Asserted as "identical to
        // each other" rather than against a class name, so restyling the button system
        // does not break this while the inconsistency it guards stays fixed.
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });
        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');
        await screen.findByText(/rename or delete it in settings/i);
        await user.selectOptions(screen.getByLabelText(/payee column/i), '3');

        const revert = await screen.findByRole('button', { name: /^revert$/i });
        const copy = screen.getByRole('button', { name: /save as copy/i });
        const update = screen.getByRole('button', { name: /^update$/i });

        expect(copy.className).toBe(revert.className);
        expect(update.className).toBe(revert.className);
    });

    it('gets out of the way when the document it was asked to save is invalid', async () => {
        // The problem list belongs beside the document it describes. Leaving the
        // naming dialog up put it behind a modal, where the answer to "why did that
        // not save" is unreadable.
        vi.spyOn(apiModule, 'validateCsvMapping').mockResolvedValue({
            valid: false, mapping: null,
            errors: [{ path: 'amount.column', line: 11, message: 'Columns are numbered from 1.' }],
        });
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);

        await nameItAndSave(user, 'Mine');

        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        expect((await screen.findByRole('alert')).textContent).toMatch(/amount\.column/);
    });

    it('does not complain about a name nobody is typing', async () => {
        // Reported from a screenshot: "A layout is already called that." sat under a
        // loaded layout with no name box on screen at all. Two causes, both mine — the
        // draft's name was preloaded with the layout's own name, which is by definition
        // taken, and the warning was a sibling of the whole bar rather than of the
        // input it describes.
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });

        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');
        await screen.findByText(/rename or delete it in settings/i);
        expect(screen.queryByText(/already called that/i)).toBeNull();

        // Still absent once it is changed, because the name box lives in a dialog
        // nobody has opened.
        await user.selectOptions(screen.getByLabelText(/payee column/i), '3');
        await screen.findByText(/changed from the saved copy/i);
        expect(screen.queryByText(/already called that/i)).toBeNull();
        expect(screen.queryByLabelText(/^name$/i)).toBeNull();
    });

    it('takes the name warning away with the box it belongs to', async () => {
        // The warning and the box it describes now leave together, because they are
        // the same dialog. Inline, they were siblings of the whole bar: closing the
        // box left the complaint behind, about a name in a field that was gone. A
        // dialog removes the class of bug rather than this instance of it.
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });
        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');
        await screen.findByText(/rename or delete it in settings/i);
        await user.selectOptions(screen.getByLabelText(/payee column/i), '3');

        await user.click(await screen.findByRole('button', { name: /save as copy/i }));
        const nameBox = await screen.findByLabelText(/^name$/i);
        await user.clear(nameBox);
        await user.type(nameBox, 'Store card statement');
        expect(await screen.findByText(/already called that/i)).toBeTruthy();

        // Scoped: the step's own footer has a Cancel too, sitting behind the backdrop.
        await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: /cancel/i }));

        expect(screen.queryByLabelText(/^name$/i)).toBeNull();
        expect(screen.queryByText(/already called that/i)).toBeNull();
        // ...and the layout is untouched by having asked.
        expect(await screen.findByText(/changed from the saved copy/i)).toBeTruthy();
    });

    it('does not accuse you of hand-editing a layout you only opened', async () => {
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });

        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');
        await screen.findByText(/rename or delete it in settings/i);

        expect(screen.queryByText(/edited by hand/i)).toBeNull();
    });

    it('names every problem at once, with its key and line', async () => {
        vi.spyOn(apiModule, 'validateCsvMapping').mockResolvedValue({
            valid: false,
            mapping: null,
            errors: [
                { path: 'delimiter', line: 2, message: "'tabs' is not a delimiter." },
                { path: 'amount.column', line: 11, message: 'Columns are numbered from 1.' },
            ],
        });

        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await user.click(screen.getByRole('button', { name: /check/i }));

        const alert = await screen.findByRole('alert');
        expect(alert.textContent).toMatch(/2 problems/i);
        expect(alert.textContent).toMatch(/line 2/);
        expect(alert.textContent).toMatch(/amount\.column/);
    });

    it('offers saving from the start, not behind a Check nobody knew to press', async () => {
        // The affordance was previously revealed only after a successful Check, which
        // is why a first-time user reported there was no way to save at all.
        const { onChoose } = renderStep();
        await latestYaml(onChoose);

        // The button is on screen from the start; the box it opens is a dialog, which
        // can be dismissed. Revealed inline it had no way of going away again.
        expect(screen.getByRole('button', { name: /^save…$/i })).toBeTruthy();
        expect(screen.queryByLabelText(/^name$/i)).toBeNull();
    });

    it('refuses to store an invalid document, and says why, from the Save button', async () => {
        // Save validates first: the create endpoint would refuse anyway, but its 422
        // body is not ProblemDetails, so the thrown error cannot carry the problems.
        const create = vi.spyOn(apiModule, 'createCsvMapping');
        vi.spyOn(apiModule, 'validateCsvMapping').mockResolvedValue({
            valid: false,
            mapping: null,
            errors: [{ path: 'amount.column', line: 11, message: 'Columns are numbered from 1.' }],
        });

        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);

        await nameItAndSave(user, 'Mine');

        const alert = await screen.findByRole('alert');
        expect(alert.textContent).toMatch(/amount\.column/);
        expect(create).not.toHaveBeenCalled();
    });

    it('clears a stale verdict when a control changes under it', async () => {
        // A green tick from two edits ago describes a document that no longer exists.
        vi.spyOn(apiModule, 'validateCsvMapping').mockResolvedValue({ valid: true, errors: [], mapping: SAMPLE_SHAPE });

        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await user.click(screen.getByRole('button', { name: /check/i }));
        await waitFor(() => expect(screen.getByRole('status').textContent).toMatch(/looks valid/i));

        await user.selectOptions(screen.getByLabelText(/payee column/i), '4');

        await waitFor(() => expect(screen.queryByText(/looks valid/i)).toBeNull());
    });

    /** The stored layout the picker offers, and what the server says it means. */
    const SAVED_YAML = 'version: 1\n# hand-written\ndelimiter: tab\n';

    function mockLoadOfSavedLayout() {
        vi.spyOn(apiModule, 'validateCsvMapping').mockResolvedValue({
            valid: true, errors: [], mapping: { ...SAMPLE_SHAPE, payeeColumn: 4 },
        });
    }

    it('opens a saved layout in the form instead of hiding everything', async () => {
        // The defect this replaces: choosing a saved mapping unmounted the grid, the
        // tabs, every control and the document, so reuse meant picking a name and
        // hoping — and override was impossible, because there was nothing to edit.
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });

        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');

        await waitFor(() =>
            expect(onChoose).toHaveBeenLastCalledWith({ mappingId: 'map-1' }));
        // Still a wizard: the file and the controls are right there to judge it by.
        expect(screen.getByRole('table')).toBeTruthy();
        expect(screen.getByLabelText(/date column/i)).toBeTruthy();
        // Populated from the SERVER's reading of the document, not a parser of our own.
        await waitFor(() =>
            expect((screen.getByLabelText(/payee column/i) as HTMLSelectElement).value).toBe('4'));
    });

    it('keeps a saved document verbatim, comments and all', async () => {
        // A document meant to be hand-edited has to reopen as it was left. Re-rendering
        // it from the form would silently drop whatever its author wrote in it.
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });

        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');
        await waitFor(() =>
            expect(onChoose).toHaveBeenLastCalledWith({ mappingId: 'map-1' }));
        await user.click(screen.getByRole('tab', { name: /yaml/i }));

        expect((await screen.findByLabelText(/mapping yaml/i) as HTMLTextAreaElement).value)
            .toBe(SAVED_YAML);
    });

    it('updates a saved layout in place rather than making a second one', async () => {
        // "How do I override one" had no answer at all: the only route to a corrected
        // layout was copying the YAML out of settings, saving it under a new name and
        // deleting the original, which is how people end up with "Chase card 2".
        mockLoadOfSavedLayout();
        const update = vi.spyOn(apiModule, 'updateCsvMapping').mockResolvedValue({
            id: 'map-1', name: 'Store card statement', definitionYaml: 'version: 1',
            schemaVersion: 1,
            createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-02T00:00:00Z',
        });

        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });
        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');
        await screen.findByText(/rename or delete it in settings/i);

        // Nothing to update until something changes.
        expect(screen.queryByRole('button', { name: /^update/i })).toBeNull();

        await user.selectOptions(screen.getByLabelText(/payee column/i), '3');

        await user.click(await screen.findByRole('button', { name: /^update/i }));

        await waitFor(() => expect(update).toHaveBeenCalled());
        expect(update.mock.calls[0]![1]).toBe('map-1');
        expect(update.mock.calls[0]![2]).toBe('Store card statement');
        expect(update.mock.calls[0]![3]).toContain('payee:\n  column: 3');
    });

    it('imports an edited layout as the EDIT, not as the row it came from', async () => {
        // The quiet one. A saved layout travels as its id, which is right until you
        // change it — after that, importing under the id runs the STORED document and
        // silently ignores every correction just made on screen.
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });
        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');

        await waitFor(() =>
            expect(onChoose).toHaveBeenLastCalledWith({ mappingId: 'map-1' }));

        await user.selectOptions(screen.getByLabelText(/payee column/i), '3');

        await waitFor(() => {
            const last = onChoose.mock.calls.at(-1)![0] as CsvMappingChoice;
            expect(last.mappingId).toBeUndefined();
            expect(last.mappingYaml).toContain('payee:\n  column: 3');
        });
    });

    it('keeps the columns inside the file when the separator changes', async () => {
        // A four-column tab file is a ONE-column comma file. The dropdowns then had no
        // option matching the stored number and rendered blank, while the document
        // quietly kept pointing at column 3 — and Check said nothing, because the
        // validator sees a document, not a file.
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        expect((screen.getByLabelText(/payee column/i) as HTMLSelectElement).value).toBe('3');

        await user.selectOptions(screen.getByLabelText(/separator/i), 'comma');

        const payee = screen.getByLabelText(/payee column/i) as HTMLSelectElement;
        // Not blank: the control shows a column the file actually has.
        expect(payee.value).not.toBe('');
        expect(Number(payee.value)).toBeLessThanOrEqual(payee.options.length);
        // ...and the document agrees with the control.
        expect(await latestYaml(onChoose)).toContain(`payee:\n  column: ${payee.value}`);
    });

    it('will not continue while there is no document to continue with', async () => {
        // Continue was disabled only while a preview was in flight, so in the window
        // before a file had been read it was live — and ran the new file against
        // whatever mapping the dialog was still holding.
        const { onChoose } = renderStep('   \n');

        // An empty file yields no document at all; say so, and leave the button dead.
        expect(await screen.findByText(/this file is empty/i)).toBeTruthy();
        expect((screen.getByRole('button', { name: /continue/i }) as HTMLButtonElement).disabled)
            .toBe(true);
        expect(onChoose).not.toHaveBeenCalled();
    });

    it('puts a changed layout back the way it was stored', async () => {
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });
        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');
        await screen.findByText(/rename or delete it in settings/i);

        await user.selectOptions(screen.getByLabelText(/payee column/i), '3');
        await screen.findByText(/changed from the saved/i);

        await user.click(screen.getByRole('button', { name: /revert/i }));

        // Back to the stored text exactly — including the comment the form cannot write.
        await user.click(screen.getByRole('tab', { name: /yaml/i }));
        expect((await screen.findByLabelText(/mapping yaml/i) as HTMLTextAreaElement).value)
            .toBe(SAVED_YAML);
        expect(screen.queryByRole('button', { name: /^update/i })).toBeNull();
    });

    it('offers nothing to save while a saved layout is untouched', async () => {
        // Nothing to do, so nothing on offer. The old bar showed a name box reading
        // back the layout's own name beside a dead Save button — a control whose only
        // possible outcome was an error from the unique index.
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });
        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');
        await screen.findByText(/rename or delete it in settings/i);

        expect(screen.queryByRole('button', { name: /^save…$/i })).toBeNull();
        expect(screen.queryByRole('button', { name: /save as copy/i })).toBeNull();
        expect(screen.queryByRole('button', { name: /^update$/i })).toBeNull();
    });

    it('names a copy only when you ask for one, and refuses a name already taken', async () => {
        mockLoadOfSavedLayout();
        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await screen.findByRole('option', { name: /store card statement/i });
        await user.selectOptions(screen.getByLabelText(/^layout$/i), 'map-1');
        await screen.findByText(/rename or delete it in settings/i);
        await user.selectOptions(screen.getByLabelText(/payee column/i), '3');

        await user.click(await screen.findByRole('button', { name: /save as copy/i }));

        const nameBox = await screen.findByLabelText(/^name$/i);
        // Prefilled with something that is not already taken, so Save works as-is.
        expect((nameBox as HTMLInputElement).value).toBe('Store card statement copy');
        expect((screen.getByRole('button', { name: /^save$/i }) as HTMLButtonElement).disabled)
            .toBe(false);

        // ...and typing a name that IS taken says so while it is being typed.
        await user.clear(nameBox);
        await user.type(nameBox, 'Store card statement');
        expect(await screen.findByText(/already called that/i)).toBeTruthy();
        expect((screen.getByRole('button', { name: /^save$/i }) as HTMLButtonElement).disabled)
            .toBe(true);
    });

    it('tells you the saved list failed, rather than looking empty', async () => {
        // An empty dropdown and a broken one look identical, and the difference decides
        // whether you go describe the file again or go and fix something.
        vi.spyOn(apiModule, 'fetchCsvMappings')
            .mockRejectedValue(new Error('the server said no'));
        const { onChoose } = renderStep();
        await latestYaml(onChoose);

        // The server's own words, not a generic fallback: errorMessage prefers the
        // error's message, and "the list is broken" is only actionable if it says how.
        const alert = await screen.findByRole('alert');
        expect(alert.textContent).toContain('the server said no');
        expect(alert.textContent).toContain('You can still describe this file');
        // ...and describing this file still works.
        expect(screen.getByLabelText(/date column/i)).toBeTruthy();
    });

    it('switches to the saved row after saving, not the draft that matched it', async () => {
        vi.spyOn(apiModule, 'validateCsvMapping').mockResolvedValue({ valid: true, errors: [], mapping: SAMPLE_SHAPE });
        vi.spyOn(apiModule, 'createCsvMapping').mockResolvedValue({
            id: 'map-new',
            name: 'Mine',
            definitionYaml: 'version: 1',
            schemaVersion: 1,
            createdAt: '2026-09-01T00:00:00Z',
            updatedAt: '2026-09-01T00:00:00Z',
        });

        const { user, onChoose } = renderStep();
        await latestYaml(onChoose);
        await nameItAndSave(user, 'Mine');

        await waitFor(() =>
            expect(onChoose).toHaveBeenLastCalledWith({ mappingId: 'map-new' }));
    });
});
