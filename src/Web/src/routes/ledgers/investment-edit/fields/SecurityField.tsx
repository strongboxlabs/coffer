import { useId } from 'react';
import { Typeahead } from '@/components/ui/Typeahead';
import { cn } from '@/lib/cn';
import { FieldLabel } from '@/components/ui/FieldLabel';
import type { SecuritySummary } from '@/lib/types';
import { useIdBackedTypeahead } from './useIdBackedTypeahead';

interface SecurityFieldProps {
    /** All securities in the ledger. Pre-fetched by the
     * orchestrator; the field filters client-side via the
     * Typeahead primitive. */
    securities: readonly SecuritySummary[];
    /** Currently picked security id, or null. */
    valueId: string | null;
    onChangeId: (next: string | null) => void;
    error?: string | null;
    disabled?: boolean;
    /** Security ids currently held in the brokerage account being
     * edited. When provided, those items sort to the top of the
     * dropdown with a divider beneath them — the 95% case is "Buy
     * more of what's already in this account." Pass an empty set
     * when no account context is available (the picker degrades to
     * a plain ledger-wide list). */
    holdingsSecurityIds?: ReadonlySet<string>;
    /** Optional: when set, an inline "+ Create new security…" row
     * appears at the bottom of the dropdown. The callback receives
     * the current query so the parent can pre-fill the create
     * dialog. */
    onCreate?: (query: string) => void;
}

/**
 * Security picker for the investment editor. Required on every
 * action except `transfer`; optional on `misc`. Label format:
 * `TICKER · Name` when ticker is set, just `Name` otherwise — the
 * Typeahead primitive does substring search on the full label.
 *
 * Three-tier surface (when both optional props are wired):
 *   1. Holdings of the current brokerage (the 95% case)
 *   2. Other ledger securities (rare: first position in this account)
 *   3. "+ Create new security…" (brand new; never seen in the ledger)
 */
export function SecurityField({
    securities, valueId, onChangeId, error, disabled,
    holdingsSecurityIds, onCreate,
}: SecurityFieldProps) {
    const inputId = useId();
    const getLabel = (s: SecuritySummary) =>
        s.ticker ? `${s.ticker} · ${s.name}` : s.name;
    const { text, onTextChange } = useIdBackedTypeahead({
        items: securities,
        getLabel,
        valueId,
        onChangeId,
    });
    return (
        <div className="flex min-w-0 flex-col gap-1 text-xs">
            <FieldLabel htmlFor={inputId}>Security</FieldLabel>
            <Typeahead
                items={securities}
                value={text}
                onChange={onTextChange}
                getKey={(s) => s.id}
                getLabel={getLabel}
                getSearchableText={getLabel}
                placeholder="Ticker or name…"
                disabled={disabled}
                aria-label="Security"
                className={cn(error ? 'border-state-danger' : undefined)}
                // Render the whole catalog; the dropdown's
                // max-h-64 + overflow-auto provides the visual cap.
                // The generic 8-row default would chop off holdings
                // beyond row 8 — unacceptable for a picker whose
                // first tier IS the user's positions.
                maxRows={securities.length}
                prioritize={
                    holdingsSecurityIds && holdingsSecurityIds.size > 0
                        ? (s) => holdingsSecurityIds.has(s.id)
                        : undefined
                }
                creationOption={
                    onCreate
                        ? {
                            label: (q) => `+ Create new security "${q}"`,
                            onSelect: (q) => onCreate(q),
                        }
                        : undefined
                }
            />
            {error ? (
                <span className="text-[0.6875rem] leading-tight text-state-danger">
                    {error}
                </span>
            ) : null}
        </div>
    );
}
