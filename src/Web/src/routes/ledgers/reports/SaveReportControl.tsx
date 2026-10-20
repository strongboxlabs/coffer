import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';

import { createSavedReport, patchSavedReport } from '@/lib/api';
import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { FieldLabel } from '@/components/ui/FieldLabel';
import type { ReportSpec, SavedReport } from '@/types/report';

/**
 * Save / Save as — the thing that makes a memorized report (ADR-0104 D3).
 *
 * Until this existed the Saved section of the landing could never fill: the
 * table, the endpoints and the list were all built, and nothing in the UI
 * could create a row. A feature reachable only by curl is not shipped.
 *
 * SAVE vs SAVE AS. "Save" appears only when a saved report is already loaded,
 * and overwrites that row. "Save as" always appears and always creates. Both
 * are explicit: nothing is written because a control moved, because adjusting
 * a shared report you did not write is a normal thing to do on the way to
 * saving your own copy.
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

    return (
        <div className="flex flex-col items-end gap-1">
            <div className="flex items-center gap-1.5">
                {savedId ? (
                    <Button
                        size="sm"
                        variant="primary"
                        disabled={busy}
                        onClick={() => update.mutate()}
                    >
                        {update.isPending ? 'Saving…' : 'Save'}
                    </Button>
                ) : null}
                <Button
                    size="sm"
                    variant={savedId ? 'secondary' : 'primary'}
                    disabled={busy}
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
                            id="saved-report-name"
                            value={name}
                            autoFocus
                            onChange={(e) => setName(e.target.value)}
                            placeholder="Groceries, 12 months"
                            className="h-control-28px w-fixed-224px rounded border border-border bg-surface px-2 text-xs text-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                        />
                    </label>
                    {/* Private by default. Sharing is a decision someone makes,
                        not one they discover they made (migration 244). */}
                    <Checkbox
                        label="Share with this ledger"
                        checked={shared}
                        onChange={(e) => setShared(e.target.checked)}
                        wrapperClassName="pb-1.5 text-xs text-text"
                    />
                    <Button size="sm" type="submit" disabled={busy || name.trim().length === 0}>
                        {create.isPending ? 'Saving…' : 'Save'}
                    </Button>
                    <Button
                        size="sm"
                        variant="ghost"
                        type="button"
                        disabled={busy}
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
    );
}
