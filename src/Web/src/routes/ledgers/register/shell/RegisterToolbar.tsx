import { Button } from '@/components/ui/Button';

// Shared register primary toolbar (ADR-0030 reuse). The "+ New
// transaction" button + the keyboard-hint chip. Bank shipped the hint
// (`N new · Enter edit selected`); investment had only the button.
// Both registers support N-to-create and Enter-to-edit via
// `useRegisterKeyboardNav`, so the hint is accurate for both — one
// definition keeps them identical.
//
// The button + hint live in `RegisterToolbarContent` (no row chrome)
// so `RegisterControlsBar` can fold them into the same flex row as the
// status-filter tabs. `RegisterToolbar` keeps the standalone row
// wrapper for callers that still want the toolbar on its own line, but
// both registers now render the combined controls bar instead.

export interface RegisterToolbarProps {
    /** Open the new-transaction editor. */
    onNew: () => void;
    /** Disable the New button (e.g. while an editor is already open). */
    disabled: boolean;
    /** Title/tooltip for the New button. Defaults to the bank copy. */
    newButtonTitle?: string;
    /** Show the inline "N new · Enter edit selected" keyboard hint. The dense
     *  combined controls bar sets this false to declutter; the standalone
     *  toolbar keeps it. Defaults to true. */
    showHint?: boolean;
}

/**
 * The "+ New transaction" button + keyboard-hint chip, grouped in a
 * single flex group WITHOUT any row chrome (border / padding). Used
 * by `RegisterControlsBar` so the toolbar folds into the filter row.
 */
export function RegisterToolbarContent({
    onNew,
    disabled,
    newButtonTitle = 'New transaction (N)',
    showHint = true,
}: RegisterToolbarProps) {
    return (
        <div className="flex items-center gap-3">
            <Button
                type="button"
                variant="primary"
                size="sm"
                onClick={onNew}
                disabled={disabled}
                title={newButtonTitle}
            >
                + New transaction
            </Button>
            {showHint ? (
                <span className="text-[0.625rem] text-text-subtle">
                    <kbd className="rounded border border-border bg-surface-muted px-1">N</kbd>
                    {' '}new  ·  <kbd className="rounded border border-border bg-surface-muted px-1">Enter</kbd>
                    {' '}edit selected
                </span>
            ) : null}
        </div>
    );
}

export function RegisterToolbar(props: RegisterToolbarProps) {
    return (
        <div className="flex items-center justify-between gap-3 border-b border-border px-3 py-1.5">
            <RegisterToolbarContent {...props} />
        </div>
    );
}
