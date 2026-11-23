import type { CsvDecimalSeparatorName } from '@/lib/types';

import { toYaml, type WizardChoices } from './csvSniff';

/**
 * Everything the mapping step remembers, and the two functions that read it.
 *
 * <b>Owned by the import dialog, not by the step.</b> The dialog renders its steps
 * conditionally, so state held in `useState` calls inside `CsvMappingStep` would be
 * destroyed when moving to the preview UNMOUNTS the wizard. Back from a bad preview
 * would land on the file picker with the file still listed, looking untouched, and
 * re-entering would give a freshly sniffed document: the hand-edited YAML gone, the name
 * box empty — no route from a bad preview back to the mapping that caused it.
 *
 * In its own module because a file that exports both a component and the values around
 * it cannot be hot-reloaded, which the linter says and the gate enforces.
 */
export interface CsvDraft {
    /**
     * Where the document came from. A saved layout keeps its identity so it can be
     * UPDATED rather than only copied — without this, every correction to a layout
     * makes "Card layout 2".
     */
    source:
        | { kind: 'new' }
        | { kind: 'saved'; id: string; name: string; savedYaml: string };
    /** The form's view of the document. Null until the file has been read. */
    choices: WizardChoices | null;
    /**
     * The document, when it is not simply what the form would write — a hand edit, or a
     * saved document kept verbatim so its comments and layout survive being reopened.
     */
    override: string | null;
    tab: 'guided' | 'yaml';
    /** What Save-as would call it. */
    name: string;
    /**
     * The document text `choices` was last read from.
     *
     * Without it there is no way to know whether the form still describes the
     * document: a hand edit in the YAML tab changes one and not the other, and the
     * controls then quietly show something the importer will not do. Comparing this
     * with the document is what says "re-read before showing the form".
     */
    parsedFrom: string | null;
    /**
     * The decimal separator guessed from the sample when this layout was first made, or
     * null when nothing was guessed: a saved layout keeps the separator it was saved
     * with and is never guessed again (ADR-0114 D15).
     *
     * Kept apart from `choices` so the step can say "Guessed from the sample." only
     * while the select still shows the guess, on a layout that is not saved.
     */
    guessedSeparator: CsvDecimalSeparatorName | null;
}

export const EMPTY_CSV_DRAFT: CsvDraft = {
    source: { kind: 'new' },
    choices: null,
    override: null,
    tab: 'guided',
    name: '',
    parsedFrom: null,
    guessedSeparator: null,
};

/** Whether the step says "Guessed from the sample." under the decimal separator: on a
 *  layout not yet saved, while the select still holds the guess (ADR-0114 D15). */
export function separatorIsGuess(draft: CsvDraft): boolean {
    return draft.source.kind === 'new'
        && draft.guessedSeparator !== null
        && draft.choices?.decimalSeparator === draft.guessedSeparator;
}

/** The document this draft describes. */
export function csvDraftYaml(draft: CsvDraft): string {
    return draft.override ?? (draft.choices !== null ? toYaml(draft.choices) : '');
}
