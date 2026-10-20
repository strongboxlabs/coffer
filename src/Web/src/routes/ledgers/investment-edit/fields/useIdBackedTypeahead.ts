import { useEffect, useState } from 'react';

/**
 * Small shared hook for fields that wrap `<Typeahead>` but expose an
 * **id** (not text) to the parent. Each picker field (Security,
 * Category, Transfer, FeeCategory) holds the typed input text
 * locally and emits the resolved id up via `onChangeId`. When the
 * external id changes (e.g., edit-mode populating from an existing
 * txn), the local text re-syncs.
 *
 * Resolution rule: a row "matches" when its label (case-insensitive)
 * exactly equals the typed text. The Typeahead primitive's
 * suggestion-pick handler writes the picked label into `value`, so
 * an exact match is the post-pick state.
 *
 * Centralizing this here keeps each field component simple
 * (~50 lines) and means the text-vs-id resolution rule is defined
 * in one place — easy to adjust if (e.g.) we later want fuzzy
 * matching or auto-create on unmatched text.
 */
export function useIdBackedTypeahead<T extends { id: string }>(args: {
    items: readonly T[];
    getLabel: (item: T) => string;
    valueId: string | null;
    onChangeId: (next: string | null) => void;
}): {
    text: string;
    onTextChange: (next: string) => void;
} {
    const { items, getLabel, valueId, onChangeId } = args;

    const [text, setText] = useState(() => {
        if (valueId === null) return '';
        const initial = items.find((i) => i.id === valueId);
        return initial ? getLabel(initial) : '';
    });

    // Sync local text when the parent-owned id changes from outside
    // (edit-mode load, prefill, reset). Unconditional: the parent
    // changing valueId is intent to overwrite, even mid-typing.
    useEffect(() => {
        if (valueId === null) {
            if (text !== '') setText('');
            return;
        }
        const match = items.find((i) => i.id === valueId);
        const newText = match ? getLabel(match) : '';
        if (newText !== text) setText(newText);
        // We intentionally depend on valueId only here. We don't
        // depend on `text` to avoid a re-resolve every keystroke
        // (which would clobber partial typing).
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [valueId]);

    // Late-arriving catalog: when the editor opens with a populated
    // valueId BEFORE `items` has finished loading (e.g. opening the
    // investment editor right after a route load — the securities
    // catalog is still in flight), the first-render `useState`
    // initializer above resolves to '' and the valueId-only effect
    // above never re-runs. This effect catches that race: when
    // `items` changes and the local text is still the unresolved
    // empty placeholder, resolve it now. The `text === ''` guard
    // prevents items-driven reruns (e.g. a new security created
    // elsewhere mid-edit) from clobbering live input.
    useEffect(() => {
        if (text !== '' || valueId === null) return;
        const match = items.find((i) => i.id === valueId);
        if (match) setText(getLabel(match));
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [items]);

    function onTextChange(next: string) {
        setText(next);
        const needle = next.trim().toLowerCase();
        if (needle.length === 0) {
            onChangeId(null);
            return;
        }
        const match = items.find((i) => getLabel(i).toLowerCase() === needle);
        onChangeId(match?.id ?? null);
    }

    return { text, onTextChange };
}
