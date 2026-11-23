import { useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import { createSavedReport, patchSavedReport } from '@/lib/api';
import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { FieldLabel } from '@/components/ui/FieldLabel';
import { PendingScope, pendingTextProps } from '@/components/ui/pending';
import { PendingLabel } from '@/components/ui/PendingLabel';
import { useFocusHandOff } from '@/components/ui/rowRemovalFocus';
import type { ReportSpec, SavedReport } from '@/types/report';

/**
 * Save / Save as — the thing that makes a memorized report (ADR-0104 D3).
 *
 * This is the UI's only way to create a saved-report row; without it the
 * Saved section of the landing cannot fill. A feature reachable only by curl
 * is not shipped.
 *
 * SAVE vs SAVE AS. "Save" appears only when a saved report is already loaded,
 * and overwrites that row. "Save as" always appears and always creates. Both
 * are explicit: nothing is written because a control moved, because adjusting
 * a shared report you did not write is a normal thing to do on the way to
 * saving your own copy.
 *
 * FOCUS. The Save as… form goes when its save lands or it is cancelled, taking
 * the control that held focus with it: the name field (Enter submits), the
 * Share checkbox, its Save or Cancel. Each hands focus to the Save as… toggle,
 * which stays on the page (ADR-0023 §L). One hand-off per control, since each
 * tracks one element. While a save is in flight the name field and the Share
 * checkbox are pending with Save and Cancel: the save sends what they held at the
 * press, and its success resets them, so a change made meanwhile would be lost.
 */
export function SaveReportControl({
    ledgerId,
    spec,
    savedId,
    savedName,
    onSaved,
}: {
    ledgerId: string;
    spec: ReportSpec;
    /** Set when the view was opened from a memorized report. */
    savedId?: string;
    savedName?: string;
    onSaved: (report: SavedReport) => void;
}) {
    const [naming, setNaming] = useState(false);
    const [name, setName] = useState('');
    const [shared, setShared] = useState(false);
    const queryClient = useQueryClient();
    const saveAsRef = useRef<HTMLButtonElement | null>(null);
    const toSaveAs = () => saveAsRef.current;
    const nameHandOff = useFocusHandOff(toSaveAs);
    const shareHandOff = useFocusHandOff(toSaveAs);
    const saveHandOff = useFocusHandOff(toSaveAs);
    const cancelHandOff = useFocusHandOff(toSaveAs);

    const invalidate = () =>
        queryClient.invalidateQueries({ queryKey: ['saved-reports', ledgerId] });

    const create = useMutation({
        mutationFn: () =>
            createSavedReport(ledgerId, name.trim(), spec, shared ? 'ledger' : 'private'),
        onSuccess: (report) => {
            void invalidate();
            setNaming(false);
            setName('');
            setShared(false);
            onSaved(report);
        },
    });

    const update = useMutation({
        mutationFn: () => patchSavedReport(ledgerId, savedId!, { spec }),
        onSuccess: (report) => {
            void invalidate();
            onSaved(report);
        },
    });

    const busy = create.isPending || update.isPending;
    const error = (create.error ?? update.error) as Error | undefined;

    // Save and Save as's own Save share `busy`: one group (pending.ts), so a
    // press of either holds both from the press.
    return (
        <PendingScope>
        <div className="flex flex-col items-end gap-1">
            <div className="flex items-center gap-1.5">
                {savedId ? (
                    <Button
                        size="sm"
                        variant="primary"
                        pending={busy}
                        onClick={() => update.mutate()}
                    >
                        <PendingLabel pending={update.isPending} label="Save" pendingLabel="Saving…" />
                    </Button>
                ) : null}
                <Button
                    ref={saveAsRef}
                    size="sm"
                    variant={savedId ? 'secondary' : 'primary'}
                    pending={busy}
                    onClick={() => {
                        // Seed from the loaded report's name so "save a copy"
                        // starts from something, rather than an empty field the
                        // user has to re-type to differ by one word.
                        setName(savedName ? `${savedName} (copy)` : '');
                        setNaming((v) => !v);
                    }}
                >
                    Save as…
                </Button>
            </div>

            {naming ? (
                <form
                    className="flex items-end gap-1.5 rounded border border-border bg-surface p-2"
                    onSubmit={(e) => {
                        e.preventDefault();
                        if (name.trim().length > 0) create.mutate();
                    }}
                >
                    <label className="flex min-w-0 flex-col gap-1">
                        <FieldLabel htmlFor="saved-report-name">Name</FieldLabel>
                        <input
                            ref={nameHandOff}
                            id="saved-report-name"
                            value={name}
                            autoFocus
                            onChange={(e) => setName(e.target.value)}
                            placeholder="Groceries, 12 months"
                            className="h-control-28px w-fixed-224px rounded border border-border bg-surface px-2 text-xs text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50 aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                            {...pendingTextProps<HTMLInputElement>(busy)}
                        />
                    </label>
                    {/* Private by default. Sharing is a decision someone makes,
                        not one they discover they made (migration 244). */}
                    <Checkbox
                        ref={shareHandOff}
                        label="Share with this ledger"
                        checked={shared}
                        pending={busy}
                        onChange={(e) => setShared(e.target.checked)}
                        wrapperClassName="pb-1.5 text-xs text-text"
                    />
                    <Button ref={saveHandOff} size="sm" type="submit" disabled={name.trim().length === 0} pending={busy}>
                        <PendingLabel pending={create.isPending} label="Save" pendingLabel="Saving…" />
                    </Button>
                    <Button
                        ref={cancelHandOff}
                        size="sm"
                        variant="ghost"
                        type="button"
                        pending={busy}
                        onClick={() => setNaming(false)}
                    >
                        Cancel
                    </Button>
                </form>
            ) : null}

            {error ? (
                <p role="alert" className="text-[0.6875rem] text-state-danger">
                    {error.message}
                </p>
            ) : null}
        </div>
        </PendingScope>
    );
}
