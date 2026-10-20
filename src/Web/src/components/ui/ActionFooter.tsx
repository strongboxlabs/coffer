import type { ReactNode, Ref } from 'react';

import { Button } from './Button';
import { cn } from '@/lib/cn';

// The one shape for "commit or back out" — ADR-0021 Rule 9.
//
// WHY A PRIMITIVE. The convention was real and widely followed (22 footers
// across 16 files, nearly all already right-aligned Cancel-then-commit) but it
// was held by habit. The Appearance panel shipped left-aligned Apply/Cancel
// because nothing said otherwise, and review did not catch it. A convention
// that lives in a component cannot drift; one that lives in prose drifts the
// moment someone writes the 26th footer without reading the 25th.
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
    disabled?: boolean;
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
    className?: string;
}

export function ActionFooter({
    cancel,
    confirm,
    tertiary,
    confirmRef,
    note,
    className,
}: ActionFooterProps) {
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
                >
                    {confirm.label}
                </Button>
            ) : null}
        </div>
    );
}
