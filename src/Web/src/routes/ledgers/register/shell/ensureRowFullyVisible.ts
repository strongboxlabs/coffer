/**
 * After a focus arrival, make sure the row is actually all there.
 *
 * Virtuoso's `scrollIntoView` with no `align` is "smart": it does nothing when
 * the item is already in the viewport. A row clipped by a pixel at the top
 * edge IS in the viewport by that test, so arriving on one — via Show other
 * side, or a post-save refresh — left it half under the header band and smart
 * mode considered the job done. Intermittent, because it depends entirely on
 * where the row happened to sit before the scroll.
 *
 * Correcting afterwards rather than choosing a different `align` is deliberate.
 * `align: 'start'` fixes the clipping and yanks the row to the top of the list
 * after EVERY save, which is disruptive mid-list and was removed for that
 * reason. This leaves a row that is already comfortably visible exactly where
 * it is, and only moves one that is cut off.
 *
 * Measured rather than computed: whatever virtuoso did, and whatever the
 * estimate error was on a list of variable-height rows, the question "is this
 * element fully inside that scroller" has one answer and the DOM has it.
 */

/** Breathing room so a corrected row is not flush against the header band. */
const GAP_PX = 6;

export function ensureRowFullyVisible(
    scroller: HTMLElement | null,
    headerId: string | null,
): void {
    if (scroller === null || headerId === null) return;

    // `data-headerid` is on the txn and split-parent rows, and deliberately
    // absent on legs — a leg is not the header's row.
    const row = scroller.querySelector<HTMLElement>(
        `[data-headerid="${CSS.escape(headerId)}"]`,
    );
    if (row === null) return;

    const view = scroller.getBoundingClientRect();
    const rect = row.getBoundingClientRect();

    // A row taller than the viewport cannot be "fully visible". Align its top
    // and let the rest run off the bottom, which is the only readable choice.
    if (rect.height >= view.height) {
        scroller.scrollTop += rect.top - view.top - GAP_PX;
        return;
    }

    if (rect.top < view.top + GAP_PX) {
        scroller.scrollTop -= view.top + GAP_PX - rect.top;
    } else if (rect.bottom > view.bottom - GAP_PX) {
        scroller.scrollTop += rect.bottom - view.bottom + GAP_PX;
    }
}
