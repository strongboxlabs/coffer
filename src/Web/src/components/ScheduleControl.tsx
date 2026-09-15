import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { errorMessage } from '@/lib/errorMessage';
import { formatRelative } from '@/lib/ledgerOperationDisplay';

/** The subset of a schedule this control reads — satisfied by both the
 *  per-ledger Schedule and the global BackupSchedule. */
export interface ScheduleView {
    enabled: boolean;
    hourLocal: number;
    minuteLocal: number;
    timezone: string | null;
    nextRunAt: string | null;
    /** Health, all optional so a caller whose endpoint does not carry it compiles
     *  unchanged. Every surface that HAS the data should pass it — a control that
     *  silently renders healthy is worse than one that renders nothing. */
    lastRunAt?: string | null;
    consecutiveFailures?: number;
    lastError?: string | null;
    disabledReason?: string | null;
}

/** Values of scheduled_jobs.disabled_reason (mig 216). */
export const DISABLED_BY_FAILURES = 'consecutive-failures';
export const DISABLED_BY_KEY_MATERIAL = 'key-material-missing';

export interface ScheduleSaveBody {
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
    enabledElsewhere = false,
}: {
    queryKey: readonly unknown[];
    load: () => Promise<ScheduleView>;
    save: (body: ScheduleSaveBody) => Promise<ScheduleView>;
    label: string;
    note: string;
    canEnable?: boolean;
    disabledHint?: string;
    /**
     * This job is switched on by something the user does elsewhere, so it offers no
     * on/off checkbox — only the time.
     *
     * Reminder auto-post: ticking "Auto-post / N days before" on a reminder IS the
     * opt-in, and the API turns the job on when a reminder first asks. A second
     * checkbox here would be a switch the user has to go and find before their first
     * tick did anything — which was the original defect, one level up.
     *
     * A RE-ENABLE control still appears when the job has been switched OFF, because it
     * auto-disables after five consecutive failures and a time-only panel would
     * otherwise be a state nobody can escape from the UI.
     */
    enabledElsewhere?: boolean;
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
            // `schedule?.enabled ?? true` was wrong and shipped. GET synthesizes
            // `enabled: false` when no row exists, so the ?? never fired and setting a
            // time CREATED A DISABLED ROW — after which auto-post could never turn
            // itself on, because a reminder only creates the schedule when absent.
            // A job that has no disabled state is always saved enabled.
            enabled: enabledElsewhere ? true : (schedule?.enabled ?? true),
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
            {enabledElsewhere ? (
                /*
                 * No switch, and no "turn back on" either. This job has no disabled
                 * state: it runs because a reminder asked to be auto-posted, and it stops
                 * because none does. A re-enable control shipped here briefly and was
                 * shown for jobs nobody had ever turned on, because GET synthesizes
                 * `enabled: false` for a job with no row.
                 */
                <div className="text-sm font-medium">{label}</div>
            ) : (
                <label className="flex items-center gap-2 text-sm font-medium">
                    <input
                        type="checkbox"
                        checked={schedule?.enabled ?? false}
                        disabled={toggleDisabled}
                        onChange={(e) => setEnabled(e.target.checked)}
                        className="size-icon-md rounded border-border text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                    />
                    <span>{label}</span>
                </label>
            )}
            {!canEnable && disabledHint ? (
                <p className="pl-6 text-[0.6875rem] text-text-subtle">{disabledHint}</p>
            ) : null}
            <div className="flex flex-wrap items-center gap-2 pl-6 text-sm text-text-muted">
                <span>at</span>
                <input
                    type="time"
                    value={timeValue(schedule?.hourLocal ?? 19, schedule?.minuteLocal ?? 0)}
                    disabled={(!enabledElsewhere && !schedule?.enabled) || mutation.isPending}
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
            {/* Health sits OUTSIDE the enabled gate above, and that is the whole point.
                A job the scheduler switched off has enabled=false and nextRunAt=null, so
                anything sharing that condition renders nothing for precisely the case
                worth showing. */}
            <ScheduleHealth schedule={schedule} />
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

/**
 * The health of a schedule, rendered whether or not it is enabled.
 */
function ScheduleHealth({ schedule }: { schedule: ScheduleView | undefined }) {
    if (!schedule) return null;

    const failures = schedule.consecutiveFailures ?? 0;
    const reason = schedule.disabledReason ?? null;
    const lastError = schedule.lastError ?? null;

    // Three distinct states, because collapsing them is the defect this replaces.
    // A job the scheduler gave up on, a job switched off because its key material
    // went away, and a job someone deliberately turned off all render `enabled:
    // false` and need different responses from the reader.
    if (reason === DISABLED_BY_FAILURES) {
        return (
            <p role="status" className="pl-6 text-[0.6875rem] text-state-danger">
                Switched off automatically after {failures} failed{' '}
                {failures === 1 ? 'run' : 'runs'} — it will not run again until you turn
                it back on.
                {lastError ? <> Last error: {lastError}</> : null}
            </p>
        );
    }

    if (reason === DISABLED_BY_KEY_MATERIAL) {
        return (
            <p role="status" className="pl-6 text-[0.6875rem] text-state-danger">
                Switched off because its stored passphrase could not be opened after a
                restore. Set a new one to turn it back on.
            </p>
        );
    }

    // Enabled and failing, but not yet given up on. The count is the health signal,
    // NOT the presence of lastError: a degraded run leaves a message behind with the
    // count at zero, and badging that red would call a healthy job broken.
    if (failures > 0) {
        return (
            <p role="status" className="pl-6 text-[0.6875rem] text-state-warning">
                {failures} failed {failures === 1 ? 'run' : 'runs'} in a row.
                {lastError ? <> Last error: {lastError}</> : null}
            </p>
        );
    }

    // Ran, did not fail, but reported doing less than it should.
    if (lastError) {
        return (
            <p className="pl-6 text-[0.6875rem] text-text-subtle">
                Last run completed with warnings: {lastError}
            </p>
        );
    }

    // "Last attempted", never "last succeeded". The scheduler stamps last_run_at when
    // it CLAIMS the slot, before the handler runs, so on a failing job this timestamp
    // is fresh and means nothing about success. There is no last_success_at to show
    // instead, so the wording carries the caveat rather than implying one.
    if (schedule.lastRunAt) {
        return (
            <p className="pl-6 text-[0.6875rem] text-text-subtle">
                Last attempted {formatRelative(schedule.lastRunAt)}
            </p>
        );
    }

    return null;
}
