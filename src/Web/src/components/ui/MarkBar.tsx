// MarkBar — spend against a mark: an accent run up to the mark, a danger
// overflow past it, and a tick at the mark (ADR-0099 D5: colour means state).
// Extracted from the budget table's bar; the home screen's widgets draw the
// same one. Decorative: the figures beside it carry the meaning, so it is
// aria-hidden and the caller writes the words.
//
// Over is over: any amount past the mark draws the overflow, with no floor, and
// the overflow is never narrower than what shows past the tick, so a category a
// little over reads as over rather than as a bar that stops at its mark.

export interface MarkBarProps {
    /** Spend, at the precision its figure prints, so the bar is over exactly when
     *  the figures beside it say so. */
    actual: number;
    /** The mark, likewise; null draws the run alone, with no tick and no overflow. */
    mark: number | null;
    /** The value at the bar's full width, shared across rows so bars compare. */
    scaleMax: number;
    /** The spend is no figure (it needs a missing rate, ADR-0114 D9): the tick alone,
     *  with no run and no overflow. */
    noRun?: boolean;
}

export function MarkBar({ actual, mark, scaleMax, noRun = false }: MarkBarProps) {
    // MARK, not typical — all three of the tick, the run and the overflow.
    // Leaving the split on history while the tick moved to the target would
    // paint a red bar with its own tick sitting to the RIGHT of the red: the
    // most visible pixels on the screen contradicting the number beside them.
    const pct = (v: number) => `${Math.max(0, Math.min(100, (v / scaleMax) * 100))}%`;
    return (
        <span aria-hidden className="relative block h-control-20px">
            {noRun ? null : (
                <span
                    className="absolute top-1.5 h-fixed-8px rounded-l-full bg-accent"
                    style={{ left: 0, width: pct(Math.min(actual, mark ?? actual)) }}
                />
            )}
            {!noRun && mark !== null && actual > mark ? (
                // At least 6px: the 2px tick sits over the overflow's start.
                <span
                    className="absolute top-1.5 h-fixed-8px min-w-fixed-6px rounded-r-full bg-state-danger"
                    style={{ left: pct(mark), width: pct(actual - mark) }}
                />
            ) : null}
            {mark !== null ? (
                <span
                    className="absolute top-0 h-full w-fixed-2px bg-border-strong"
                    style={{ left: pct(mark) }}
                />
            ) : null}
        </span>
    );
}
