import { useId } from 'react';
import { AlertTriangle } from 'lucide-react';

import type { ImportJob, ImportSkippedTransaction, ImportWarning } from '@/lib/api/import';
import { formatShares } from '@/lib/money';

/**
 * The failed checks of a finished import, one box each, shown on the wizard's
 * last step (ADR-0071 D2). The ledger is already committed: these say what the
 * import could not bring in or found wrong, so the user can fix it in Moneydance
 * and import again.
 */

const DROPPED = 'no-dropped-transactions';

/** A plain title per check the importer runs; an unknown check shows its name. */
const CHECK_TITLES: Record<string, string> = {
    'account-name-uniqueness': 'Duplicate account names',
    'investment-action-conformance': 'Investment transactions with an action Coffer could not use',
    'header-balance': 'Transactions that do not balance',
    'header-has-legs': 'Transactions with no lines',
    'originating-account': 'Transactions without an owning account',
    'lots-reference-existing-legs': 'Tax lots pointing at missing lines',
    'account-count-parity': 'Accounts missing after the import',
    'security-types-recognised': 'Securities with a type Coffer does not recognise',
};

/** A plain label per reason the importer skips a transaction; an unknown reason shows as named. */
const REASON_LABELS: Record<string, string> = {
    UnknownSecurity: 'Security not in the export',
    UnknownShape: 'Action Coffer cannot place',
    UnknownBrokerageAccount: 'Investment account not in the export',
    BrokerageMissingHoldingsSibling: 'Investment account Coffer could not set up',
    UnknownXferAccount: 'Transfer account not in the export',
    UnknownIncomeOrFeeCategory: 'Income or fee category not in the export',
    NotInvestmentTxn: 'Not an investment transaction',
    // The bank route's reasons (TransactionMapper.SkipReason, and the FITID dedup).
    UnknownPrimaryAccount: 'Account not in the export',
    UnknownSplitAccount: 'Category or account not in the export',
    NoSplits: 'No lines',
    DuplicateFitid: 'Same bank download as another transaction',
};

const DASH = '—';

function plural(count: number, one: string, many: string): string {
    return `${count.toLocaleString()} ${count === 1 ? one : many}`;
}

/**
 * The warning status line. It renders inside the wizard's live region, which stays
 * mounted from the start of the run, so the change from running to this is announced.
 */
export function ImportWarningsStatus({ job }: { job: ImportJob }) {
    return (
        <p className="flex items-center gap-2 text-sm font-medium text-state-warning">
            <AlertTriangle className="size-icon-lg" aria-hidden />
            Import complete, with {plural(job.warnings.length, 'warning', 'warnings')}.
        </p>
    );
}

/**
 * A box per failed check, and where else they are kept. The not-imported box comes
 * first: it is the one the user acts on, whatever order the checks ran in.
 */
export function ImportWarnings({ job }: { job: ImportJob }) {
    const ordered = [
        ...job.warnings.filter((w) => w.check === DROPPED),
        ...job.warnings.filter((w) => w.check !== DROPPED),
    ];
    return (
        <>
            <section aria-label="Import warnings" className="space-y-3">
                {ordered.map((w) =>
                    w.check === DROPPED ? (
                        <DroppedBox
                            key={w.check}
                            rows={job.skippedTransactions}
                            notListed={job.skippedTransactionsNotListed}
                        />
                    ) : (
                        <CheckBox key={w.check} warning={w} />
                    ),
                )}
            </section>
            <p className="text-xs text-text-muted">
                Warnings stay on this screen only. They are also written to the server log.
            </p>
        </>
    );
}

const BOX = 'space-y-1.5 rounded border border-state-warning/40 bg-state-warning-soft px-3 py-2';
const TITLE = 'text-sm font-semibold text-state-warning';

function CheckBox({ warning }: { warning: ImportWarning }) {
    return (
        <div className={BOX}>
            <h2 className={TITLE}>{CHECK_TITLES[warning.check] ?? warning.check}</h2>
            <p className="text-xs text-text">{warning.message}</p>
        </div>
    );
}

function DroppedBox({ rows, notListed }: { rows: ImportSkippedTransaction[]; notListed: number }) {
    const total = rows.length + notListed;
    const titleId = useId();
    return (
        <div className={BOX}>
            <h2 id={titleId} className={TITLE}>
                {plural(total, 'transaction was not imported', 'transactions were not imported')}
            </h2>
            <p className="text-xs text-text">
                They are still in your Moneydance file. Fix them there, then import the export again
                into a new ledger. You can delete this ledger afterwards.
            </p>
            <div className="max-h-fixed-344px overflow-auto rounded border border-border bg-surface">
                <table aria-labelledby={titleId} className="w-full text-xs">
                    <thead className="sticky top-0">
                        <tr className="border-b border-border bg-surface-header text-[0.625rem] uppercase tracking-wider text-text-muted">
                            <th className="px-3 py-2 text-left">Date</th>
                            <th className="px-3 py-2 text-left">Security</th>
                            <th className="px-3 py-2 text-right">Shares</th>
                            <th className="px-3 py-2 text-left">Why</th>
                        </tr>
                    </thead>
                    <tbody>
                        {rows.map((r, i) => (
                            <tr key={i} className="border-b border-border last:border-b-0">
                                <td className="whitespace-nowrap px-3 py-1.5 tabular-nums">{r.date ?? DASH}</td>
                                <td className="whitespace-nowrap px-3 py-1.5">{securityLabel(r)}</td>
                                <td className="whitespace-nowrap px-3 py-1.5 text-right font-mono tabular-nums">
                                    {r.shares === null ? DASH : formatShares(r.shares)}
                                </td>
                                <td className="whitespace-nowrap px-3 py-1.5">{REASON_LABELS[r.reason] ?? r.reason}</td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>
            {notListed > 0 ? (
                <p className="text-xs text-text-muted">
                    {plural(notListed, 'more transaction', 'more transactions')} not listed.
                </p>
            ) : null}
        </div>
    );
}

function securityLabel(r: ImportSkippedTransaction): string {
    if (r.ticker && r.security) return `${r.ticker} · ${r.security}`;
    return r.ticker || r.security || DASH;
}
