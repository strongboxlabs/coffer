import { useState } from 'react';

import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';

/**
 * Name a delimited-file layout — the one dialog behind every "save this as" and
 * "rename this".
 *
 * <b>A dialog rather than a row that appears in place.</b> The wizard used to reveal a
 * name field inside its Layout bar, which had no way of going away again: once shown it
 * sat there through everything else, and the bar changed height under the reader. A
 * dialog opens, is answered or dismissed, and leaves nothing behind.
 *
 * Shared because the settings panel asks the same question about the same things, and
 * a second copy of "what may a layout be called" is how the two drift apart — one
 * checking for a clash while the other lets the unique index answer with a 409.
 *
 * The rule itself belongs to the CALLER: saving a copy may not reuse any existing name,
 * while renaming may keep its own. So the component owns the box and the wiring, and
 * `validateName` owns what is allowed.
 */
export function LayoutNameDialog({
    title, label, initialName, submitLabel, pending, error, validateName, onSubmit, onClose,
}: {
    title: string;
    label: string;
    initialName: string;
    submitLabel: string;
    pending: boolean;
    /** A failure from the write itself, as opposed to a name that was never allowed. */
    error: string | null;
    /** Why this name cannot be used, or null when it can. */
    validateName: (name: string) => string | null;
    onSubmit: (name: string) => void;
    onClose: () => void;
}) {
    const [name, setName] = useState(initialName);
    const trimmed = name.trim();
    // Answered while the name is being typed, rather than as a failed request
    // afterwards — and not while the box is still empty, which is a starting state
    // rather than a mistake.
    const problem = trimmed === '' ? null : validateName(trimmed);

    return (
        <Modal open onClose={onClose} titleId="layout-name-title" className="max-w-sm">
            <form
                className="space-y-3 p-4"
                onSubmit={(e) => { e.preventDefault(); onSubmit(trimmed); }}
            >
                <h2 id="layout-name-title" className="text-sm font-medium">{title}</h2>
                <label className="block">
                    <span className="text-xs text-text-muted">{label}</span>
                    <Input
                        className="mt-1 w-full"
                        value={name}
                        autoFocus
                        placeholder="e.g. Store card statement"
                        onChange={(e) => setName(e.target.value)}
                    />
                </label>
                {problem !== null ? (
                    <p role="alert" className="text-xs text-state-warning">{problem}</p>
                ) : null}
                {error !== null ? (
                    <p role="alert" className="text-xs text-state-danger">{error}</p>
                ) : null}
                <div className="flex justify-end gap-2">
                    <Button type="button" variant="secondary" size="sm" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button
                        type="submit"
                        variant="primary"
                        size="sm"
                        disabled={trimmed === '' || problem !== null || pending}
                    >
                        {pending ? 'Saving…' : submitLabel}
                    </Button>
                </div>
            </form>
        </Modal>
    );
}
