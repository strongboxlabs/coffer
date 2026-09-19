/**
 * Extracted from TxnRowEdit.tsx during the Slice 3 decomposition. Presentational
 * only: it holds no draft state and fetches nothing, per the boundary rule in
 * ../README.md.
 */

import { cn } from '@/lib/cn';

// --------------------------------------------------------------------
// TagsPlaceholder
// --------------------------------------------------------------------

/** Reserved layout slot for tag editing — visual placeholder, not
 *  a real input. Keeps the form layout stable so a future PR
 *  adding tag editing doesn't reshuffle the editor (user: "I DO
 *  NOT WANT TO REDESIGN FORMS IN EVERY PR").
 *
 *  `compact` is the splits-grid form: a chip beside the category picker
 *  rather than a full-width box under it. Same slot, same promise about not
 *  reshuffling — it just costs no row height, which is what lets a leg row be
 *  30px tall and the list scroll instead of the editor growing. */
export function TagsPlaceholder({
    label,
    hint,
    compact,
    'aria-label': ariaLabel,
}: {
    label?: string;
    hint: string;
    compact?: boolean;
    'aria-label'?: string;
}) {
    return (
        <label
            className={cn(
                'flex min-w-0 text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted',
                compact ? 'shrink-0 items-center' : 'flex-col gap-1',
            )}
            aria-label={ariaLabel}
        >
            {label !== undefined ? <span>{label}</span> : null}
            <div
                role="presentation"
                title={hint}
                className={cn(
                    'flex min-w-0 items-center rounded border border-dashed border-border bg-transparent italic text-text-subtle opacity-50',
                    compact
                        ? 'h-control-20px px-1.5 text-[0.625rem] normal-case tracking-normal'
                        : 'h-control-28px px-2 text-xs',
                )}
            >
                {compact ? 'tags' : hint}
            </div>
        </label>
    );
}
