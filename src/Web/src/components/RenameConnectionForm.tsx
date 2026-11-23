import { useState } from 'react';

import { Button } from '@/components/ui/Button';
import { pendingTextProps } from '@/components/ui/pending';

import type { RenameFormRefs } from './renameFocus';

// RenameConnectionForm — name an MCP connection in place, where it sits in a list.
//
// One copy for Account → Security (a person naming their own tokens and sign-ins) and
// System → MCP (an admin naming anyone's), so the two can never disagree about what a
// blank name does or whether a failure is shown.
//
// Save and Cancel are explicit, unlike the sidebar's InlineNameInput, because a blank
// name here is a real answer for an OAuth connection ("clear it, show the app's own
// name") and InlineNameInput reads a blank as cancel. A token has no such fallback, so
// there a blank name keeps Save disabled.
//
// The parent owns the mutation and passes its state in, so the failure stays on screen
// with what was typed, rather than the form closing as if it had worked. While the
// rename is in flight the whole form is pending (ADR-0023 §L): the field is read-only,
// Cancel refuses a press and Escape is refused, so nothing typed meanwhile is dropped
// by the save's success, and a refusal still has its form to show in. The host hands
// focus on when the form goes (useRenameFocus), through the refs it passes in.

export interface RenameConnectionFormProps extends Partial<RenameFormRefs> {
    /** What the field starts with: the name the owner set, or empty when none is set. */
    initial: string;
    /** Announced to screen readers, since the input has no visible label. */
    label: string;
    /** What shows when the field is empty: the name this falls back to. */
    placeholder: string;
    /** Whether a blank name is allowed (it clears the name); false for a token. */
    allowBlank: boolean;
    pending: boolean;
    /** Why the last save failed, or null. */
    error: string | null;
    onSave: (name: string) => void;
    onCancel: () => void;
}

export function RenameConnectionForm({
    initial,
    label,
    placeholder,
    allowBlank,
    pending,
    error,
    onSave,
    onCancel,
    inputRef,
    saveRef,
    cancelRef,
}: RenameConnectionFormProps) {
    const [draft, setDraft] = useState(initial);
    const blank = draft.trim().length === 0;

    return (
        <form
            className="space-y-1"
            onSubmit={(e) => {
                e.preventDefault();
                if (!pending && (allowBlank || !blank)) onSave(draft.trim());
            }}
        >
            <div className="flex items-center gap-2">
                <input
                    ref={inputRef}
                    autoFocus
                    aria-label={label}
                    className="w-fixed-224px rounded-md border border-border bg-surface px-2 py-1 text-sm text-text disabled:cursor-not-allowed disabled:opacity-50 aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                    value={draft}
                    placeholder={placeholder}
                    onChange={(e) => setDraft(e.target.value)}
                    onKeyDown={(e) => {
                        if (e.key !== 'Escape') return;
                        // A rename in flight refuses the cancel, as its Cancel does.
                        if (pending) {
                            e.preventDefault();
                            return;
                        }
                        onCancel();
                    }}
                    {...pendingTextProps<HTMLInputElement>(pending)}
                />
                <Button ref={saveRef} type="submit" size="sm" disabled={!allowBlank && blank} pending={pending}>
                    Save
                </Button>
                <Button ref={cancelRef} type="button" variant="secondary" size="sm" pending={pending} onClick={onCancel}>
                    Cancel
                </Button>
            </div>
            {error ? (
                <p role="alert" className="text-xs text-state-danger">
                    {error}
                </p>
            ) : null}
        </form>
    );
}
