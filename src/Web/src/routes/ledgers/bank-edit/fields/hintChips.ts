/**
 * The hint strips under Payee (Similar, Merge candidates) in an editor's
 * dialog layout, when the dialog is narrow enough to stack its fields (a
 * phone): 12px text, and chips at least 32px tall with room between them, so
 * a thumb can take one. The register's editor and the wide dialog keep the
 * compact strip. Both editors use these, so the two strips stay alike.
 *
 * Container-query variants on the dialog card (`@container/dialog`, see
 * EditorDialog), the same threshold the editors' stacked layout reads.
 */

/** The strip: the label and every chip at 12px, with touch spacing. */
export const DIALOG_NARROW_HINT_STRIP = '@max-3xl/dialog:items-center @max-3xl/dialog:gap-2 @max-3xl/dialog:text-xs';

/** One chip: at least 32px tall, its content centred, wider padding. */
export const DIALOG_NARROW_HINT_CHIP = '@max-3xl/dialog:min-h-control-32px @max-3xl/dialog:items-center @max-3xl/dialog:px-2';
