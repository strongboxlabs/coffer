// Shared register row-state chrome (ADR-0021 revision; ADR-0030 reuse).
//
// One source of truth for how a register row signals its state, so the
// bank and investment registers can't drift apart. Returns the
// state-dependent background classes plus an inline box-shadow that
// stacks a needs-review status bar with the focus ring.
//
// Model (orthogonal — a row can be several at once):
//   • focused   → a full 2px accent ring (border only, no fill — a soft
//                 fill read as noise in a dense grid).
//   • selected  → a faint accent tint (bulk selection; bank + investment).
//   • needsReview → a warning fill + a 3px warning left bar.
//   • nested    → leg/child rows rest on the muted plane so they read as
//                 nested under their split parent.
//
// Inline style (not a Tailwind arbitrary value) because the inset set is
// dynamic and wouldn't be picked up by the JIT class scanner.

export interface RowChromeState {
    /** The current/active row (single click). */
    focused: boolean;
    /** Bulk-selected (checkbox / Ctrl-click). Bank + investment. */
    selected?: boolean;
    /** Bank-feed row awaiting acceptance. */
    needsReview?: boolean;
    /** Split-leg / child row — rests on the muted plane. */
    nested?: boolean;
}

export interface RowChrome {
    /** Tailwind background + hover classes for the row element. */
    bgClass: string;
    /** Inline `boxShadow` value (focus ring + status bar), or undefined. */
    boxShadow: string | undefined;
}

export function registerRowChrome(state: RowChromeState): RowChrome {
    const { focused, selected = false, needsReview = false, nested = false } = state;

    const bgClass = selected
        ? 'bg-accent-soft/40'
        : needsReview
            ? 'bg-state-warning-soft/70 hover:bg-state-warning-soft'
            : nested
                ? 'bg-surface-muted/40 hover:bg-surface-hover'
                : 'hover:bg-surface-hover';

    const insets: string[] = [];
    if (needsReview) insets.push('inset 3px 0 0 0 var(--color-state-warning)');
    if (focused) insets.push('inset 0 0 0 2px var(--color-accent)');

    return {
        bgClass,
        boxShadow: insets.length > 0 ? insets.join(', ') : undefined,
    };
}
