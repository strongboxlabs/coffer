import { useRef, useState, type Ref } from 'react';

import { useFocusHandOff } from './ui/rowRemovalFocus';

/** The refs a host passes to RenameConnectionForm's name field, Save and Cancel. */
export interface RenameFormRefs {
    inputRef: (el: HTMLInputElement | null) => void;
    saveRef: (el: HTMLButtonElement | null) => void;
    cancelRef: (el: HTMLButtonElement | null) => void;
}

/**
 * Where focus goes when a list's rename form goes (ADR-0023 §L): to the Rename
 * button of the row it was open on, which the same render draws in its place.
 *
 * Call it in the component whose render takes the form away (the list's host), so
 * the hand-offs run in that render. Call `opened` with a row's key when its Rename
 * is pressed; give `renameRef(key)` to each row's Rename button and `formRefs` to
 * the form. The row is remembered past the render that closes the form, which no
 * longer says which row it was open on. One hand-off per control of the form, since
 * each tracks one element; focus the reader moved elsewhere stays there.
 */
export function useRenameFocus<K>(): {
    opened: (key: K) => void;
    renameRef: (key: K) => Ref<HTMLButtonElement> | undefined;
    formRefs: RenameFormRefs;
} {
    const [last, setLast] = useState<K | null>(null);
    const rename = useRef<HTMLButtonElement | null>(null);
    const toRename = () => rename.current;
    const inputRef = useFocusHandOff(toRename);
    const saveRef = useFocusHandOff(toRename);
    const cancelRef = useFocusHandOff(toRename);
    return {
        opened: (key: K) => setLast(() => key),
        renameRef: (key: K) => (last !== null && key === last ? rename : undefined),
        formRefs: { inputRef, saveRef, cancelRef },
    };
}
