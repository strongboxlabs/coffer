/**
 * The merge candidate chip's tooltip, in both editors.
 *
 * Says which row survives: the candidate, kept as it is. The row being edited is
 * the one folded away (the merge's direction is inverted, see bank-edit/save.ts),
 * so a tooltip that named the edited row as the one kept promised the opposite of
 * what Fold does. A manual candidate is the person's own entry, kept as they
 * entered it; any other was imported earlier, and is kept as it is.
 *
 * @param dateLabel the candidate's posted date, `yyyy-MM-dd`.
 * @param origin the candidate's `origin` (`manual`, `online_import`, `file_import`).
 */
export function mergeChipTitle(dateLabel: string, origin: string): string {
    return origin === 'manual'
        ? `Folds this feed row into your manual row of ${dateLabel}, which is kept as you entered it. Click again to cancel.`
        : `Folds this feed row into the imported row of ${dateLabel}, which is kept as it is. Click again to cancel.`;
}
