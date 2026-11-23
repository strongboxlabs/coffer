import type { ReactNode } from 'react';

/**
 * A button's label that reads `pendingLabel` while its request is pending
 * ("Save" → "Saving…"), in a box that never changes size (ADR-0023 §L,
 * pending.ts). Both labels are laid out from the first render, in one grid
 * cell, so the box is the wider of the two at rest and while pending alike:
 * neither the button nor a neighbour moves when the label swaps, and the
 * second press of a double click lands on the button the first one pressed.
 * The label not shown is invisible and hidden from assistive technology, so
 * the button's accessible name is the label shown.
 *
 * Button and an ActionFooter slot use it through `pendingLabel`; a raw button
 * puts it in as its content. So does a Button pending with its group whose
 * label swaps only for its own request: the Button's `pending` is the group's,
 * this component's `pending` is the button's own request. A label that reads
 * in stages while pending (starting, then waiting on the authenticator) nests
 * a PendingLabel as its `pendingLabel`, whose `pending` is the later stage, so
 * every stage is laid out from the first render too.
 *
 * Each label is laid out as the button lays out its content, in a row with the
 * button's own gap, so a label of an icon and text keeps that gap between them.
 */
export function PendingLabel({
    pending,
    label,
    pendingLabel,
}: {
    /** The button's request is in flight. */
    pending: boolean;
    /** What the button reads at rest. */
    label: ReactNode;
    /** What it reads while pending. */
    pendingLabel: ReactNode;
}) {
    return (
        <span className="inline-grid gap-[inherit]">
            <span className={layer(!pending)} aria-hidden={pending ? true : undefined}>{label}</span>
            <span className={layer(pending)} aria-hidden={pending ? undefined : true}>{pendingLabel}</span>
        </span>
    );
}

/** One label in the shared cell: shown, or holding its width while invisible. */
function layer(shown: boolean): string {
    const base = 'col-start-1 row-start-1 flex items-center justify-center gap-[inherit]';
    return shown ? base : `${base} invisible`;
}
