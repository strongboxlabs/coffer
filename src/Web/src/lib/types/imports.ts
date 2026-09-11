// Mirror of API `Coffer.Api.Contracts.UndoImportResult` (mig 221).

export interface UndoImportResult {
    /** Transactions still carrying this import's stamp. */
    found: number;
    /** How many of them have been edited since. Reported so a confirm can say
     *  so — never a reason to refuse, because whose edits they are is the
     *  user's call. */
    edited: number;
    /** Rows removed outright. An undo does NOT hide: a hidden row keeps its
     *  `external_id`, and the import dedup matches on that and never on
     *  `is_hidden`, so hiding would make the same file un-importable — the
     *  re-import would count every row already-known and insert nothing. For a
     *  file import the file is the backup. */
    deleted: number;
    /** The import is above the bulk path's id cap and was NOT touched — a
     *  partly-undone import is worse than none. */
    tooLarge: boolean;
}
