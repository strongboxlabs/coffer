// Mirror of API `Coffer.Api.Contracts.UndoImportResult` (mig 221).

export interface UndoImportResult {
    /** Transactions still carrying this import's stamp.
     *
     *  There is deliberately no `edited` count beside it any more. Undo is
     *  offered in a modal right after the import that created these rows, so
     *  nobody has edited them yet — see `UndoImportResult` on the API side. */
    found: number;
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
