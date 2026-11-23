import type { ReactNode, Ref } from 'react';

import { Button } from './Button';
import { cn } from '@/lib/cn';

// The one shape for "commit or back out" — ADR-0021 Rule 9.
//
// WHY A PRIMITIVE. Right-aligned Cancel-then-commit held only by habit lets a
// footer go left-aligned because nothing says otherwise. A convention that
// lives in a component cannot drift; one that lives in prose drifts the moment
// someone writes the next footer without reading the last.
//
// WHY PROPS AND NOT `onCancel` / `onSave`. The footers in this app come in four
// shapes, and a two-callback API fits one of them and fights the rest:
//
//     cancel + confirm                 the common case
//     cancel + tertiary + confirm      the import wizard's Back
//     tertiary + confirm               "Import another" / "Done", no cancel
//     a decision pair                  "Deny" / "Allow"
//
// So each slot is optional and the labels are free text. What the primitive
// fixes is the part that should never vary: placement, order, variants, size,
// and the gap. What it leaves open is wording, which is a product decision.
//
// ORDER IS THE POINT. Rendering is tertiary → cancel → confirm regardless of
// prop order, so the commit action is always furthest right and the retreat is
// never where a confident click lands.

interface Slot {
    label: ReactNode;
    onClick?: () => void;
    /** Unavailable: a required field is empty, nothing has changed. */
    disabled?: boolean;
    /** Busy: a request it waits on is in flight. The button keeps focus and
     *  refuses to act (pending.ts, ADR-0023 §L). */
    pending?: boolean;
    /** What it reads while pending ("Saving…"); neither label reads the
     *  pending state itself (PendingLabel, which keeps the button's size). */
    pendingLabel?: ReactNode;
    /** `submit` when the footer sits in a <form> that owns the action. */
    type?: 'button' | 'submit';
}

export interface ActionFooterProps {
    /** The retreat. `secondary` — visible, but never competing with confirm. */
    cancel?: Slot;
    /** The commit. `primary`, or `danger` when it destroys something. */
    confirm?: Slot & { variant?: 'primary' | 'danger' };
    /** A third, lower-weight action: Back, Import another. `ghost`. */
    tertiary?: Slot;
    /**
     * Focus target for the affirmative. ConfirmDialog focuses its confirm
     * button on open, so the primitive has to expose it — otherwise the most
     * safety-critical footer in the app would be the one exception to the rule.
     *
     * A top-level prop rather than `confirm.ref`: a property literally named
     * `ref` on a plain object trips react-hooks/refs ("Cannot access refs
     * during render"), because the rule cannot tell that object apart from a
     * real ref.
     */
    confirmRef?: Ref<HTMLButtonElement>;
    /** Shown left of the buttons — dirty state, validation, a hint. */
    note?: ReactNode;
    /**
     * The footer sits in an editor dialog (EditorDialog's `@container/dialog`
     * card). When the dialog is narrow enough to stack (a phone), its buttons
     * take the 44px touch height the editors' own Cancel and Save take. The
     * size stays the primitive's: this names where the footer is, not a size.
     */
    dialog?: boolean;
    className?: string;
}

/** A button's height in a narrow editor dialog, as the editors size theirs. */
const NARROW_DIALOG_BUTTON = '@max-3xl/dialog:h-control-44px';

export function ActionFooter({
    cancel,
    confirm,
    tertiary,
    confirmRef,
    note,
    dialog = false,
    className,
}: ActionFooterProps) {
    const sized = dialog ? NARROW_DIALOG_BUTTON : undefined;
    return (
        <div className={cn('flex items-center justify-end gap-2', className)}>
            {note !== undefined && note !== null ? (
                <span className="mr-auto text-xs text-text-muted">{note}</span>
            ) : null}
            {tertiary ? (
                <Button
                    type={tertiary.type ?? 'button'}
                    variant="ghost"
                    size="sm"
                    onClick={tertiary.onClick}
                    disabled={tertiary.disabled}
                    pending={tertiary.pending}
                    pendingLabel={tertiary.pendingLabel}
                    className={sized}
                >
                    {tertiary.label}
                </Button>
            ) : null}
            {cancel ? (
                <Button
                    type={cancel.type ?? 'button'}
                    variant="secondary"
                    size="sm"
                    onClick={cancel.onClick}
                    disabled={cancel.disabled}
                    pending={cancel.pending}
                    pendingLabel={cancel.pendingLabel}
                    className={sized}
                >
                    {cancel.label}
                </Button>
            ) : null}
            {confirm ? (
                <Button
                    ref={confirmRef}
                    type={confirm.type ?? 'button'}
                    variant={confirm.variant ?? 'primary'}
                    size="sm"
                    onClick={confirm.onClick}
                    disabled={confirm.disabled}
                    pending={confirm.pending}
                    pendingLabel={confirm.pendingLabel}
                    className={sized}
                >
                    {confirm.label}
                </Button>
            ) : null}
        </div>
    );
}
