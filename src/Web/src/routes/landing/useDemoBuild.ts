import { useCallback, useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';

import { ApiError, fetchDemoJob, startDemoLedger, type DemoJob } from '@/lib/api';
import type { LedgerSummary } from '@/lib/types';
import { errorMessage } from '@/lib/errorMessage';
import { useMutation } from '@/lib/mutation';

import {
    demoFailedText,
    demoInterruptedText,
    isLostBuild,
    lostBuildArrived,
    readDemoNote,
    writeDemoNote,
    type DemoBuildNote,
    type DemoRow,
} from './demoBuild';

export const DEMO_JOB_QUERY_KEY = ['demo-job'] as const;

export interface DemoBuild {
    /** The Demo rows the list shows after the ledgers, in order. */
    rows: readonly DemoRow[];
    /** The list row to leave out: a Demo still adding its examples is shown once, as
     *  its preparing row, although its ledger already exists. */
    hiddenLedgerId: string | null;
    /** A refusal that named no Demo (another import running, or a failure before the
     *  name was chosen): said under the header, with no row. */
    alert: string | null;
    /** Start a new Demo build. */
    start: () => void;
    /** Start again from a failed or interrupted row: the old row is forgotten. */
    retry: () => void;
    pending: boolean;
}

/**
 * The hub's Demo build (ADR-0115 D9). It finds the caller's latest Demo job when the
 * hub opens, polls it each second while it runs, and remembers the build it saw
 * running for the browser session, so a build the server lost to a restart is named
 * as interrupted rather than silently gone.
 */
export function useDemoBuild(ledgersQuery: UseQueryResult<LedgerSummary[]>): DemoBuild {
    const queryClient = useQueryClient();

    const [note, setNoteState] = useState<DemoBuildNote | null>(readDemoNote);
    const noteRef = useRef(note);
    const setNote = useCallback((next: DemoBuildNote | null) => {
        noteRef.current = next;
        setNoteState(next);
        writeDemoNote(next);
    }, []);

    // Each answer about the job updates the note: a running job is followed, and one
    // seen finished is forgotten (its own row, or the list, now says what happened).
    const follow = useCallback((job: DemoJob | null) => {
        const current = noteRef.current;
        if (job?.state === 'running') {
            if (current?.jobId === job.jobId && current.lastState === 'running') return;
            const ledgerIdsAtStart = current?.jobId === job.jobId
                ? current.ledgerIdsAtStart
                : (queryClient.getQueryData<LedgerSummary[]>(['ledgers']) ?? []).map((l) => l.id);
            setNote({ jobId: job.jobId, ledgerName: job.ledgerName, ledgerIdsAtStart, lastState: 'running' });
        } else if (job !== null && current?.jobId === job.jobId) {
            setNote(null);
        }
    }, [queryClient, setNote]);

    const jobQuery = useQuery({
        queryKey: DEMO_JOB_QUERY_KEY,
        queryFn: async () => {
            const job = await fetchDemoJob();
            follow(job);
            return job;
        },
        // Asked afresh whenever the hub opens: this is how it finds a build again
        // after the user went elsewhere.
        staleTime: 0,
        refetchInterval: (query) => (query.state.data?.state === 'running' ? 1000 : false),
    });
    const job = jobQuery.data ?? null;

    const startMutation = useMutation({
        mutationFn: () => startDemoLedger(),
        onSuccess: async (started) => {
            // An answer to a lookup already in flight could predate this job.
            await queryClient.cancelQueries({ queryKey: DEMO_JOB_QUERY_KEY });
            queryClient.setQueryData(DEMO_JOB_QUERY_KEY, started);
            follow(started);
        },
    });
    const { mutate } = startMutation;
    const start = useCallback(() => mutate(), [mutate]);
    const retry = useCallback(() => {
        setNote(null);
        mutate();
    }, [mutate, setNote]);

    // The job this hub watched run: only its end is held back until the list shows it.
    const [watchedJobId, setWatchedJobId] = useState<string | null>(null);
    const runningJobId = job?.state === 'running' ? job.jobId : null;
    useEffect(() => {
        if (runningJobId !== null) setWatchedJobId(runningJobId);
    }, [runningJobId]);

    // The Demo's ledger is listed once its import has committed, and listed with its
    // final state once it has succeeded.
    const ledgerReady = job !== null && (job.state === 'succeeded' || job.phase === 'examples');
    const succeededJobId = job?.state === 'succeeded' ? job.jobId : null;
    const [listReadFor, setListReadFor] = useState<string | null>(null);
    useEffect(() => {
        if (!ledgerReady) return;
        let live = true;
        void queryClient.invalidateQueries({ queryKey: ['ledgers'] }).then(() => {
            if (live && succeededJobId !== null) setListReadFor(succeededJobId);
        });
        return () => {
            live = false;
        };
    }, [ledgerReady, job?.jobId, job?.state, succeededJobId, queryClient]);

    // A build that just succeeded keeps its row until the list it is reloading shows the
    // ledger, so the hub never shows the list without it in between (on an empty hub,
    // the empty state). Once that read has settled the row goes, whatever it found.
    const holdingEnd = succeededJobId !== null
        && watchedJobId === succeededJobId
        && listReadFor !== succeededJobId
        && !(ledgersQuery.data ?? []).some((l) => l.id === job?.ledgerId);

    // A build the server no longer holds may have finished while the hub was closed,
    // so the list is read again before the build is called interrupted: only a list
    // read after the build was lost can say its ledger never came.
    const lostJobId = jobQuery.isSuccess && isLostBuild(note, job) ? note.jobId : null;
    const [listCheckedFor, setListCheckedFor] = useState<string | null>(null);
    useEffect(() => {
        if (lostJobId === null) return;
        let live = true;
        void queryClient.invalidateQueries({ queryKey: ['ledgers'] }).then(() => {
            if (live) setListCheckedFor(lostJobId);
        });
        return () => {
            live = false;
        };
    }, [lostJobId, queryClient]);

    const arrived = lostJobId !== null && note !== null && lostBuildArrived(note, ledgersQuery.data ?? []);
    useEffect(() => {
        if (arrived) setNote(null);
    }, [arrived, setNote]);

    const interrupted = lostJobId !== null && !arrived && ledgersQuery.isSuccess && listCheckedFor === lostJobId;

    const error = startMutation.error;
    const failedName = error instanceof ApiError ? error.ledgerName : undefined;

    const rows: DemoRow[] = [];
    if (failedName !== undefined) {
        // One sentence whatever the reason (ADR-0115 D9): the server logs why, the refusal
        // for lack of memory included, and the sentence points there.
        rows.push({ kind: 'failed', key: 'start-failed', name: failedName, message: demoFailedText(failedName) });
    } else if (interrupted && note !== null) {
        rows.push({ kind: 'interrupted', key: `lost-${note.jobId}`, name: note.ledgerName, message: demoInterruptedText(note.ledgerName) });
    }
    if (job?.state === 'running' || (job !== null && holdingEnd)) {
        rows.push({
            kind: 'preparing', key: job.jobId, name: job.ledgerName,
            stepNumber: job.stepNumber, total: job.total, stepLabel: job.stepLabel,
        });
    } else if (job?.state === 'failed' && failedName === undefined) {
        rows.push({ kind: 'failed', key: job.jobId, name: job.ledgerName, message: job.error ?? demoFailedText(job.ledgerName) });
    }

    return {
        rows,
        hiddenLedgerId: job?.state === 'running' || holdingEnd ? (job?.ledgerId ?? null) : null,
        alert: error !== null && failedName === undefined
            ? errorMessage(error, 'The Demo could not be started. Try again, or check the server logs.')
            : null,
        start,
        retry,
        pending: startMutation.isPending,
    };
}
