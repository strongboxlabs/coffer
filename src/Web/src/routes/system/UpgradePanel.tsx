import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMutation } from '@/lib/mutation';

import {
    UPGRADE_CONFIRM_PHRASE,
    UPGRADE_QUERY_KEY,
    checkForUpgrade,
    clearUpgradeToken,
    fetchUpgradeStatus,
    preflightUpgrade,
    saveUpgradeSettings,
    saveUpgradeToken,
    startUpgrade,
} from '@/lib/api';
import { ApiError } from '@/lib/api/_request';
import { formatLedgerDateTime } from '@/lib/dates';
import { errorMessage } from '@/lib/errorMessage';
import { formatRelative } from '@/lib/ledgerOperationDisplay';
import type { UpgradeAttempt, UpgradeSettings, UpgradeStatus } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { Checkbox } from '@/components/ui/Checkbox';
import { Input } from '@/components/ui/Input';
import { Panel, PanelBody } from '@/components/ui/Panel';
import { PendingScope } from '@/components/ui/pending';
import { useFocusHandOff } from '@/components/ui/rowRemovalFocus';
import { cn } from '@/lib/cn';
import { SYSTEM_ATTENTION_KEY } from '@/lib/useSystemAttention';

/** How often the status is read while an upgrade is under way. The API goes down for part of every upgrade. */
const POLL_MS = 3000;

const isActive = (attempt: UpgradeAttempt | null | undefined): boolean =>
    attempt?.phase === 'backing-up' || attempt?.phase === 'requested' || attempt?.phase === 'started';

/** A path or word safe to paste into a shell as typed, quoted only when it has to be. */
function shellWord(value: string): string {
    return /^[\w@%+=:,./-]+$/.test(value) ? value : `'${value.replace(/'/g, `'\\''`)}'`;
}

/** `bash <install dir>/<script> [arg]`, with the install directory the server reported. */
function hostCommand(installDir: string, script: string, arg?: string): string {
    const dir = installDir.length > 0 ? installDir.replace(/\/+$/, '') : 'INSTALL_DIR';
    const path = installDir.length > 0 ? shellWord(`${dir}/${script}`) : `${dir}/${script}`;
    return `bash ${path}${arg ? ` ${arg}` : ''}`;
}

/**
 * Admin Upgrade tab (ADR-0110). Shows whether a newer release exists, starts the upgrade, and follows
 * it through the restart it causes.
 *
 * The upgrade recreates the API container, so for part of every upgrade nothing answers this page. A
 * failed poll during an active attempt is therefore expected, not an error: the last answer stays on
 * screen, the page says it is waiting, and it keeps asking. When the new process answers it has lost
 * its memory of the attempt and rebuilds it from the host's last word, so a reload mid-upgrade lands
 * on the right state too.
 */
export function UpgradePanel({ onOpenBackups }: { onOpenBackups: () => void }) {
    const queryClient = useQueryClient();
    const query = useQuery({
        queryKey: UPGRADE_QUERY_KEY,
        queryFn: fetchUpgradeStatus,
        retry: false,
        // `data` survives a failed refetch, so an attempt seen as active keeps being polled through the restart.
        refetchInterval: (q) => (isActive(q.state.data?.attempt) ? POLL_MS : false),
    });
    const status = query.data;
    // Every answer this page gets (a check, a setting, a start) can change what the gear and the
    // tabs say, so they are asked again: a release found by Check now must light the dot at once,
    // exactly as one found by the automatic check does.
    const accept = (next: UpgradeStatus) => {
        queryClient.setQueryData(UPGRADE_QUERY_KEY, next);
        void queryClient.invalidateQueries({ queryKey: SYSTEM_ATTENTION_KEY });
    };
    // A finish is seen by polling, not by an answer to a click, so `accept` never runs for it. The
    // install is a different version now, so the gear and the tabs are asked again, or "Update
    // available" would stay lit beside a result that says it is done.
    const seen = `${status?.installed ?? ''}|${status?.attempt?.phase ?? ''}`;
    useEffect(() => {
        if (seen.endsWith('|done')) void queryClient.invalidateQueries({ queryKey: SYSTEM_ATTENTION_KEY });
    }, [seen, queryClient]);

    return (
        <section className="space-y-3">
            <header className="space-y-1">
                <h2 className="text-base font-semibold">Upgrade</h2>
                <p className="text-sm text-text-muted">
                    Upgrade this install to the latest release. The new version runs on the host, so
                    Coffer is unavailable for a few minutes while it swaps.
                </p>
            </header>

            {query.isPending ? (
                <Panel><PanelBody><p className="text-sm text-text-subtle">Loading…</p></PanelBody></Panel>
            ) : status === undefined ? (
                <Panel className="border-state-danger/40 bg-state-danger-soft">
                    <PanelBody>
                        <p role="alert" className="text-sm text-state-danger">
                            {errorMessage(query.error, 'Could not load the upgrade status.')}
                        </p>
                    </PanelBody>
                </Panel>
            ) : (
                <>
                    <UpgradeCard
                        status={status}
                        reconnecting={query.isError && isActive(status.attempt)}
                        lastAnswerAt={query.dataUpdatedAt}
                        onStatus={accept}
                        onOpenBackups={onOpenBackups}
                    />
                    <UpgradeSettingsCard status={status} onStatus={accept} />
                </>
            )}
        </section>
    );
}

function UpgradeCard({
    status, reconnecting, lastAnswerAt, onStatus, onOpenBackups,
}: {
    status: UpgradeStatus;
    reconnecting: boolean;
    lastAnswerAt: number;
    onStatus: (next: UpgradeStatus) => void;
    onOpenBackups: () => void;
}) {
    // A failed attempt that changed nothing can be put away to try again. Keyed on its start so a new
    // attempt is never hidden by an earlier dismissal.
    const [dismissed, setDismissed] = useState<string | null>(null);
    const attempt = status.attempt;
    const shown = attempt !== null && attempt.startedAtUtc !== dismissed ? attempt : null;
    // Whether this page has seen an upgrade under way. Set while rendering, as it only ever
    // goes from false to true, so the frame that finishes the upgrade already knows.
    const [watched, setWatched] = useState(false);
    if (!watched && isActive(attempt)) setWatched(true);
    // Whether this page has seen the host blocked. The page that watched a failure keeps its card
    // only until the host stops saying blocked after having said it, which is `ack` having been run.
    const [sawBlocked, setSawBlocked] = useState(false);
    if (!sawBlocked && status.host.state === 'blocked') setSawBlocked(true);

    if (shown !== null && isActive(shown)) {
        return <ProgressCard attempt={shown} reconnecting={reconnecting} lastAnswerAt={lastAnswerAt} installDir={status.installDir} />;
    }
    // A failure past the first change is shown to everyone while the host still refuses new
    // upgrades (it says so in its heartbeat, and clears that on `ack`), and otherwise to the page that
    // watched it. It never hides the release card: Check now stays, and the upgrade button says why it is off.
    const needsAttention = shown?.phase === 'failed' && shown.changed
        && (status.host.state === 'blocked' || (watched && !sawBlocked));
    // A failure that changed nothing is news for the page that watched it. Another visit sees the
    // release card and nothing about a run that did no harm.
    const didNotStart = shown?.phase === 'failed' && !shown.changed && watched;

    return (
        <>
            {needsAttention && shown !== null ? (
                <Panel className="border-state-danger/40 bg-state-danger-soft">
                    <PanelBody className="space-y-2">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                            <p role="alert" className="text-sm font-semibold text-state-danger">Needs attention</p>
                            <Pill tone="danger">This install was changed</Pill>
                        </div>
                        <p className="text-sm text-text">
                            The upgrade to {shown.to} failed after it began changing this install. Nothing was retried or rolled back.
                        </p>
                        {shown.message ? <p className="text-xs text-text-muted">{shown.message}</p> : null}
                        <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1 text-xs">
                            <dt className="text-text-muted">Backup</dt>
                            <dd>before upgrade {shown.from} to {shown.to} (kept)</dd>
                            <dt className="text-text-muted">Check on the host</dt>
                            <dd><Command>{hostCommand(status.installDir, 'app-upgrades.sh', 'status')}</Command></dd>
                            <dt className="text-text-muted">When you have dealt with it</dt>
                            <dd><Command>{hostCommand(status.installDir, 'app-upgrades.sh', 'ack')}</Command></dd>
                        </dl>
                        <p className="text-xs text-text-muted">
                            Rollback is a restore of that backup on the release it came from. Changes made after the backup are lost.
                        </p>
                        <Button type="button" variant="secondary" size="sm" onClick={onOpenBackups}>Open Backups</Button>
                    </PanelBody>
                </Panel>
            ) : null}
            {didNotStart && shown !== null ? (
                <Panel className="border-state-warning/40 bg-state-warning-soft">
                    <PanelBody className="space-y-2">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                            <p role="alert" className="text-sm font-semibold text-state-warning">
                                The upgrade to {shown.to} did not start
                            </p>
                            <Pill tone="warn">Nothing was changed</Pill>
                        </div>
                        {shown.message ? <p className="text-sm text-text">{shown.message}</p> : null}
                        <p className="text-xs text-text-muted">
                            The backup went back to normal retention. To see why:{' '}
                            <Command inline>{hostCommand(status.installDir, 'app-upgrades.sh', 'status')}</Command>
                        </p>
                        <Button type="button" variant="secondary" size="sm" onClick={() => setDismissed(shown.startedAtUtc)}>Dismiss</Button>
                    </PanelBody>
                </Panel>
            ) : null}
            {/* The result of a finished upgrade is the page's answer to the progress card it
                was just showing, so it appears only for a page that watched it run. The release
                card says the same afterwards, with the installed version, and always shows, so
                Check now is never hidden behind an old result. */}
            {shown?.phase === 'done' && watched ? (
                <Panel className="border-state-success/40 bg-state-success-soft">
                    <PanelBody className="space-y-2">
                        <div className="flex items-baseline justify-between gap-3">
                            <p role="status" className="text-sm font-semibold text-state-success">
                                {shown.message ?? `Coffer is now on ${shown.to}.`}
                            </p>
                            <span className="text-xs text-text-muted">{formatRelative(shown.startedAtUtc)}</span>
                        </div>
                        <p className="text-xs text-text-muted">
                            Upgraded from {shown.from}. The backup taken for it is kept, and Coffer keeps the newest two upgrade backups.
                        </p>
                        {/* This page loaded before the upgrade, so it is still the old version of the app.
                            Reloading brings the new one in, so it is the first thing offered. */}
                        {__APP_VERSION__ !== shown.to ? (
                            <p className="text-xs text-text">This page is still the old version. Reload it to use {shown.to}.</p>
                        ) : null}
                        <div className="flex flex-wrap gap-2">
                            {__APP_VERSION__ !== shown.to ? (
                                <Button type="button" size="sm" onClick={() => window.location.reload()}>Reload</Button>
                            ) : null}
                            <Button type="button" variant="secondary" size="sm" onClick={onOpenBackups}>Open Backups</Button>
                        </div>
                    </PanelBody>
                </Panel>
            ) : null}
            <ReleaseCard status={status} onStatus={onStatus} failureAbove={needsAttention} />
        </>
    );
}

function ReleaseCard({
    status, onStatus, failureAbove,
}: { status: UpgradeStatus; onStatus: (next: UpgradeStatus) => void; failureAbove: boolean }) {
    const [phrase, setPhrase] = useState('');
    const [error, setError] = useState<string | null>(null);

    const check = useMutation({
        mutationFn: checkForUpgrade,
        onSuccess: (next) => { setError(null); onStatus(next); },
        onError: (err) => setError(errorMessage(err, 'Could not check for a release.')),
    });
    const start = useMutation({
        // Everything the server can refuse without a passkey is asked first, so a stale screen, a
        // blocked host or a missing backup passphrase never costs a tap. Start asks again, as time passes.
        mutationFn: async (version: string) => {
            await preflightUpgrade(version, phrase);
            return startUpgrade(version, phrase);
        },
        onSuccess: (next) => { setError(null); setPhrase(''); onStatus(next); },
        // Covers a refused start and a dismissed passkey prompt alike. The server has just read the
        // release and looked at the host, so the screen is brought up to what it saw: a newer release
        // replaces the stale one, and the button follows the reason it is off.
        onError: async (err, version) => {
            setError(errorMessage(err, 'The upgrade did not start.'));
            try {
                const fresh = await fetchUpgradeStatus();
                onStatus(fresh);
                if (err instanceof ApiError && err.code === 'upgrade-nothing-newer'
                    && fresh.updateAvailable && fresh.latest !== null && fresh.latest.version !== version) {
                    setError(`${fresh.latest.version} is now the latest release, and the page shows it. Check it, then press Upgrade again.`);
                }
            } catch {
                // The refusal stands as it was worded; the next Check now reads it again.
            }
        },
    });

    const latest = status.latest;
    const phraseOk = phrase.trim().toLowerCase() === UPGRADE_CONFIRM_PHRASE;
    const canStart = status.canStart && !failureAbove && latest !== null && phraseOk;

    return (
        <>
            <Panel>
                <PanelBody className="space-y-3">
                    <div className="flex flex-wrap items-start justify-between gap-3">
                        <div className="space-y-1.5">
                            {status.updateAvailable && latest !== null ? (
                                <>
                                    <Pill tone="accent">Update available</Pill>
                                    <p className="font-mono">
                                        <span className="text-sm text-text-muted">{status.installed}</span>
                                        <span className="px-2 text-text-subtle" aria-hidden="true">→</span>
                                        <span className="text-xl font-semibold">{latest.version}</span>
                                    </p>
                                </>
                            ) : (
                                <>
                                    <Pill tone={latest === null ? 'neutral' : 'success'}>
                                        {latest === null ? 'Not checked yet' : 'Up to date'}
                                    </Pill>
                                    <p className="font-mono text-xl font-semibold">{status.installed}</p>
                                </>
                            )}
                            <p className="text-xs text-text-muted">
                                {status.checkedAt
                                    ? `Last checked ${formatRelative(status.checkedAt)}`
                                    : 'Press Check now to look for a release.'}
                                {status.updateAvailable && latest?.publishedAt
                                    ? ` · published ${formatLedgerDateTime(latest.publishedAt)}`
                                    : ''}
                                {status.updateAvailable && latest !== null && latest.url.startsWith('https://github.com/')
                                    ? <> · <a className="text-accent underline" href={latest.url} target="_blank" rel="noopener noreferrer">Release page on GitHub</a></>
                                    : null}
                            </p>
                        </div>
                        <Button type="button" variant="secondary" size="sm" onClick={() => check.mutate()} pending={check.isPending}
                            pendingLabel="Checking…">
                            Check now
                        </Button>
                    </div>

                    {status.checkError ? (
                        <p role="alert" className="text-xs text-state-danger">
                            The last check failed: {status.checkError}
                        </p>
                    ) : null}

                    {status.updateAvailable && latest !== null && latest.notes.length > 0 ? (
                        <details open={latest.notes.length <= 600} className="text-xs">
                            <summary className="cursor-pointer text-text-muted">Release notes</summary>
                            {/* Plain text, escaped by React: this page sits in front of a privileged action. */}
                            <pre className="mt-1 max-h-fixed-160px overflow-auto whitespace-pre-wrap rounded border border-border bg-surface-muted p-2 font-mono text-xs text-text-muted">
                                {latest.notes}
                            </pre>
                        </details>
                    ) : null}
                </PanelBody>
            </Panel>

            {status.updateAvailable && latest !== null ? (
                <Panel>
                    <PanelBody className="space-y-3">
                        <p className="text-sm font-medium">What happens when you upgrade</p>
                        <ol className="list-decimal space-y-1.5 pl-5 text-sm text-text-muted">
                            <li>
                                You confirm with your passkey. Coffer then takes a fresh backup and reads it back to check it.
                                The backup is labelled "before upgrade {status.installed} to {latest.version}".
                            </li>
                            <li>
                                The host swaps in the new version. Coffer is unavailable for a few minutes, longer if a
                                migration is large. This page reconnects by itself.
                            </li>
                            <li>
                                The result appears here. If the upgrade fails after it began changing this install, nothing is
                                rolled back for you. You restore the labelled backup, and you lose changes made after it.
                            </li>
                        </ol>
                        <p className="text-xs text-text-subtle">
                            A checked backup can be read back. That does not prove it restores.
                        </p>

                        {status.canStart && !failureAbove ? (
                            <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-3">
                                <label htmlFor="upgrade-phrase" className="flex flex-wrap items-center gap-2 text-sm text-text-muted">
                                    <span>Type <code className="font-mono">{UPGRADE_CONFIRM_PHRASE}</code> to confirm</span>
                                    {/* Pending while the start is in flight: its success clears
                                        the phrase, which would drop anything typed meanwhile
                                        (ADR-0023 §L). */}
                                    <Input
                                        id="upgrade-phrase"
                                        pending={start.isPending}
                                        value={phrase}
                                        onChange={(e) => setPhrase(e.target.value)}
                                        autoComplete="off"
                                        spellCheck={false}
                                        className="h-control-36px w-fixed-176px font-mono"
                                    />
                                </label>
                                <Button type="button" onClick={() => start.mutate(latest.version)} disabled={!canStart} pending={start.isPending}
                                    pendingLabel="Confirm on your passkey…">
                                    {`Upgrade to ${latest.version}`}
                                </Button>
                            </div>
                        ) : (
                            <CannotStart status={status} failureAbove={failureAbove} />
                        )}

                        {error !== null ? <p role="alert" className="text-xs text-state-danger">{error}</p> : null}
                    </PanelBody>
                </Panel>
            ) : error !== null ? (
                <p role="alert" className="text-xs text-state-danger">{error}</p>
            ) : null}
        </>
    );
}

/** Why the button is off, in the server's words, and the host command that fixes it where there is one. */
function CannotStart({ status, failureAbove }: { status: UpgradeStatus; failureAbove: boolean }) {
    const reason = status.cannotStartReason
        ?? (failureAbove
            ? 'An earlier upgrade failed after it began changing this install. Look at that first, with the commands above.'
            : 'Upgrading from the app is not available.');
    const fixable = status.hostNote === '' && status.inAppEnabled && (status.host.state === 'disabled' || status.host.state === 'stale');
    return (
        <div className="space-y-2 border-t border-border pt-3">
            <p role="status" className="text-sm text-state-warning">{reason}</p>
            {status.hostNote !== '' ? <p className="text-xs text-text-muted">{status.hostNote}</p> : null}
            {fixable ? (
                <div className="space-y-1">
                    <p className="text-xs text-text-muted">
                        {status.host.state === 'disabled' ? 'To turn it on:' : 'Check that cron is running. This repairs a missing entry:'}
                    </p>
                    <Command>{hostCommand(status.installDir, 'app-upgrades.sh', 'enable')}</Command>
                </div>
            ) : null}
            {status.host.state === 'blocked' && !failureAbove ? (
                <div className="space-y-1">
                    <p className="text-xs text-text-muted">When you have looked at what failed:</p>
                    <Command>{hostCommand(status.installDir, 'app-upgrades.sh', 'ack')}</Command>
                </div>
            ) : null}
            {!status.inAppEnabled ? (
                <p className="text-xs text-text-muted">Switch it on under Settings below.</p>
            ) : null}
            <div className="space-y-1">
                <p className="text-xs text-text-muted">Or upgrade by hand on the host:</p>
                <Command>{hostCommand(status.installDir, 'upgrade.sh')}</Command>
            </div>
            <Button type="button" size="sm" disabled title={reason}>
                {`Upgrade to ${status.latest?.version ?? 'the latest release'}`}
            </Button>
        </div>
    );
}

type StepState = 'done' | 'now' | 'pending';

/** The steps of an attempt in progress. Each is read off the phase the server reports. */
function steps(phase: UpgradeAttempt['phase']): Array<{ state: StepState; text: ReactNode }> {
    const backingUp = phase === 'backing-up';
    const requested = phase === 'requested';
    return [
        { state: 'done', text: 'Passkey confirmed' },
        {
            state: backingUp ? 'now' : 'done',
            text: backingUp ? 'Taking a backup and checking it' : 'Backup taken and checked',
        },
        {
            state: backingUp ? 'pending' : requested ? 'now' : 'done',
            text: requested ? 'Handed to the host, waiting for it to pick the request up' : 'Request handed to the host',
        },
        {
            state: backingUp || requested ? 'pending' : 'now',
            text: (
                <>
                    <strong>The host is upgrading.</strong> Coffer restarts on the new version, then runs its migrations.
                </>
            ),
        },
    ];
}

function ProgressCard({
    attempt, reconnecting, lastAnswerAt, installDir,
}: { attempt: UpgradeAttempt; reconnecting: boolean; lastAnswerAt: number; installDir: string }) {
    return (
        <>
            <Panel>
                <PanelBody className="space-y-3">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                        <div className="flex flex-wrap items-center gap-3">
                            <Pill tone="warn">Upgrading</Pill>
                            <span className="font-mono text-sm">
                                <span className="text-text-muted">{attempt.from}</span>
                                <span className="px-2 text-text-subtle" aria-hidden="true">→</span>
                                <span className="font-semibold">{attempt.to}</span>
                            </span>
                        </div>
                        <span className="text-xs text-text-muted">Started {formatRelative(attempt.startedAtUtc)}</span>
                    </div>
                    <ul className="space-y-2" aria-label="Upgrade progress">
                        {steps(attempt.phase).map((step, i) => (
                            <li key={i} className={cn('flex items-start gap-2.5 text-sm', step.state === 'pending' && 'text-text-subtle')}>
                                <StepMark state={step.state} />
                                <span>{step.text}</span>
                            </li>
                        ))}
                    </ul>
                    <p className="text-xs text-text-muted">
                        Leaving this page does not stop the upgrade. Coffer may be unavailable for several minutes.
                    </p>
                </PanelBody>
            </Panel>
            {reconnecting ? (
                <Panel className="border-state-warning/40 bg-state-warning-soft">
                    <PanelBody className="space-y-2">
                        <p role="status" className="text-sm font-semibold text-state-warning">Waiting for Coffer to restart</p>
                        <p className="text-xs text-text">
                            No answer since {formatRelative(new Date(lastAnswerAt).toISOString())}. That is expected during a
                            restart. This page tries again every few seconds and shows the result when Coffer answers. To
                            follow it on the host:
                        </p>
                        <Command>{hostCommand(installDir, 'app-upgrades.sh', 'status')}</Command>
                    </PanelBody>
                </Panel>
            ) : null}
        </>
    );
}

function StepMark({ state }: { state: StepState }) {
    if (state === 'done') {
        return (
            <span className="mt-0.5 inline-flex size-icon-md flex-none items-center justify-center rounded-full bg-state-success text-[0.625rem] leading-none text-surface">
                <span className="sr-only">Done: </span>✓
            </span>
        );
    }
    return state === 'now' ? (
        <span className="mt-0.5 size-icon-md flex-none animate-spin rounded-full border-[1.5px] border-accent border-t-transparent motion-reduce:animate-none motion-reduce:border-t-accent">
            <span className="sr-only">In progress: </span>
        </span>
    ) : (
        <span className="mt-0.5 size-icon-md flex-none rounded-full border-[1.5px] border-text-subtle">
            <span className="sr-only">Waiting: </span>
        </span>
    );
}

function UpgradeSettingsCard({ status, onStatus }: { status: UpgradeStatus; onStatus: (next: UpgradeStatus) => void }) {
    const queryClient = useQueryClient();
    // No success message: a switch flips once the save has gone through, the token's status and field
    // change, and the other settings panels confirm nothing either. Only a failure needs saying.
    const [error, setError] = useState<string | null>(null);
    const [token, setToken] = useState('');
    // A stored token clears the field, which leaves Save token disabled with
    // focus on it when it was pressed; focus goes to the token field it waits
    // on (ADR-0023 §L).
    const tokenRef = useRef<HTMLInputElement>(null);
    // A removed token takes Remove away once the status is read again, with
    // focus on it: focus goes to the token field, which stays (ADR-0023 §L).
    const removeHandOff = useFocusHandOff(() => tokenRef.current);

    const save = useMutation({
        mutationFn: (next: UpgradeSettings) => saveUpgradeSettings(next),
        onSuccess: (_void, next) => {
            setError(null);
            // The switches change what the status says (whether it may start), so read it again.
            onStatus({ ...status, ...next });
            queryClient.invalidateQueries({ queryKey: UPGRADE_QUERY_KEY });
        },
        onError: (err) => setError(errorMessage(err, 'Could not save the setting.')),
    });
    const saveTokenMutation = useMutation({
        mutationFn: () => saveUpgradeToken(token),
        onSuccess: () => {
            // Cleared at once: the token is a credential and is write-only, so the field is the only place it lingers.
            setToken('');
            setError(null);
            queryClient.invalidateQueries({ queryKey: UPGRADE_QUERY_KEY });
        },
        onError: (err) => setError(errorMessage(err, 'Could not store the token.')),
    });
    const clearTokenMutation = useMutation({
        mutationFn: clearUpgradeToken,
        onSuccess: () => {
            setError(null);
            queryClient.invalidateQueries({ queryKey: UPGRADE_QUERY_KEY });
        },
        onError: (err) => setError(errorMessage(err, 'Could not remove the token.')),
    });

    const current: UpgradeSettings = {
        inAppEnabled: status.inAppEnabled, autoCheck: status.autoCheck, privateRepo: status.privateRepo,
    };
    const busy = save.isPending;

    return (
        <section className="space-y-2">
            <h3 className="text-sm font-semibold">Settings</h3>
            <Panel>
                <PanelBody className="space-y-3">
                    {/* Each setting saves all three, built from the last read:
                        one group (pending.ts), so a change of any holds them all
                        until its save has landed, and a second change never puts
                        the first back. */}
                    <PendingScope>
                    <SettingRow
                        id="upgrade-in-app"
                        checked={status.inAppEnabled}
                        pending={busy}
                        onChange={(v) => save.mutate({ ...current, inAppEnabled: v })}
                        title="Allow upgrades from this app"
                        hint="When off, this tab only reports. Upgrading is then by hand on the host."
                    />
                    <SettingRow
                        id="upgrade-auto-check"
                        checked={status.autoCheck}
                        pending={busy}
                        onChange={(v) => save.mutate({ ...current, autoCheck: v })}
                        title="Check for new releases automatically"
                        hint="Every few hours, while an admin is using the app, from any page, so that an available release can show on the System gear. This sends a request to GitHub's API. Nothing is sent while it is off, except when you press Check now."
                    />
                    <SettingRow
                        id="upgrade-private-repo"
                        checked={status.privateRepo}
                        pending={busy}
                        onChange={(v) => save.mutate({ ...current, privateRepo: v })}
                        title="This install comes from a private fork"
                        hint="Needs a repository token for the check, and the same token on the host."
                    />
                    </PendingScope>

                    {status.privateRepo ? (
                        <div className="space-y-2 pl-6">
                            <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1 text-xs">
                                <dt className="text-text-muted">Repository</dt>
                                <dd className="font-mono">{status.repository}</dd>
                                <dt className="text-text-muted">Token</dt>
                                <dd>
                                    {status.tokenStored ? (
                                        <><Pill tone="success">Stored</Pill>{' '}<span className="text-text-muted">Sealed with the master key. Never shown again.</span></>
                                    ) : (
                                        <span className="text-state-warning">Not stored. The check cannot read a private fork without it.</span>
                                    )}
                                </dd>
                            </dl>
                            <form
                                className="flex flex-wrap items-center gap-2"
                                onSubmit={(e) => { e.preventDefault(); if (token.trim() !== '') saveTokenMutation.mutate(); }}
                            >
                                <label htmlFor="upgrade-token" className="text-xs text-text-muted">New token</label>
                                {/* Pending while the token is stored: the save's success
                                    clears the field (ADR-0023 §L). */}
                                <Input
                                    ref={tokenRef}
                                    id="upgrade-token"
                                    type="password"
                                    pending={saveTokenMutation.isPending}
                                    value={token}
                                    onChange={(e) => setToken(e.target.value)}
                                    autoComplete="off"
                                    spellCheck={false}
                                    className="h-control-36px w-fixed-256px font-mono"
                                />
                                <Button type="submit" variant="secondary" size="sm" disabled={token.trim() === ''} pending={saveTokenMutation.isPending}
                                    fallbackFocusRef={tokenRef} pendingLabel="Saving…">
                                    Save token
                                </Button>
                                {status.tokenStored ? (
                                    <Button ref={removeHandOff} type="button" variant="ghost" size="sm" onClick={() => clearTokenMutation.mutate()} pending={clearTokenMutation.isPending}>
                                        Remove
                                    </Button>
                                ) : null}
                            </form>
                            <p className="text-xs text-text-muted">
                                The host needs it too: <Command inline>{hostCommand(status.installDir, 'app-upgrades.sh', 'set-token')}</Command>
                            </p>
                        </div>
                    ) : null}

                    <div className="flex items-center justify-between gap-3 border-t border-border pt-3 text-sm">
                        <span className="font-medium">Host updater</span>
                        <HostPill host={status.host} />
                    </div>

                    {error !== null ? <p role="alert" className="text-xs text-state-danger">{error}</p> : null}
                </PanelBody>
            </Panel>
        </section>
    );
}

function SettingRow({
    id, checked, pending, onChange, title, hint,
}: {
    id: string; checked: boolean;
    /** A save is in flight: the checkbox keeps focus and refuses to change (pending.ts). */
    pending: boolean;
    onChange: (value: boolean) => void; title: string; hint: string;
}) {
    return (
        <div className="flex items-start gap-2">
            <Checkbox
                id={id}
                checked={checked}
                pending={pending}
                onChange={(e) => onChange(e.target.checked)}
                className="mt-0.5"
            />
            <label htmlFor={id} className="flex flex-col text-sm">
                <span className="font-medium">{title}</span>
                <span className="text-xs text-text-muted">{hint}</span>
            </label>
        </div>
    );
}

function HostPill({ host }: { host: UpgradeStatus['host'] }) {
    switch (host.state) {
        case 'running':
            return <Pill tone="success">Running{host.at ? ` · answered ${formatRelative(host.at)}` : ''}</Pill>;
        case 'disabled':
            return <Pill tone="warn">Disabled on the host</Pill>;
        case 'stale':
            return <Pill tone="warn">Not responding{host.at ? ` · last answered ${formatRelative(host.at)}` : ''}</Pill>;
        case 'blocked':
            return <Pill tone="danger">Blocked after a failed upgrade · run ack</Pill>;
        case 'unpinned':
            return <Pill tone="warn">Cannot tell the installed version</Pill>;
        default:
            return <Pill tone="neutral">Never answered</Pill>;
    }
}

type PillTone = 'neutral' | 'success' | 'warn' | 'danger' | 'accent';

const PILL_TONES: Record<PillTone, string> = {
    neutral: 'bg-chip-neutral text-chip-neutral-text',
    success: 'bg-state-success-soft text-state-success',
    warn: 'bg-state-warning-soft text-state-warning',
    danger: 'bg-state-danger-soft text-state-danger',
    accent: 'bg-accent-soft text-accent-soft-text',
};

function Pill({ tone, children }: { tone: PillTone; children: ReactNode }) {
    return (
        <span className={cn('inline-flex items-center self-start rounded-full px-2 py-0.5 text-xs font-semibold', PILL_TONES[tone])}>
            {children}
        </span>
    );
}

/** A command to copy, selectable as one piece. Block by default, inline inside a sentence. */
function Command({ children, inline = false }: { children: ReactNode; inline?: boolean }) {
    return (
        <code className={cn(
            'select-all rounded border border-border bg-surface-muted px-2 py-1 font-mono text-xs text-text',
            inline ? 'inline-block' : 'block overflow-x-auto whitespace-nowrap',
        )}>
            {children}
        </code>
    );
}
