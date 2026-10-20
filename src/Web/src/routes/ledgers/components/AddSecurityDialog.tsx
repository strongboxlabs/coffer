import { useMutation } from '@tanstack/react-query';

import { ApiError, createSecurity } from '@/lib/api';
import type { CreateSecurityRequest } from '@/lib/types';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { SecurityFormFields, useSecurityForm } from './SecurityForm';

/**
 * Modal form for creating one security in the ledger's catalog.
 *
 * Two callers today:
 *   * Securities catalog page — the user explicitly hits "+ Add
 *     security" to add something brand new.
 *   * Investment editor (`SecurityField` → InvestmentTxnRowEdit) —
 *     the user is mid-accept on an investment transaction and the
 *     security doesn't yet exist in the ledger; the picker's
 *     `+ Create "<ticker>"` row pre-fills `initialTicker` and opens
 *     this dialog without leaving the editor.
 *
 * The fields + gating are shared with the Edit dialog via
 * <see cref="useSecurityForm"/> / <see cref="SecurityFormFields"/>; this
 * shell owns only the Modal chrome and the create mutation. On successful
 * create, `onCreated(securityId)` fires before the dialog closes.
 */
export function AddSecurityDialog({
    ledgerId,
    onClose,
    onCreated,
    initialTicker,
    initialName,
}: {
    ledgerId: string;
    onClose: () => void;
    onCreated: (securityId: string) => void;
    /** Pre-fill the ticker field. Used by the investment editor's
     *  inline `+ Create` flow to carry the typed query in. */
    initialTicker?: string;
    /** Pre-fill the name field. Defaults to the initialTicker if
     *  the caller doesn't have a better starting point. */
    initialName?: string;
}) {
    const form = useSecurityForm({ ticker: initialTicker, name: initialName });

    const createMutation = useMutation({
        mutationFn: (body: CreateSecurityRequest) => createSecurity(ledgerId, body),
        onSuccess: ({ securityId }) => onCreated(securityId),
    });

    const saveDisabled = !form.isValid || createMutation.isPending;

    function handleSubmit(e: React.FormEvent) {
        e.preventDefault();
        if (saveDisabled) return;
        createMutation.mutate(form.buildCreatePayload());
    }

    const errorCode = createMutation.error instanceof ApiError
        ? createMutation.error.code
        : undefined;

    return (
        <Modal open onClose={onClose} titleId="add-security-title" className="max-w-md">
            <form onSubmit={handleSubmit}>
                <header className="border-b border-border px-4 py-3">
                    <h2 id="add-security-title" className="text-base font-semibold">Add security</h2>
                </header>
                <div className="space-y-3 p-4">
                    <SecurityFormFields form={form} errorCode={errorCode} />

                    {createMutation.isError && errorCode === undefined ? (
                        <p role="alert" className="text-xs text-state-danger">
                            {(createMutation.error as Error).message}
                        </p>
                    ) : null}
                </div>
                <footer className="flex justify-end gap-2 border-t border-border bg-surface-muted/30 px-4 py-2">
                    <Button
                        type="button"
                        variant="secondary"
                        size="sm"
                        onClick={onClose}
                        disabled={createMutation.isPending}
                    >
                        Cancel
                    </Button>
                    <Button
                        type="submit"
                        variant="primary"
                        size="sm"
                        disabled={saveDisabled}
                    >
                        {createMutation.isPending ? 'Saving…' : 'Save'}
                    </Button>
                </footer>
            </form>
        </Modal>
    );
}
