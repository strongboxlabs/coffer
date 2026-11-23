/** What a tag merge reported back, named for the confirmation the Tags panel shows. */
export interface MergeSummary {
    from: string;
    into: string;
    assignmentsMoved: number;
    /** Saved reports the merging user can see that now filter on `into`. Another
     *  member's private report is repointed too, and never counted here. */
    savedReportsRepointed: number;
}

/** The confirmation line after a merge: what moved, and which saved reports followed. */
export function mergeSummaryText(s: MergeSummary): string {
    const moved = `${s.assignmentsMoved} tag assignment${s.assignmentsMoved === 1 ? '' : 's'} moved`;
    const n = s.savedReportsRepointed;
    const reports = n > 0
        ? `; ${n} saved report${n === 1 ? ' now filters' : 's now filter'} on “${s.into}”`
        : '';
    return `Merged “${s.from}” into “${s.into}”: ${moved}${reports}.`;
}
