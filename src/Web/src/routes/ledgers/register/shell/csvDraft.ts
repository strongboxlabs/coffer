import { toYaml, type WizardChoices } from './csvSniff';

/**
 * Everything the mapping step remembers, and the two functions that read it.
 *
 * <b>Owned by the import dialog, not by the step.</b> It used to be seven `useState`
 * calls inside `CsvMappingStep`, and the dialog renders its steps conditionally — so
 * moving to the preview UNMOUNTED the wizard and destroyed all of it. Back from a bad
 * preview landed on the file picker with the file still listed, looking untouched, and
 * re-entering gave a freshly sniffed document: the hand-edited YAML gone, the name box
 * empty. There was no route from a bad preview back to the mapping that caused it.
 *
 * In its own module because a file that exports both a component and the values around
 * it cannot be hot-reloaded, which the linter says and the gate enforces.
 */
export interface CsvDraft {
    /**
     * Where the document came from. A saved layout keeps its identity so it can be
     * UPDATED rather than only copied — without this, every correction to a layout
     * makes "Chase card 2".
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
}

export const EMPTY_CSV_DRAFT: CsvDraft = {
    source: { kind: 'new' },
    choices: null,
    override: null,
    tab: 'guided',
    name: '',
    parsedFrom: null,
};

/** The document this draft describes. */
export function csvDraftYaml(draft: CsvDraft): string {
    return draft.override ?? (draft.choices !== null ? toYaml(draft.choices) : '');
}
