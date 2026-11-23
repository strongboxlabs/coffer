import { FieldLabel } from '@/components/ui/FieldLabel';

/**
 * The caption over a Memo or Tags field that may show the other side's value.
 *
 * While the side inherits, the caption says where the shown value comes from
 * ("from" and the other side's account); typing or picking gives this side its
 * own. Otherwise it is just the label, as on any other field.
 *
 * It offers no control to go back to the other side's value: emptying the
 * field is the way back, and the caption then says where the value comes from
 * again.
 */
export function NotesFieldCaption({
    htmlFor,
    fromId,
    label,
    otherSide,
    inheriting,
}: {
    htmlFor: string;
    /** Id of the "from X" text, for the field's `aria-describedby`: the
     *  placeholder alone does not tell a screen reader the value is the other
     *  side's. Rendered only while inheriting. */
    fromId?: string;
    label: string;
    /** The account the inherited value comes from; null when it is not known. */
    otherSide: string | null;
    inheriting: boolean;
}) {
    return (
        <span className="flex min-w-0 items-baseline justify-between gap-2">
            <FieldLabel htmlFor={htmlFor}>{label}</FieldLabel>
            {inheriting ? (
                <span id={fromId} className="truncate text-[0.625rem] font-medium leading-[1.5] text-text-subtle">
                    from {otherSide ?? 'the other side'}
                </span>
            ) : null}
        </span>
    );
}
