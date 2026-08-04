import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { fetchReminders, setReminderActive, skipReminder } from '@/lib/api';
import type { ReminderSummary } from '@/lib/types';
import { formatLedgerDate } from '@/lib/dates';
import { formatSignedAmount } from '@/lib/money';
import { humanizeRrule } from '@/lib/recurrence';
import { errorMessage } from '@/lib/errorMessage';
import { Button } from '@/components/ui/Button';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { EmptyState } from '@/components/ui/EmptyState';
import { Panel, PanelBody } from '@/components/ui/Panel';

import { ReminderEditorDialog } from './ReminderEditorDialog';

/**
 * Reminders management list (ADR-0049 R1 + ADR-0051 R2 authoring) - one row per
 * series with the humanized recurrence, next-due, and signed amount (Moneydance
 * agenda parity), plus New / Edit / Skip-next / Disable-Enable.
 */
export function RemindersListPanel({ ledgerId }: { ledgerId: string }) {
    const queryClient = useQueryClient();
    const query = useQuery({
        queryKey: ['reminders', ledgerId],
        queryFn: () => fetchReminders(ledgerId),
    });

    // null = closed; { id: null } = create; { id } = edit that series.
    const [editor, setEditor] = useState<{ id: string | null } | null>(null);
    // Series whose next occurrence is pending a skip confirmation.
    const [skipTarget, setSkipTarget] = useState<ReminderSummary | null>(null);

    const invalidate = () => {
        queryClient.invalidateQueries({ queryKey: ['reminders', ledgerId] });
        queryClient.invalidateQueries({ queryKey: ['reminders', 'upcoming', ledgerId] });
    };

    const activeMutation = useMutation({
        mutationFn: (v: { id: string; active: boolean }) =>
            setReminderActive(ledgerId, v.id, { active: v.active }),
        onSuccess: invalidate,
    });
    const skipMutation = useMutation({
        mutationFn: (v: { id: string; occurrenceDate: string }) =>
            skipReminder(ledgerId, v.id, { occurrenceDate: v.occurrenceDate }),
        onSuccess: invalidate,
    });

    const reminders = query.data ?? [];
    const busy = activeMutation.isPending || skipMutation.isPending;
    const actionError = activeMutation.error ?? skipMutation.error;

    function renderBody() {
        if (query.isError) {
            return (
                <Panel className="border-state-danger/40 bg-state-danger-soft">
                    <PanelBody>
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(query.error, 'Could not load reminders.')}
                        </p>
                    </PanelBody>
                </Panel>
            );
        }
        if (query.isPending) {
            return <Panel><PanelBody><p className="text-sm text-text-subtle">Loading…</p></PanelBody></Panel>;
        }
        if (reminders.length === 0) {
            return (
                <EmptyState
                    message="No reminders yet."
                    hint="Reminders post recurring transactions on a schedule. Import from Moneydance, or create one."
                    action={
                        <Button type="button" variant="primary" size="sm" onClick={() => setEditor({ id: null })}>
                            New reminder
                        </Button>
                    }
                />
            );
        }
        return (
            <ul className="space-y-2">
                {reminders.map((r) => (
                    <ReminderRow
                        key={r.id}
                        reminder={r}
                        disabled={busy}
                        onEdit={() => setEditor({ id: r.id })}
                        onToggleActive={() => activeMutation.mutate({ id: r.id, active: !r.isActive })}
                        onSkipNext={() => {
                            if (r.nextDueDate === null) return;
                            setSkipTarget(r);
                        }}
                    />
                ))}
            </ul>
        );
    }

    return (
        <div className="space-y-2">
            <div className="flex items-center justify-between">
                <h2 className="text-sm font-semibold text-text">Reminders</h2>
                <Button type="button" variant="secondary" size="sm" onClick={() => setEditor({ id: null })}>
                    New reminder
                </Button>
            </div>

            {actionError !== null ? (
                <p role="alert" className="text-xs text-state-danger">
                    {errorMessage(actionError, 'Action failed.')}
                </p>
            ) : null}

            {renderBody()}

            {editor !== null ? (
                <ReminderEditorDialog
                    ledgerId={ledgerId}
                    reminderId={editor.id}
                    onClose={() => setEditor(null)}
                    onSaved={invalidate}
                />
            ) : null}

            <ConfirmDialog
                open={skipTarget !== null && skipTarget.nextDueDate !== null}
                title="Skip the next occurrence?"
                body={
                    skipTarget !== null && skipTarget.nextDueDate !== null
                        ? `Skip the ${formatLedgerDate(skipTarget.nextDueDate)} occurrence of `
                          + `"${skipTarget.payee ?? 'this reminder'}"? The cursor advances to the next due date.`
                        : ''
                }
                confirmLabel="Skip occurrence"
                isConfirming={skipMutation.isPending}
                onConfirm={() => {
                    if (skipTarget === null || skipTarget.nextDueDate === null) return;
                    skipMutation.mutate(
                        { id: skipTarget.id, occurrenceDate: skipTarget.nextDueDate },
                        { onSettled: () => setSkipTarget(null) },
                    );
                }}
                onCancel={() => setSkipTarget(null)}
            />
        </div>
    );
}

function ReminderRow({
    reminder, disabled, onEdit, onToggleActive, onSkipNext,
}: {
    reminder: ReminderSummary;
    disabled: boolean;
    onEdit: () => void;
    onToggleActive: () => void;
    onSkipNext: () => void;
}) {
    const r = reminder;
    return (
        <li className={`rounded border border-border bg-surface px-3 py-2.5 ${r.isActive ? '' : 'opacity-70'}`}>
            <div className="flex items-start justify-between gap-3">
                <div className="min-w-0 flex-1 space-y-0.5">
                    <p className="flex items-center gap-2 text-sm font-medium">
                        <span className="truncate">{r.payee ?? 'Untitled reminder'}</span>
                        {!r.isActive ? (
                            <span className="shrink-0 rounded bg-surface-muted px-1.5 py-0.5 text-[0.625rem] uppercase tracking-wider text-text-muted">
                                Paused
                            </span>
                        ) : null}
                    </p>
                    <p className="text-xs text-text-muted">
                        {humanizeRrule(r.rrule)}
                        {r.nextDueDate !== null
                            ? <> · next {formatLedgerDate(r.nextDueDate)}</>
                            : null}
                        {r.origin === 'manual'
                            ? <> · Manual</>
                            : <> · Imported</>}
                    </p>
                </div>
                <div className="flex shrink-0 items-center gap-3">
                    <span
                        className={`font-mono text-sm tabular-nums ${
                            r.amount < 0 ? 'text-state-danger' : r.amount > 0 ? 'text-state-success' : 'text-text-muted'}`}
                    >
                        {formatSignedAmount(r.amount)}
                    </span>
                    <div className="flex items-center gap-1.5">
                        <Button type="button" variant="ghost" size="sm" disabled={disabled} onClick={onEdit}>
                            Edit
                        </Button>
                        {r.isActive && r.nextDueDate !== null ? (
                            <Button type="button" variant="ghost" size="sm" disabled={disabled} onClick={onSkipNext}>
                                Skip next
                            </Button>
                        ) : null}
                        <Button type="button" variant="ghost" size="sm" disabled={disabled} onClick={onToggleActive}>
                            {r.isActive ? 'Disable' : 'Enable'}
                        </Button>
                    </div>
                </div>
            </div>
        </li>
    );
}
