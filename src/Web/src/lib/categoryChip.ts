import type { ChipProps } from '@/components/ui/Chip';

// Resolve a Chip variant from a counterparty account. The "category"
// surface on a register row IS the symmetric posting's counterparty
// account (ADR-0019); we pick a chip color per ADR-0021 Rule 5.
//
// Resolution order:
//   1. Counterparty is a real account (bank / credit_card / investment /
//      holding / asset / liability / loan) → it's a transfer, render as
//      the slate `xfer` variant. No category guessing.
//   2. Counterparty is a category account → look up a known-name slug
//      (groceries → groc, dining → din, …). Case-insensitive substring
//      match so "Bills:Electricity" maps to `util`, "Groceries:Bulk Mart"
//      maps to `groc`, etc.
//   3. Fallback → hash the counterparty account id to one of the 10
//      category variants. Deterministic, so a given account always
//      gets the same chip across sessions (per ADR-0021's auto-assign
//      fallback). When the user-editable category-color feature lands
//      (Phase 6+), the override replaces this hash.
//   4. Counterparty is null / missing → `default` slate variant.

type CategoryVariant = NonNullable<ChipProps['variant']>;

/** Known category-name slugs in resolution order. First-match wins. */
const NAME_PATTERNS: ReadonlyArray<readonly [RegExp, CategoryVariant]> = [
    [/grocer/i, 'groc'],
    [/(dining|restaurant|food\s*&\s*drink|coffee|cafe)/i, 'din'],
    [/(housing|rent|mortgage|home\b)/i, 'house'],
    [/(utilit|electric|gas|water|sewer|trash)/i, 'util'],
    [/(subscription|streaming|saas)/i, 'sub'],
    [/(transport|transit|uber|lyft|fuel|fare|parking)/i, 'tran'],
    [/(salary|paycheck|wages|payroll|income)/i, 'sal'],
    [/(phone|telecom|wireless|mobile)/i, 'phone'],
    [/(recreation|entertainment|leisure|hobby|gym)/i, 'rec'],
];

/** Account types that count as "real" (not categories). */
const TRANSFER_TYPES = new Set([
    'bank',
    'credit_card',
    'investment',
    'holding',
    'asset',
    'liability',
    'loan',
]);

/** Auto-assign pool — the 10 category variants in a stable order. */
const AUTO_VARIANTS: readonly CategoryVariant[] = [
    'groc',
    'din',
    'house',
    'util',
    'sub',
    'tran',
    'sal',
    'xfer',
    'phone',
    'rec',
];

/**
 * Pick a Chip variant for a register row's "category" cell. Returns
 * `default` when no counterparty is available, `xfer` for real-account
 * transfers, a name-matched variant for known category names, or a
 * hash-assigned variant otherwise.
 */
export function categoryChipVariant(
    counterpartyAccountName: string | null,
    counterpartyAccountType: string | null,
    counterpartyAccountId: string | null,
): CategoryVariant {
    if (!counterpartyAccountName) return 'default';
    if (counterpartyAccountType && TRANSFER_TYPES.has(counterpartyAccountType)) {
        return 'xfer';
    }

    for (const [pattern, variant] of NAME_PATTERNS) {
        if (pattern.test(counterpartyAccountName)) return variant;
    }

    // Stable hash → variant. Using the account id (when present) means
    // every transaction in the same category gets the same color.
    const seed = counterpartyAccountId ?? counterpartyAccountName;
    return AUTO_VARIANTS[hashStringToBucket(seed, AUTO_VARIANTS.length)]!;
}

/**
 * Simple deterministic string → bucket index. djb2 hash mod buckets.
 * Not cryptographic; just needs to be stable across sessions and
 * spread inputs reasonably.
 */
function hashStringToBucket(input: string, bucketCount: number): number {
    let hash = 5381;
    for (let i = 0; i < input.length; i++) {
        hash = ((hash << 5) + hash + input.charCodeAt(i)) | 0; // |0 keeps int32
    }
    return Math.abs(hash) % bucketCount;
}
