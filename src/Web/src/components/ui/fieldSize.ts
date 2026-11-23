import { createContext, useContext } from 'react';

/**
 * Extra classes for an editor field's control box, from the layout around it.
 *
 * The editors' fields are 28px everywhere: the register's rows set that height.
 * Their dialog layout grows them to touch size when the dialog is narrow, and a
 * field cannot tell which layout it is in, so the layout says it here and each
 * field appends what it gets to its own control's classes. Empty by default, so
 * a field outside such a layout is unchanged.
 *
 * Classes, not a size name, because the switch is a container query on the
 * dialog (`@container/dialog`, components/ui/EditorDialog.tsx): only a class can
 * carry one, and the field must not need to know the container's name.
 */
export const FieldSizeContext = createContext<string>('');

/** The classes the surrounding layout asks this field's control box to add. */
export function useFieldSizeClass(): string {
    return useContext(FieldSizeContext);
}

/**
 * Touch size in a narrow editor dialog: 40px fields with 14px text, as the
 * dialogs' phone layout draws them. Overrides under the container query only,
 * so the 28px base stays the size in a wide dialog.
 */
export const NARROW_DIALOG_FIELD = '@max-3xl/dialog:h-control-40px @max-3xl/dialog:text-sm';
