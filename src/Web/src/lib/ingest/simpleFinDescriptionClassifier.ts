// SimpleFIN description classifier — TypeScript port of the C#
// SimpleFinDescriptionClassifier (src/Api/Ingest/SimpleFin/
// SimpleFinDescriptionClassifier.cs, ADR-0031 Phase 3b).
//
// The orchestrator's classifier runs at sync time and persists its
// outputs on `txn_headers.ingest_action_hint` + `ingest_security_id`.
// This TS port runs in the investment editor on save: when the user
// resolves a previously-unmapped ticker, we need the ORIGINAL ticker
// STRING from the description to pass back as
// `providerSecurityHint.providerSecurityId` so the server can record
// the mapping for future syncs (Phase 3d.1 endpoint surface).
//
// We deliberately port the regex instead of persisting the ticker on
// the row: keeps Phase 3 to 5 migrations, not 6. Tradeoff is mild
// regex-drift risk between the two implementations; the unit tests in
// `simpleFinDescriptionClassifier.test.ts` pin the same example
// descriptions both implementations are tested against. If a real
// payload surfaces a description neither matches, fix both at once.

/**
 * Result of classifying a SimpleFIN transaction description.
 * Both fields are independently nullable:
 * - `action`: one of ADR-0027's investment actions (`buy` / `sell` /
 *   `dividend_cash` / `dividend_reinvest` / `transfer`) when the
 *   description matches a known prefix pattern; null otherwise.
 * - `tickerHint`: a 1-5 uppercase letter parenthesized group from
 *   the description (e.g. "(ETFA)" → "ETFA"); null when no match.
 */
export interface ClassifiedDescription {
    action: string | null;
    tickerHint: string | null;
}

/**
 * Pure function: take a SimpleFIN transaction description and
 * return the classifier outputs. Mirrors the C# implementation's
 * dispatch order (more-specific patterns first) so the two
 * classifiers can't diverge on overlapping prefixes like
 * "DIVIDEND REINVESTMENT" (which must match reinvest, not the
 * bare dividend pattern).
 */
export function classifySimpleFinDescription(
    description: string | null | undefined,
): ClassifiedDescription {
    if (description === null || description === undefined) {
        return { action: null, tickerHint: null };
    }
    const trimmed = description.trim();
    if (trimmed.length === 0) {
        return { action: null, tickerHint: null };
    }
    return {
        action: matchAction(description),
        tickerHint: matchTicker(description),
    };
}

function matchAction(description: string): string | null {
    // Order matters: reinvest dispatches before the bare-dividend
    // pattern so "DIVIDEND REINVESTMENT" doesn't claim cash-div
    // first.
    if (REINVEST_RX.test(description)) return 'dividend_reinvest';
    if (DIVIDEND_RX.test(description)) return 'dividend_cash';
    if (BUY_RX.test(description)) return 'buy';
    if (SELL_RX.test(description)) return 'sell';
    if (TRANSFER_RX.test(description)) return 'transfer';
    return null;
}

function matchTicker(description: string): string | null {
    const m = TICKER_RX.exec(description);
    return m ? m[1] : null;
}

// Compiled regexes — mirror the C# patterns verbatim, with `i`
// (case-insensitive) on the action patterns and case-sensitive on
// the ticker pattern (uppercase-only by design).
const REINVEST_RX = /^\s*(REINVEST(MENT)?|DIVIDEND\s+REINVEST(MENT)?)\b/i;
const DIVIDEND_RX = /^\s*(DIV(IDEND)?(\s+RECEIVED)?)\b/i;
const BUY_RX = /^\s*(YOU\s+BOUGHT|BOUGHT|BUY)\b/i;
const SELL_RX = /^\s*(YOU\s+SOLD|SOLD|SELL)\b/i;
const TRANSFER_RX = /^\s*TRANSFER\b/i;
const TICKER_RX = /\(([A-Z]{1,5})\)/;
