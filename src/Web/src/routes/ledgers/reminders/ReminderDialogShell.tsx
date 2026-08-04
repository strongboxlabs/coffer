import { type ReactNode } from 'react';
import { cn } from '@/lib/cn';
import { Modal } from '@/components/ui/Modal';

// Shared modal scaffolding for the reminder dialogs (ADR-0051): the overlay,
// the centered dialog card, the bordered header (a title node + close button),
// and escape-to-close. Extracted verbatim from ReminderEditorDialog and
// ReminderOccurrenceModal, which had been duplicating it. The footer stays
// inside each dialog's embedded editor (TxnRowEdit / InvestmentTxnRowEdit own
// their own Cancel / Save action row), so the shell owns only the chrome.
//
// Deliberately NOT an app-wide primitive: ConfirmDialog hand-rolls its own
// (smaller) chrome and is out of scope for this slice.
export function ReminderDialogShell({
    ariaLabel, title, onClose, bodyClassName, children,
}: {
    /** Accessible name for the dialog (role="dialog"). */
    ariaLabel: string;
    /** Header content rendered inside the <h2>: the title plus any muted
     *  trailing label (account / date / amount). */
    title: ReactNode;
    onClose: () => void;
    /** Spacing for the body (the editor uses space-y-4; the occurrence modal
     *  uses space-y-3). Composed onto the shared `p-5` padding. */
    bodyClassName?: string;
    children: ReactNode;
}): React.JSX.Element {
    return (
        <Modal open onClose={onClose} ariaLabel={ariaLabel} className="max-w-5xl">
            <div className="flex items-baseline justify-between gap-3 border-b border-border px-5 py-3">
                <h2 className="text-base font-semibold">{title}</h2>
                <button type="button" onClick={onClose} aria-label="Close"
                    className="rounded p-1 text-text-muted hover:bg-surface-hover hover:text-text">✕</button>
            </div>
            <div className={cn('p-5', bodyClassName)}>{children}</div>
        </Modal>
    );
}
