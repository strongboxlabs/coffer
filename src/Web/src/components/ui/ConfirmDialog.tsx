import { useEffect, useRef, useState } from 'react';

import { Button } from './Button';
import { Modal } from './Modal';
import { cn } from '@/lib/cn';

// ConfirmDialog — modal confirm for destructive / consequential
// actions. Drop-in replacement for `window.confirm` when the action
// warrants more than a browser-default OK/Cancel chrome (per ADR-0023
// §E: nag dialogs are only warranted for genuinely destructive
// content; this primitive is that lever).
//
// Contract:
//
//   * Controlled visibility — caller renders / unmounts the dialog
//     via `open`. State changes through `onCancel` (Esc, Cancel
//     button, backdrop click) and `onConfirm` (the affirmative
//     button). The component never closes itself.
//   * Keyboard: Esc → onCancel (per ADR-0023 §L); Enter inside the
//     dialog → onConfirm (the affirmative button receives focus on
//     mount so this works naturally).
//   * Click outside the panel → onCancel (per ADR-0023 §L modal
//     dismissal). Pass `dismissOnBackdrop={false}` if the action is
//     critical enough to require explicit Cancel.
//   * Variant: `danger` swaps the Confirm button to the destructive
//     style (red fill); use for delete / drop / revoke flows.
//
// Why not a third-party dialog (Radix etc): same posture as the
// ContextMenu primitive — zero new deps in this PR, hand-roll until
// the count of modal surfaces makes a library justify itself.

export type ConfirmDialogVariant = 'neutral' | 'danger';

export interface ConfirmDialogProps {
    /** Render only when true; unmount when false. */
    open: boolean;
    /** Heading text at the top of the panel. Short and direct
     *  ("Delete 12 transactions?"). */
    title: string;
    /** Optional body — a single string or pre-formatted ReactNode.
     *  Use to explain what will happen + warn about
     *  irreversibility for destructive variants. */
    body?: React.ReactNode;
    /** Label on the affirmative button. Default: "Confirm". */
    confirmLabel?: string;
    /** Label on the dismissive button. Default: "Cancel". */
    cancelLabel?: string;
    /** Visual treatment for the affirmative button. */
    variant?: ConfirmDialogVariant;
    /** Fired when the user picks the affirmative action. The
     *  component does NOT auto-close — the parent decides when to
     *  unmount (commonly: after the mutation completes). */
    onConfirm: () => void;
    /** Fired when the user cancels (Esc, Cancel button, backdrop). */
    onCancel: () => void;
    /** Disable the affirmative button — useful when a mutation is
     *  in flight. */
    confirmDisabled?: boolean;
    /** If false, clicking the backdrop is a no-op. Default true. */
    dismissOnBackdrop?: boolean;
    /** When set, the Confirm button is gated on the user typing this
     *  exact phrase into a confirmation input. Used for bulk-destructive
     *  actions (ADR-0024 — typed confirmation when count > 100) so a
     *  single accidental click can't catastrophically delete N rows.
     *  Comparison is case-insensitive, whitespace-trimmed. */
    requireTypedConfirmation?: string;
    /** When true, swap the Confirm button into a "working…" state with
     *  the affirmative action disabled. Used while a bulk mutation is
     *  in flight to prevent a re-fire on double-click. */
    isConfirming?: boolean;
}

export function ConfirmDialog({
    open,
    title,
    body,
    confirmLabel = 'Confirm',
    cancelLabel = 'Cancel',
    variant = 'neutral',
    onConfirm,
    onCancel,
    confirmDisabled = false,
    dismissOnBackdrop = true,
    requireTypedConfirmation,
    isConfirming = false,
}: ConfirmDialogProps) {
    const confirmRef = useRef<HTMLButtonElement | null>(null);
    const typeInputRef = useRef<HTMLInputElement | null>(null);
    const [typedValue, setTypedValue] = useState('');

    const typedSatisfied =
        requireTypedConfirmation === undefined
        || typedValue.trim().toLowerCase()
            === requireTypedConfirmation.trim().toLowerCase();
    const affirmDisabled = confirmDisabled || isConfirming || !typedSatisfied;

    // Reset typed-confirmation text on close so a subsequent open
    // doesn't show stale input.
    useEffect(() => {
        if (!open) setTypedValue('');
    }, [open]);

    // Focus: when typed confirmation is required, focus the input so the user
    // can start typing immediately; otherwise focus Confirm so Enter commits.
    // (Runs after Modal's default focus — child effect first — so this wins.
    // Esc / backdrop / focus-trap / return-focus are handled by Modal.)
    useEffect(() => {
        if (!open) return;
        if (requireTypedConfirmation !== undefined) {
            typeInputRef.current?.focus();
        } else {
            confirmRef.current?.focus();
        }
    }, [open, requireTypedConfirmation]);

    return (
        <Modal
            open={open}
            onClose={onCancel}
            titleId="confirm-dialog-title"
            dismissOnBackdrop={dismissOnBackdrop}
            className="max-w-md"
        >
            <div className="flex flex-col gap-4 p-5">
                <h2
                    id="confirm-dialog-title"
                    className={cn(
                        'text-base font-semibold',
                        variant === 'danger' ? 'text-state-danger' : 'text-text',
                    )}
                >
                    {title}
                </h2>
                {body ? (
                    <div className="text-sm leading-relaxed text-text-muted">
                        {body}
                    </div>
                ) : null}
                {requireTypedConfirmation !== undefined ? (
                    <label className="flex flex-col gap-1 text-sm">
                        <span className="text-text-muted">
                            Type{' '}
                            <code className="rounded bg-surface-muted px-1 py-0.5 font-mono text-xs text-text">
                                {requireTypedConfirmation}
                            </code>{' '}
                            to confirm:
                        </span>
                        <input
                            ref={typeInputRef}
                            type="text"
                            value={typedValue}
                            onChange={(e) => setTypedValue(e.target.value)}
                            onKeyDown={(e) => {
                                if (e.key === 'Enter' && typedSatisfied && !affirmDisabled) {
                                    e.preventDefault();
                                    onConfirm();
                                }
                            }}
                            autoComplete="off"
                            spellCheck={false}
                            className="rounded border border-border bg-surface px-2 py-1 font-mono text-sm focus:outline-none focus:ring-2 focus:ring-accent"
                        />
                    </label>
                ) : null}
                <div className="flex justify-end gap-2 pt-1">
                    <Button
                        type="button"
                        variant="secondary"
                        size="sm"
                        onClick={onCancel}
                    >
                        {cancelLabel}
                    </Button>
                    <Button
                        ref={confirmRef}
                        type="button"
                        variant={variant === 'danger' ? 'danger' : 'primary'}
                        size="sm"
                        onClick={onConfirm}
                        disabled={affirmDisabled}
                    >
                        {isConfirming ? 'Working…' : confirmLabel}
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
