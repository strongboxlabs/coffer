import { useQuery } from '@tanstack/react-query';

import { fetchCurrentUser, fetchSystemAttention } from '@/lib/api';
import type { SystemAttention } from '@/lib/types';
import type { AttentionSeverity } from '@/lib/useLedgerAttention';

/**
 * What the INSTALLATION needs — the deployment-scope counterpart of `useLedgerAttention`
 * (ADR-0102, amended for ADR-0110). One source behind the System-settings gear and the System tabs.
 *
 * An available upgrade is one subject among them, drawn by the same dot as a failed backup: the same
 * outline for a warning, the same fill for critical, one dot per surface at the highest severity
 * beneath it. Its own module, for the reason `useLedgerAttention` gives: a file that exports both
 * components and non-components breaks react-refresh's boundary, and the rail renders on every page.
 */

/** The query key, shared so an action that changes the answer can ask for it again. */
export const SYSTEM_ATTENTION_KEY = ['system-attention'] as const;

/** The subject an available release is announced under (`upgrade.available`). */
const UPGRADE_SUBJECT = 'upgrade';

function toSeverity(value: string | null | undefined): AttentionSeverity | null {
    return value === 'critical' || value === 'warning' ? value : null;
}

export interface SystemAttentionView {
    /** Beneath the gear: the worst of EVERYTHING outstanding, the upgrade included. */
    gear: AttentionSeverity | null;
    /** The Upgrade tab: the upgrade subject, and nothing else. */
    upgrade: AttentionSeverity | null;
    /**
     * The Notifications tab: everything outstanding EXCEPT the upgrade. The Upgrade tab carries that
     * alone, so one subject never lights two tabs.
     */
    problems: AttentionSeverity | null;
    /** Distinct subjects outstanding other than the upgrade. */
    problemCount: number;
    upgradeVersion: string | null;
    isPending: boolean;
    /** The read failed: no surface may say "nothing to report" from it. */
    isError: boolean;
    /** The sentence for the gear's label, naming what is outstanding; null when nothing is. */
    gearSentence: string | null;
}

function worstOf(subjects: SystemAttention['subjects']): AttentionSeverity | null {
    if (subjects.some((s) => s.severity === 'critical')) return 'critical';
    if (subjects.some((s) => s.severity === 'warning')) return 'warning';
    return null;
}

/**
 * Admins only. The route is RequireAdmin, so for anyone else the request is never made: the gear
 * stays as it was, and nothing is asked of the server on their behalf.
 */
export function useSystemAttention(): SystemAttentionView {
    const me = useQuery({ queryKey: ['me'], queryFn: fetchCurrentUser });
    const isAdmin = me.data?.isAdmin === true;

    const query = useQuery({
        queryKey: SYSTEM_ATTENTION_KEY,
        queryFn: fetchSystemAttention,
        enabled: isAdmin,
        // Rides along with every navigation, and what it reports does not change by the minute.
        // Refetching on focus keeps it honest after an upgrade or a repair without polling.
        staleTime: 60_000,
    });

    const subjects = isAdmin ? (query.data?.subjects ?? []) : [];
    const upgradeSubjects = subjects.filter((s) => s.subject === UPGRADE_SUBJECT);
    const otherSubjects = subjects.filter((s) => s.subject !== UPGRADE_SUBJECT);

    const upgrade = worstOf(upgradeSubjects);
    const problems = worstOf(otherSubjects);
    const gear = toSeverity(isAdmin ? query.data?.worst : null) ?? worstOf(subjects);
    const upgradeVersion = isAdmin ? (query.data?.upgradeVersion ?? null) : null;

    return {
        gear,
        upgrade,
        problems,
        problemCount: otherSubjects.length,
        upgradeVersion,
        isPending: isAdmin && query.isPending,
        isError: isAdmin && query.isError,
        gearSentence: sentenceFor(otherSubjects.length, upgrade !== null, upgradeVersion),
    };
}

/** What is outstanding, in words, for anyone who cannot see a colour. */
export function sentenceFor(
    problemCount: number,
    upgradeAvailable: boolean,
    upgradeVersion: string | null,
): string | null {
    const problems = problemCount > 0
        ? `${problemCount} problem${problemCount === 1 ? '' : 's'} outstanding on this install`
        : null;
    const update = upgradeAvailable
        ? (upgradeVersion ? `Update available: ${upgradeVersion}` : 'Update available')
        : null;
    if (problems && update) return `${problems}, and ${update.charAt(0).toLowerCase()}${update.slice(1)}`;
    return problems ?? update;
}
