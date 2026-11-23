import { ConfirmDialog } from '@/components/ui/ConfirmDialog';

/**
 * Asks before an account or a category is deactivated. Reactivating needs no question, since
 * it loses nothing; deactivating changes where the row can be chosen, so it is said first.
 * The error, if the server refuses, is shown in the dialog and the dialog stays.
 */
export function DeactivateConfirm({
    kind,
    name,
    isConfirming,
    error,
    onConfirm,
    onCancel,
}: {
    kind: 'account' | 'category';
    name: string;
    isConfirming: boolean;
    error: string | null;
    onConfirm: () => void;
    onCancel: () => void;
}) {
    return (
        <ConfirmDialog
            open
            variant="danger"
            title={`Deactivate “${name}”?`}
            confirmLabel="Deactivate"
            isConfirming={isConfirming}
            body={
                <>
                    {kind === 'account' ? (
                        <>
                            The account stays in your data and keeps its transactions, but it leaves the
                            pickers and the sidebar until you reactivate it.
                        </>
                    ) : (
                        <>
                            The category stays in your data and keeps its transactions, but it can no
                            longer be chosen for a new transaction until you reactivate it.
                        </>
                    )}{' '}
                    Turn on Show inactive to find it again.
                    {error !== null ? <p role="alert" className="mt-2 text-state-danger">{error}</p> : null}
                </>
            }
            onConfirm={onConfirm}
            onCancel={onCancel}
        />
    );
}
