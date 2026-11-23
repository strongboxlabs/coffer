import { type ReactNode, type RefObject } from 'react';

import { cn } from '@/lib/cn';
import { Modal } from './Modal';

// EditorDialog — the dialog that hosts a transaction editor: the reminder
// occurrence's Post or skip, the reminder series editor and the home screen's
// Review. The overlay, the wide card, a bordered header with the title and a
// close button, and a padded body. The footer belongs to the editor inside
// (TxnRowEdit and InvestmentTxnRowEdit own their Cancel / Save row), so the
// shell owns only the chrome.
//
// The card is a query container, `@container/dialog`, and the editors' dialog
// layout reads it: five columns where they fit, one field per row below
// `@3xl/dialog` (48rem of card). The header follows the same width: below it
// the muted subtitle takes its own line and the close button a 44px target.

export interface EditorDialogProps {
    /** Accessible name for the dialog (role="dialog"). */
    ariaLabel: string;
    /** The title, rendered inside the <h2>. */
    title: ReactNode;
    /** Muted text after the title (date · amount · account). On its own line
     *  when the dialog is narrow. */
    subtitle?: ReactNode;
    onClose: () => void;
    /** Spacing for the body (the series editor uses space-y-4; the occurrence
     *  dialog uses space-y-3). Composed onto the shared padding. */
    bodyClassName?: string;
    /** Where focus goes on close when nothing on the page opened the dialog (one a
     *  link's search param opened), or what did is gone (Modal). */
    fallbackFocusRef?: RefObject<HTMLElement | null>;
    children: ReactNode;
}

export function EditorDialog({
    ariaLabel, title, subtitle, onClose, bodyClassName, fallbackFocusRef, children,
}: EditorDialogProps): React.JSX.Element {
    return (
        <Modal open onClose={onClose} ariaLabel={ariaLabel} className="@container/dialog max-w-5xl" fallbackFocusRef={fallbackFocusRef}>
            <EditorDialogFrame title={title} subtitle={subtitle} onClose={onClose} bodyClassName={bodyClassName}>
                {children}
            </EditorDialogFrame>
        </Modal>
    );
}

/**
 * The dialog's chrome without the overlay: the header and the body. The style
 * guide renders it inline, where an open overlay would cover the page.
 */
export function EditorDialogFrame({
    title, subtitle, onClose, bodyClassName, children,
}: Omit<EditorDialogProps, 'ariaLabel' | 'fallbackFocusRef'>): React.JSX.Element {
    return (
        <>
            <div className="flex items-baseline justify-between gap-3 border-b border-border px-5 py-3 @max-3xl/dialog:items-start @max-3xl/dialog:gap-2 @max-3xl/dialog:px-4">
                <h2 className="text-base font-semibold">
                    {title}
                    {subtitle !== undefined && subtitle !== null ? (
                        <span className="ml-2 text-sm font-normal text-text-muted @max-3xl/dialog:ml-0 @max-3xl/dialog:block">
                            {subtitle}
                        </span>
                    ) : null}
                </h2>
                <button type="button" onClick={onClose} aria-label="Close"
                    className="shrink-0 rounded p-1 text-text-muted hover:bg-surface-hover hover:text-text @max-3xl/dialog:-my-2 @max-3xl/dialog:-mr-3 @max-3xl/dialog:size-control-44px @max-3xl/dialog:p-0">✕</button>
            </div>
            <div className={cn('p-5 @max-3xl/dialog:p-4', bodyClassName)}>{children}</div>
        </>
    );
}
