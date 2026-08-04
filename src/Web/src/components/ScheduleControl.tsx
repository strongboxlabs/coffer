import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { errorMessage } from '@/lib/errorMessage';

/** The subset of a schedule this control reads — satisfied by both the
 *  per-ledger Schedule and the global BackupSchedule. */
export interface ScheduleView {
    enabled: boolean;
    hourLocal: number;
    minuteLocal: number;
    timezone: string | null;
    nextRunAt: string | null;
}

interface ScheduleSaveBody {
    enabled: boolean;
    hourLocal: number;
    minuteLocal: number;
    timezone: string;
}

/**
 * Reusable daily-schedule control: an enable toggle + a time-of-day input,
 * backed by a caller-supplied load/save pair. Used by the per-ledger panels
 * (quote-refresh, snapshot — `/api/ledgers/{id}/schedules/{jobType}`) and the
 * global admin backup schedule (`/api/admin/backups/schedule`). Generic over
 * the data source rather than hardcoding ledgerId/jobType so there's one
 * control, not a per-surface copy.
 *
 * `canEnable=false` (with `disabledHint`) blocks turning the schedule on — the
 * backup panel uses it to require a passphrase first.
 */
export function ScheduleControl({
    queryKey,
    load,
    save,
    label,
    note,
    canEnable = true,
    disabledHint,
}: {
    queryKey: readonly unknown[];
    load: () => Promise<ScheduleView>;
    save: (body: ScheduleSaveBody) => Promise<ScheduleView>;
    label: string;
    note: string;
    canEnable?: boolean;
    disabledHint?: string;
}) {
    const queryClient = useQueryClient();
    const query = useQuery({ queryKey: [...queryKey], queryFn: load });
    const mutation = useMutation({
        mutationFn: (body: ScheduleSaveBody) => save(body),
        onSuccess: (saved) => queryClient.setQueryData([...queryKey], saved),
    });

    const schedule = query.data;
    // The user's browser timezone — captured on every save so the schedule runs
    // at the user's local time, not the server's.
    const browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone;

    function setEnabled(on: boolean) {
        mutation.mutate({
            enabled: on,
            hourLocal: schedule?.hourLocal ?? 19,
            minuteLocal: schedule?.minuteLocal ?? 0,
            timezone: browserTz,
        });
    }

    function setTime(value: string) {
        const [h, m] = value.split(':').map(Number);
        if (Number.isNaN(h) || Number.isNaN(m)) return;
        mutation.mutate({
            enabled: schedule?.enabled ?? true,
            hourLocal: h,
            minuteLocal: m,
            timezone: browserTz,
        });
    }

    // Allow turning OFF even when canEnable is false (you can always disable);
    // only block turning ON.
    const toggleDisabled = query.isPending || mutation.isPending
        || (!canEnable && !(schedule?.enabled ?? false));

    return (
        <div className="space-y-2">
            <label className="flex items-center gap-2 text-sm font-medium">
                <input
                    type="checkbox"
                    checked={schedule?.enabled ?? false}
                    disabled={toggleDisabled}
                    onChange={(e) => setEnabled(e.target.checked)}
                    className="h-4 w-4 rounded border-border text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                />
                <span>{label}</span>
            </label>
            {!canEnable && disabledHint ? (
                <p className="pl-6 text-[0.6875rem] text-text-subtle">{disabledHint}</p>
            ) : null}
            <div className="flex flex-wrap items-center gap-2 pl-6 text-sm text-text-muted">
                <span>at</span>
                <input
                    type="time"
                    value={timeValue(schedule?.hourLocal ?? 19, schedule?.minuteLocal ?? 0)}
                    disabled={!schedule?.enabled || mutation.isPending}
                    onChange={(e) => setTime(e.target.value)}
                    className="rounded border border-border bg-surface px-2 py-1 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent disabled:opacity-50"
                    aria-label="Daily run time"
                />
                <span className="text-[0.6875rem]">
                    {schedule?.enabled && !schedule.timezone
                        ? 'server time zone — re-save to use yours'
                        : schedule?.timezone ?? browserTz}
                    {' · '}{note}
                </span>
            </div>
            {schedule?.enabled && schedule.nextRunAt ? (
                <p className="pl-6 text-[0.6875rem] text-text-subtle">
                    Next run: {new Date(schedule.nextRunAt).toLocaleString()}
                </p>
            ) : null}
            {mutation.isError ? (
                <p role="alert" className="pl-6 text-sm text-state-danger">
                    {errorMessage(mutation.error, 'Could not update the schedule.')}
                </p>
            ) : null}
        </div>
    );
}

function timeValue(hour: number, minute: number): string {
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${pad(hour)}:${pad(minute)}`;
}
