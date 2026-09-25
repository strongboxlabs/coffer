/**
 * Non-component module, per the boundary note on `RegisterRowStrategy`: a
 * function exported beside a component trips
 * `react-refresh/only-export-components`, so shared helpers live in their own
 * file rather than adding to the handful that already do.
 */
/**
 * Virtuoso's emitted index → the row's position in the LOADED WINDOW.
 *
 * `firstItemIndex` shifts the index space by `offset` so rows can be prepended
 * without going negative, so what `itemContent` receives is ~1,000,000 for the
 * first row. That number reaches `aria-rowindex`, where a screen reader reads
 * it out: the register announced its first row as row 1,000,001.
 *
 * Exported and pure because it cannot be tested through the component. Under
 * jsdom virtuoso emits LOCAL indices — `firstItemIndex` has no effect without
 * layout — so a rendered-DOM assertion reads 1 and 2 whether or not this
 * subtraction happens, and passes either way. The rule is the thing worth
 * pinning, so it is pinned directly.
 *
 * The out-of-bounds fallback covers the re-mount race the sibling
 * `rangeChanged` handler documents: the offset has not resettled, the
 * subtraction lands outside the array, and virtuoso is already emitting a
 * local index.
 */
export function toWindowIndex(
    emitted: number,
    offset: number,
    rowCount: number,
): number {
    const local = emitted - offset;
    return local < 0 || local >= rowCount ? emitted : local;
}
