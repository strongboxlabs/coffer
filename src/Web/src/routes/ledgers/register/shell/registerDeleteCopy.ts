import type { RegisterRow } from '@/lib/types';

/**
 * Shared single-row delete-confirmation copy (review #18). Both
 * register pages showed the SAME manual-vs-feed title / body / CTA
 * for a single-row delete; the wording lives here in one place.
 *
 * BankRegisterPage's combined single+bulk ConfirmDialog reuses this
 * for the single-row branch (its bulk branch has its own copy +
 * typed-confirmation, so that page keeps its own dialog element).
 * InvestmentRegisterPage renders the shared `RegisterDeleteDialog`,
 * which also resolves its copy through here.
 */

export interface DeleteDialogCopy {
    title: string;
    body: string;
    confirmLabel: string;
}

/** Manual entries hard-delete (irreversible); feed / import rows
 *  soft-hide (reversible). Copy flips on `externalId` presence —
 *  the same policy signal the server keys off. */
export function singleDeleteCopy(
    target: RegisterRow,
): DeleteDialogCopy {
    const isManual = target.externalId === null;
    const payee = target.payee ?? '(no payee)';
    return {
        title: isManual
            ? `Delete "${payee}"?`
            : `Hide "${payee}" from the register?`,
        body: isManual
            ? 'This was entered manually. Deleting removes the header and its postings permanently — this cannot be undone.'
            : 'This row was imported or synced from a feed. Hiding removes it from the register; the data stays in the database so a re-import / re-sync keeps it hidden. Reversible via "show hidden" once that surface lands.',
        confirmLabel: isManual ? 'Delete' : 'Hide',
    };
}
